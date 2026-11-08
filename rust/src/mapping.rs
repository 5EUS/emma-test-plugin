//! Turns MangaDex JSON into plain structs. No Wasm types here, so it is unit-tested natively.

use serde_json::Value;

use crate::util::parse_rfc3339_ms;

/// A manga as the plugin needs it.
#[derive(Debug, Clone, PartialEq)]
pub struct Manga {
    /// The MangaDex id.
    pub id: String,
    /// The best title for the preferred language.
    pub title: String,
    /// The description for the preferred language.
    pub description: Option<String>,
    /// `ongoing`, `completed`, `hiatus`, `cancelled`.
    pub status: Option<String>,
    /// The year it began.
    pub year: Option<i64>,
    /// `safe`, `suggestive`, `erotica`, `pornographic`.
    pub content_rating: Option<String>,
    /// The publication demographic.
    pub demographic: Option<String>,
    /// Tag names.
    pub tags: Vec<String>,
    /// Author and artist names.
    pub creators: Vec<String>,
    /// The cover image URL.
    pub cover_url: Option<String>,
}

/// A chapter of a manga.
#[derive(Debug, Clone, PartialEq)]
pub struct Chapter {
    /// The MangaDex chapter id.
    pub id: String,
    /// The chapter label such as `12.5`.
    pub number: Option<String>,
    /// The volume label.
    pub volume: Option<String>,
    /// The chapter's own title.
    pub title: Option<String>,
    /// The number of pages.
    pub pages: i64,
    /// When it was published, ms since the Unix epoch.
    pub published_ms: Option<i64>,
    /// Scanlation group names.
    pub groups: Vec<String>,
    /// The translated language.
    pub language: Option<String>,
}

/// Where a chapter's page images are served from.
#[derive(Debug, Clone, PartialEq)]
pub struct AtHome {
    /// The image server's base URL.
    pub base_url: String,
    /// The chapter hash.
    pub hash: String,
    /// The path segment: `data` (original) or `data-saver`.
    pub segment: &'static str,
    /// Image file names in reading order.
    pub files: Vec<String>,
}

/// Rating statistics for a manga.
#[derive(Debug, Clone, PartialEq)]
pub struct Statistics {
    /// The MangaDex id.
    pub id: String,
    /// The Bayesian rating, 0 to 10.
    pub rating: Option<f64>,
    /// How many users follow it.
    pub follows: Option<i64>,
}

/// A page of results with the paging fields MangaDex returns.
#[derive(Debug, Clone, PartialEq)]
pub struct Listing<T> {
    /// The items.
    pub items: Vec<T>,
    /// The total number available.
    pub total: i64,
    /// The offset this page started at.
    pub offset: i64,
}

fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value
        .get(key)?
        .as_str()
        .map(str::trim)
        .filter(|s| !s.is_empty())
}

/// Picks a localized string: the preferred language, then English, then any.
#[must_use]
pub fn pick_localized(map: &Value, preferred: &str) -> Option<String> {
    let object = map.as_object()?;
    [preferred, "en"]
        .iter()
        .filter_map(|language| object.get(*language))
        .chain(object.values())
        .filter_map(Value::as_str)
        .map(str::trim)
        .find(|s| !s.is_empty())
        .map(str::to_owned)
}

fn relationships<'a>(item: &'a Value, kind: &'a str) -> impl Iterator<Item = &'a Value> {
    item.get("relationships")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(move |r| r.get("type").and_then(Value::as_str) == Some(kind))
}

fn listing<T>(root: &Value, map: impl Fn(&Value) -> Option<T>) -> Listing<T> {
    Listing {
        items: root
            .get("data")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter_map(map)
            .collect(),
        total: root.get("total").and_then(Value::as_i64).unwrap_or(0),
        offset: root.get("offset").and_then(Value::as_i64).unwrap_or(0),
    }
}

/// Reads a `/manga` response.
#[must_use]
pub fn manga_listing(root: &Value, language: &str) -> Listing<Manga> {
    listing(root, |item| manga(item, language))
}

