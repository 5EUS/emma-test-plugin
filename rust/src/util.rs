//! Small helpers with no dependencies: percent-encoding and RFC 3339 timestamps.

use std::fmt::Write as _;

/// Percent-encodes a query-string component (everything except unreserved characters).
#[must_use]
pub fn encode(text: &str) -> String {
    let mut encoded = String::with_capacity(text.len());
    for byte in text.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'~') {
            encoded.push(char::from(byte));
        } else {
            let _ = write!(encoded, "%{byte:02X}");
        }
    }
    encoded
}

/// Parses an RFC 3339 timestamp such as `2021-05-02T10:11:12+00:00` or `...Z` into milliseconds
/// since the Unix epoch. Returns `None` for anything malformed.
#[must_use]
pub fn parse_rfc3339_ms(text: &str) -> Option<i64> {
    let bytes = text.as_bytes();
    if bytes.len() < 20
        || bytes[4] != b'-'
        || bytes[7] != b'-'
        || !matches!(bytes[10], b'T' | b't' | b' ')
        || bytes[13] != b':'
        || bytes[16] != b':'
    {
        return None;
    }
    let number = |range: std::ops::Range<usize>| text.get(range)?.parse::<i64>().ok();
    let (year, month, day) = (number(0..4)?, number(5..7)?, number(8..10)?);
    let (hour, minute, second) = (number(11..13)?, number(14..16)?, number(17..19)?);
    if !(1..=12).contains(&month)
        || !(1..=31).contains(&day)
        || hour > 23
        || minute > 59
        || second > 60
    {
        return None;
    }

    let mut index = 19;
    let mut millis = 0_i64;
    if bytes.get(index) == Some(&b'.') {
        let digits_end = (index + 1..bytes.len())
            .find(|i| !bytes[*i].is_ascii_digit())
            .unwrap_or(bytes.len());
        let fraction = text.get(index + 1..digits_end)?;
        millis = format!("{fraction:0<3}")[..3].parse().ok()?;
        index = digits_end;
    }
    let offset_seconds = match bytes.get(index)? {
        b'Z' | b'z' if index + 1 == bytes.len() => 0,
        sign @ (b'+' | b'-') if bytes.len() == index + 6 && bytes[index + 3] == b':' => {
            let hours = number(index + 1..index + 3)?;
            let minutes = number(index + 4..index + 6)?;
            let total = hours * 3600 + minutes * 60;
            if *sign == b'+' { total } else { -total }
        }
        _ => return None,
    };

    let days = days_from_civil(year, month, day);
    Some(((days * 86_400 + hour * 3600 + minute * 60 + second - offset_seconds) * 1000) + millis)
}

/// Days since 1970-01-01 for a proleptic Gregorian date (Howard Hinnant's algorithm).
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = year.div_euclid(400);
    let year_of_era = year.rem_euclid(400);
    let shifted_month = (month + 9) % 12;
    let day_of_year = (153 * shifted_month + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encoding_keeps_unreserved_and_escapes_the_rest() {
        assert_eq!(encode("one piece"), "one%20piece");
        assert_eq!(encode("a&b=c/d?é"), "a%26b%3Dc%2Fd%3F%C3%A9");
        assert_eq!(encode("safe-_.~09azAZ"), "safe-_.~09azAZ");
    }

    #[test]
    fn timestamps_parse_across_offsets_and_fractions() {
        assert_eq!(parse_rfc3339_ms("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(
            parse_rfc3339_ms("2021-05-02T10:11:12+00:00"),
            Some(1_619_950_272_000)
        );
        assert_eq!(
            parse_rfc3339_ms("2021-05-02T12:11:12+02:00"),
            Some(1_619_950_272_000)
        );
        assert_eq!(
            parse_rfc3339_ms("2021-05-02T05:41:12-04:30"),
            Some(1_619_950_272_000)
        );
        assert_eq!(
            parse_rfc3339_ms("2021-05-02T10:11:12.5Z"),
            Some(1_619_950_272_500)
        );
        assert_eq!(
            parse_rfc3339_ms("2000-02-29T00:00:00Z"),
            Some(951_782_400_000)
        );
        assert_eq!(parse_rfc3339_ms("1969-12-31T23:59:59Z"), Some(-1000));
    }

    #[test]
    fn malformed_timestamps_are_none() {
        for bad in [
            "",
            "yesterday",
            "2021-13-01T00:00:00Z",
            "2021-05-02",
            "2021-05-02T10:11:12",
            "2021-05-02T10:11:12+0000",
            "2021-05-32T00:00:00Z",
            "2021-05-02T25:00:00Z",
        ] {
            assert_eq!(parse_rfc3339_ms(bad), None, "{bad}");
        }
    }
}