fn manga(item: &Value, language: &str) -> Option<Manga> {
    let id = text(item, "id")?.to_owned();
    let attributes = item.get("attributes")?;
    let title = attributes
        .get("title")
        .and_then(|t| pick_localized(t, language))?;
    let tags = attributes
        .get("tags")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|tag| pick_localized(tag.get("attributes")?.get("name")?, language))
        .collect();
    let creators = ["author", "artist"]
        .iter()
        .flat_map(|kind| relationships(item, kind))
        .filter_map(|r| text(r.get("attributes")?, "name").map(str::to_owned))
        .fold(Vec::new(), |mut names, name| {
            if !names.contains(&name) {
                names.push(name);
            }
            names
        });
    let cover_url = relationships(item, "cover_art")
        .find_map(|r| text(r.get("attributes")?, "fileName"))
        .map(|file| format!("https://uploads.mangadex.org/covers/{id}/{file}.256.jpg"));
    Some(Manga {
        description: attributes
            .get("description")
            .and_then(|d| pick_localized(d, language)),
        status: text(attributes, "status").map(str::to_owned),
        year: attributes.get("year").and_then(Value::as_i64),
        content_rating: text(attributes, "contentRating").map(str::to_owned),
        demographic: text(attributes, "publicationDemographic").map(str::to_owned),
        tags,
        creators,
        cover_url,
        title,
        id,
    })
}

/// Reads a chapter feed. Chapters hosted elsewhere are skipped, because MangaDex has no images for them.
/// That means any chapter with an `externalUrl` (official releases often report pages but are served by
/// the publisher) and any with no pages.
#[must_use]
pub fn chapter_listing(root: &Value) -> Listing<Chapter> {
    listing(root, |item| {
        let id = text(item, "id")?.to_owned();
        let attributes = item.get("attributes")?;
        let pages = attributes.get("pages").and_then(Value::as_i64).unwrap_or(0);
        let external = attributes
            .get("externalUrl")
            .is_some_and(|url| !url.is_null());
        if pages <= 0 || external {
            return None;
        }
        let groups = relationships(item, "scanlation_group")
            .filter_map(|r| text(r.get("attributes")?, "name").map(str::to_owned))
            .collect();
        Some(Chapter {
            id,
            number: text(attributes, "chapter").map(str::to_owned),
            volume: text(attributes, "volume").map(str::to_owned),
            title: text(attributes, "title").map(str::to_owned),
            pages,
            published_ms: text(attributes, "publishAt").and_then(parse_rfc3339_ms),
            groups,
            language: text(attributes, "translatedLanguage").map(str::to_owned),
        })
    })
}

/// Reads an `/at-home/server/{id}` response, preferring original quality.
#[must_use]
pub fn at_home(root: &Value) -> Option<AtHome> {
    let base_url = text(root, "baseUrl")?.trim_end_matches('/').to_owned();
    let chapter = root.get("chapter")?;
    let hash = text(chapter, "hash")?.to_owned();
    let names = |key: &str| -> Vec<String> {
        chapter
            .get(key)
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter_map(|f| f.as_str().map(str::to_owned))
            .collect()
    };
    let (segment, files) = match names("data") {
        original if !original.is_empty() => ("data", original),
        _ => ("data-saver", names("dataSaver")),
    };
    (!files.is_empty()).then_some(AtHome {
        base_url,
        hash,
        segment,
        files,
    })
}

/// Reads a `/statistics/manga` response.
#[must_use]
pub fn statistics(root: &Value) -> Vec<Statistics> {
    root.get("statistics")
        .and_then(Value::as_object)
        .into_iter()
        .flatten()
        .map(|(id, stats)| Statistics {
            id: id.clone(),
            rating: stats
                .get("rating")
                .and_then(|r| r.get("bayesian"))
                .and_then(Value::as_f64),
            follows: stats.get("follows").and_then(Value::as_i64),
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn localized_text_prefers_the_language_then_english_then_anything() {
        let map = json!({ "ja": "日本語", "en": "English", "fr": "Français" });
        assert_eq!(pick_localized(&map, "ja").as_deref(), Some("日本語"));
        assert_eq!(pick_localized(&map, "de").as_deref(), Some("English"));
        assert_eq!(
            pick_localized(&json!({ "fr": "Français" }), "de").as_deref(),
            Some("Français")
        );
        assert_eq!(pick_localized(&json!({ "en": "  " }), "en"), None);
        assert_eq!(pick_localized(&json!("not a map"), "en"), None);
    }

    fn sample_manga() -> Value {
        json!({ "total": 41, "offset": 20, "data": [{
            "id": "m-1", "type": "manga",
            "attributes": { "title": { "en": "Berserk" }, "description": { "en": "Dark." }, "status": "ongoing", "year": 1989,
                "contentRating": "suggestive", "publicationDemographic": "seinen",
                "tags": [ { "attributes": { "name": { "en": "Action" } } }, { "attributes": { "name": { "en": "Fantasy" } } } ] },
            "relationships": [
                { "type": "author", "attributes": { "name": "Kentaro Miura" } },
                { "type": "artist", "attributes": { "name": "Kentaro Miura" } },
                { "type": "cover_art", "attributes": { "fileName": "abc.jpg" } } ]
        }, { "id": "broken", "attributes": {} }] })
    }

    #[test]
    fn manga_are_mapped_with_tags_creators_and_cover() {
        let listing = manga_listing(&sample_manga(), "en");

        assert_eq!(
            (listing.total, listing.offset, listing.items.len()),
            (41, 20, 1),
            "the entry without a title is skipped"
        );
        let manga = &listing.items[0];
        assert_eq!(manga.title, "Berserk");
        assert_eq!(manga.tags, ["Action", "Fantasy"]);
        assert_eq!(
            manga.creators,
            ["Kentaro Miura"],
            "author and artist are listed once"
        );
        assert_eq!(
            manga.cover_url.as_deref(),
            Some("https://uploads.mangadex.org/covers/m-1/abc.jpg.256.jpg")
        );
        assert_eq!(
            (
                manga.year,
                manga.status.as_deref(),
                manga.demographic.as_deref()
            ),
            (Some(1989), Some("ongoing"), Some("seinen"))
        );
    }

    #[test]
    fn unreadable_chapters_are_skipped_and_groups_are_collected() {
        let root = json!({ "total": 3, "offset": 0, "data": [
            { "id": "c-1", "attributes": { "chapter": "12.5", "volume": "3", "title": "Hello", "pages": 20, "publishAt": "2021-05-02T10:11:12+00:00", "translatedLanguage": "en" },
              "relationships": [ { "type": "scanlation_group", "attributes": { "name": "Group A" } }, { "type": "user", "attributes": { "name": "ignored" } } ] },
            { "id": "c-2", "attributes": { "chapter": "13", "pages": 0, "externalUrl": "https://elsewhere.example" } },
            { "id": "c-4", "attributes": { "chapter": "1148", "pages": 1, "externalUrl": "https://mangaplus.example/viewer/1" } },
            { "id": "c-5", "attributes": { "chapter": "9", "pages": 7, "externalUrl": null } },
            { "id": "c-3", "attributes": { "pages": 5 } } ] });

        let listing = chapter_listing(&root);

        assert_eq!(
            listing
                .items
                .iter()
                .map(|c| c.id.as_str())
                .collect::<Vec<_>>(),
            ["c-1", "c-5", "c-3"],
            "chapters with no pages or an external URL are skipped"
        );
        let first = &listing.items[0];
        assert_eq!(
            (
                first.number.as_deref(),
                first.volume.as_deref(),
                first.pages
            ),
            (Some("12.5"), Some("3"), 20)
        );
        assert_eq!(first.groups, ["Group A"]);
        assert_eq!(first.published_ms, Some(1_619_950_272_000));
        assert_eq!(listing.items[2].number, None);
    }

    #[test]
    fn at_home_prefers_original_quality_and_falls_back_to_data_saver() {
        let both = json!({ "baseUrl": "https://img.example.network/", "chapter": { "hash": "h", "data": ["1.png", "2.png"], "dataSaver": ["1.jpg"] } });
        let saver_only = json!({ "baseUrl": "https://img.example.network", "chapter": { "hash": "h", "data": [], "dataSaver": ["1.jpg"] } });

        let original = at_home(&both).unwrap();
        let saver = at_home(&saver_only).unwrap();

        assert_eq!(
            (
                original.base_url.as_str(),
                original.segment,
                original.files.len()
            ),
            ("https://img.example.network", "data", 2)
        );
        assert_eq!(
            (saver.segment, saver.files.as_slice()),
            ("data-saver", &["1.jpg".to_owned()][..])
        );
        assert!(
            at_home(
                &json!({ "baseUrl": "x", "chapter": { "hash": "h", "data": [], "dataSaver": [] } })
            )
            .is_none()
        );
        assert!(at_home(&json!({ "chapter": {} })).is_none());
    }

    #[test]
    fn statistics_are_read_per_manga() {
        let root = json!({ "statistics": { "m-1": { "rating": { "average": 9.0, "bayesian": 8.7 }, "follows": 1200 }, "m-2": {} } });

        let mut stats = statistics(&root);
        stats.sort_by(|a, b| a.id.cmp(&b.id));

        assert_eq!(
            stats[0],
            Statistics {
                id: "m-1".into(),
                rating: Some(8.7),
                follows: Some(1200)
            }
        );
        assert_eq!(
            stats[1],
            Statistics {
                id: "m-2".into(),
                rating: None,
                follows: None
            }
        );
    }
}
