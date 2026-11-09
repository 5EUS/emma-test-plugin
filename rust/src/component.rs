//! The Wasm glue: the `emma:core` exports, written against the pure `mapping` module.

#[path = "bindings.generated.rs"]
mod bindings;

use serde_json::Value;

use crate::mapping::{self, AtHome, Chapter, Manga};
use crate::util::encode;
use bindings::emma::core::host;
use bindings::emma::core::types::{
    Choice, ErrorCode, FilterSpec, FilterType, Header, LookupSpec, Node, NodePage, PageRequest,
    PluginError, Prop, PropValue, Query,
};
use bindings::exports::emma::core::assets::{self, Asset, Chunk, Delivery, Remote, ResolveRequest};
use bindings::exports::emma::core::catalog;
use bindings::exports::emma::core::meta::{
    self, HealthReport, KindInfo, PluginInfo, SettingCategory, SettingCondition, SettingSpec,
    SettingType,
};
use bindings::exports::emma::core::search_assist;

struct MangaDex;

const DEFAULT_API: &str = "https://api.mangadex.org";
const USER_AGENT: &str = "EMMA-MangaDex/0.1 (+https://github.com/5EUS/emma)";
const DEFAULT_PAGE: usize = 20;
/// MangaDex refuses `offset + limit` beyond this.
const MAX_WINDOW: usize = 10_000;
const AT_HOME_TTL_MS: u32 = 10 * 60 * 1000;
const MATURITY: [&str; 4] = ["safe", "suggestive", "erotica", "pornographic"];

/// The user's settings with this plugin's own defaults (the host also supplies declared defaults).
struct Settings {
    language: String,
    maturity: Vec<String>,
    timeout_ms: u32,
    api_base: String,
    verbose: bool,
}

impl Settings {
    fn load() -> Self {
        let maturity = host::setting_values("search.defaultMaturity");
        let base = host::setting("advanced.apiBaseUrl")
            .filter(|b| b.starts_with("http://") || b.starts_with("https://"));
        Self {
            language: host::setting("search.defaultLanguage").unwrap_or_else(|| "en".to_owned()),
            maturity: if maturity.is_empty() {
                vec!["safe".to_owned(), "suggestive".to_owned()]
            } else {
                maturity
            },
            timeout_ms: host::setting("advanced.requestTimeout")
                .and_then(|t| t.parse().ok())
                .unwrap_or(15_000),
            api_base: base.map_or_else(
                || DEFAULT_API.to_owned(),
                |b| b.trim_end_matches('/').to_owned(),
            ),
            verbose: host::setting("advanced.enableVerboseLogging").as_deref() == Some("true"),
        }
    }
}

fn error(code: ErrorCode, message: impl Into<String>) -> PluginError {
    PluginError {
        code,
        message: message.into(),
        retry_after_ms: None,
    }
}

fn prop(key: &str, value: PropValue) -> Prop {
    Prop {
        key: key.to_owned(),
        value,
    }
}

fn text_prop(key: &str, value: &str) -> Prop {
    prop(key, PropValue::Text(value.to_owned()))
}

fn choice(value: &str, label: &str, is_default: bool) -> Choice {
    Choice {
        value: value.to_owned(),
        label: label.to_owned(),
        description: None,
        is_default,
    }
}

fn kind(kind: &str, label: &str, children: &[&str], roles: &[&str]) -> KindInfo {
    KindInfo {
        kind: kind.to_owned(),
        label: label.to_owned(),
        children: children.iter().map(|c| (*c).to_owned()).collect(),
        roles: roles.iter().map(|r| (*r).to_owned()).collect(),
        profile: Some("emma.profile.paged@1".to_owned()),
    }
}

fn multi_filter(id: &str, label: &str, role: &str, choices: Vec<Choice>) -> FilterSpec {
    FilterSpec {
        id: id.to_owned(),
        label: label.to_owned(),
        filter_type: FilterType::MultiChoice,
        kinds: vec![],
        required: false,
        hidden: false,
        group: None,
        description: None,
        choices,
        range: None,
        lookup: None,
        role: Some(role.to_owned()),
    }
}

/// The tag filter. MangaDex has about eighty tags, so the host asks `suggest` instead of listing them.
fn tag_filter() -> FilterSpec {
    FilterSpec {
        id: "tag".to_owned(),
        label: "Tag".to_owned(),
        filter_type: FilterType::Lookup,
        kinds: vec![],
        required: false,
        hidden: false,
        group: None,
        description: Some("Only manga that have every chosen tag.".to_owned()),
        choices: vec![],
        range: None,
        lookup: Some(LookupSpec {
            min_query_length: 2,
            max_results: 10,
            debounce_ms: 200,
            allow_custom_values: false,
        }),
        role: Some("emma.filter.tag".to_owned()),
    }
}

fn setting(key: &str, label: &str, setting_type: SettingType, category: &str) -> SettingSpec {
    SettingSpec {
        key: key.to_owned(),
        label: label.to_owned(),
        description: None,
        help: None,
        setting_type,
        category: Some(category.to_owned()),
        required: false,
        default_values: vec![],
        choices: vec![],
        placeholder: None,
        max_length: None,
        visible_when: None,
    }
}

// ----- HTTP -----------------------------------------------------------------------------------

/// Performs a GET against the MangaDex API and returns the body, mapping failures to contract errors.
fn get(settings: &Settings, path_and_query: &str) -> Result<Vec<u8>, PluginError> {
    let url = format!("{}{path_and_query}", settings.api_base);
    if settings.verbose {
        host::log(host::LogLevel::Debug, &format!("GET {url}"));
    }
    let response = host::http_fetch(&host::HttpRequest {
        method: "GET".to_owned(),
        url,
        headers: vec![
            Header {
                name: "accept".to_owned(),
                value: "application/json".to_owned(),
            },
            Header {
                name: "user-agent".to_owned(),
                value: USER_AGENT.to_owned(),
            },
        ],
        body: None,
        timeout_ms: Some(settings.timeout_ms),
    })?;
    match response.status {
        200..=299 => Ok(response.body),
        404 => Err(error(ErrorCode::NotFound, "MangaDex has no such item.")),
        401 | 403 => Err(error(
            ErrorCode::Unauthorized,
            "MangaDex refused the request.",
        )),
        429 => Err(PluginError {
            code: ErrorCode::RateLimited,
            message: "MangaDex asked for slower requests.".to_owned(),
            retry_after_ms: Some(retry_after_ms(&response.headers)),
        }),
        500..=599 => Err(error(
            ErrorCode::Unavailable,
            format!("MangaDex failed with status {}.", response.status),
        )),
        other => {
            let detail = serde_json::from_slice::<Value>(&response.body)
                .ok()
                .and_then(|v| v["errors"][0]["detail"].as_str().map(str::to_owned))
                .unwrap_or_else(|| "the request was rejected".to_owned());
            Err(error(
                ErrorCode::InvalidArgument,
                format!("MangaDex rejected the request ({other}): {detail}"),
            ))
        }
    }
}

/// Performs a GET and parses the JSON body.
fn get_json(settings: &Settings, path_and_query: &str) -> Result<Value, PluginError> {
    serde_json::from_slice(&get(settings, path_and_query)?).map_err(|_| {
        error(
            ErrorCode::Unavailable,
            "MangaDex returned a response that is not JSON.",
        )
    })
}

/// MangaDex sends `X-RateLimit-Retry-After` as a Unix timestamp in seconds.
fn retry_after_ms(headers: &[Header]) -> u32 {
    let value = |name: &str| {
        headers
            .iter()
            .find(|h| h.name.eq_ignore_ascii_case(name))
            .and_then(|h| h.value.trim().parse::<i64>().ok())
    };
    let wait = value("x-ratelimit-retry-after")
        .map(|at| at.saturating_mul(1000) - host::now_ms())
        .or_else(|| value("retry-after").map(|s| s.saturating_mul(1000)));
    u32::try_from(wait.unwrap_or(5000).clamp(1000, 60_000)).unwrap_or(5000)
}

fn repeated(parameters: &mut Vec<String>, name: &str, values: &[String]) {
    for value in values {
        parameters.push(format!("{name}={}", encode(value)));
    }
}

// ----- ids ------------------------------------------------------------------------------------

fn manga_id(id: &str) -> String {
    format!("manga:{id}")
}

fn chapter_id(id: &str) -> String {
    format!("chapter:{id}")
}

/// A page id carries everything needed to build its URL except the image server, which rotates.
fn page_id(chapter: &str, at_home: &AtHome, file: &str) -> String {
    format!("page:{chapter}:{}:{}:{file}", at_home.segment, at_home.hash)
}

struct PageRef<'a> {
    chapter: &'a str,
    segment: &'a str,
    hash: &'a str,
    file: &'a str,
}

fn parse_page_id(id: &str) -> Option<PageRef<'_>> {
    let mut parts = id.strip_prefix("page:")?.splitn(4, ':');
    let (chapter, segment, hash, file) =
        (parts.next()?, parts.next()?, parts.next()?, parts.next()?);
    matches!(segment, "data" | "data-saver").then_some(PageRef {
        chapter,
        segment,
        hash,
        file,
    })
}

// ----- node mapping ---------------------------------------------------------------------------

fn manga_node(manga: &Manga) -> Node {
    let mut props = Vec::new();
    if let Some(description) = &manga.description {
        props.push(text_prop("emma.synopsis", description));
    }
    if !manga.tags.is_empty() {
        props.push(prop("emma.tags", PropValue::TextList(manga.tags.clone())));
    }
    if !manga.creators.is_empty() {
        props.push(prop(
            "emma.creators",
            PropValue::TextList(manga.creators.clone()),
        ));
    }
    if let Some(status) = &manga.status {
        props.push(text_prop("emma.status", status));
    }
    if let Some(rating) = &manga.content_rating {
        props.push(text_prop("mangadex.content-rating", rating));
    }
    if let Some(demographic) = &manga.demographic {
        props.push(text_prop("mangadex.demographic", demographic));
    }
    if let Some(year) = manga.year {
        props.push(prop("mangadex.year", PropValue::Integer(year)));
    }
    props.push(text_prop("emma.reading-direction", "rtl"));
    Node {
        id: manga_id(&manga.id),
        kind: "manga".to_owned(),
        title: manga.title.clone(),
        parent: None,
        thumbnail: manga.cover_url.clone(),
        props,
    }
}

fn chapter_node(chapter: &Chapter, parent: Option<&str>) -> Node {
    let label = chapter
        .number
        .as_deref()
        .map_or_else(|| "Oneshot".to_owned(), |n| format!("Chapter {n}"));
    let title = chapter
        .title
        .as_deref()
        .map_or_else(|| label.clone(), |t| format!("{label} — {t}"));
    let mut props = vec![prop("emma.page-count", PropValue::Integer(chapter.pages))];
    if let Some(ordinal) = chapter
        .number
        .as_deref()
        .and_then(|n| n.parse::<f64>().ok())
    {
        props.push(prop("emma.ordinal", PropValue::Number(ordinal)));
    }
    if let Some(ms) = chapter.published_ms {
        props.push(prop("emma.released-ms", PropValue::TimestampMs(ms)));
    }
    if !chapter.groups.is_empty() {
        props.push(prop(
            "emma.groups",
            PropValue::TextList(chapter.groups.clone()),
        ));
    }
    if let Some(volume) = &chapter.volume {
        props.push(text_prop("mangadex.volume", volume));
    }
    if let Some(language) = &chapter.language {
        props.push(text_prop("emma.language", language));
    }
    Node {
        id: chapter_id(&chapter.id),
        kind: "manga.chapter".to_owned(),
        title,
        parent: parent.map(str::to_owned),
        thumbnail: None,
        props,
    }
}

fn page_nodes(chapter: &str, at_home: &AtHome, from: usize, count: usize) -> Vec<Node> {
    at_home
        .files
        .iter()
        .enumerate()
        .skip(from)
        .take(count)
        .map(|(index, file)| Node {
            id: page_id(chapter, at_home, file),
            kind: "manga.page".to_owned(),
            title: format!("Page {}", index + 1),
            parent: Some(chapter_id(chapter)),
            thumbnail: None,
            props: vec![prop(
                "emma.ordinal",
                PropValue::Number(f64::from(u32::try_from(index + 1).unwrap_or(u32::MAX))),
            )],
        })
        .collect()
}

// ----- paging ---------------------------------------------------------------------------------

fn window(cursor: Option<&str>, limit: Option<u32>) -> Result<(usize, usize), PluginError> {
    let offset = match cursor {
        None => 0,
        Some(text) => text
            .parse::<usize>()
            .map_err(|_| error(ErrorCode::InvalidArgument, "That cursor is not valid."))?,
    };
    let size = limit
        .map_or(DEFAULT_PAGE, |l| usize::try_from(l).unwrap_or(DEFAULT_PAGE))
        .clamp(1, 100);
    Ok((offset, size))
}

fn next_cursor(offset: usize, returned: usize, total: i64) -> Option<String> {
    let next = offset + returned;
    (returned > 0 && i64::try_from(next).is_ok_and(|n| n < total) && next < MAX_WINDOW)
        .then(|| next.to_string())
}

// ----- the manga list (search, browse root) ---------------------------------------------------

fn manga_page(
    settings: &Settings,
    query: &Query,
    offset: usize,
    size: usize,
) -> Result<NodePage, PluginError> {
    let values = |id: &str| -> Vec<String> {
        query
            .filters
            .iter()
            .find(|f| f.id == id)
            .map(|f| f.values.clone())
            .unwrap_or_default()
    };
    let mut parameters = vec![format!("limit={size}"), format!("offset={offset}")];
    repeated(
        &mut parameters,
        "includes[]",
        &[
            "cover_art".to_owned(),
            "author".to_owned(),
            "artist".to_owned(),
        ],
    );
    let maturity = values("maturity");
    repeated(
        &mut parameters,
        "contentRating[]",
        if maturity.is_empty() {
            &settings.maturity
        } else {
            &maturity
        },
    );
    repeated(&mut parameters, "status[]", &values("status"));
    repeated(
        &mut parameters,
        "publicationDemographic[]",
        &values("demographic"),
    );
    repeated(&mut parameters, "includedTags[]", &values("tag"));
    let languages = values("language");
    repeated(
        &mut parameters,
        "availableTranslatedLanguage[]",
        if languages.is_empty() {
            std::slice::from_ref(&settings.language)
        } else {
            &languages
        },
    );
    let title = query.text.trim();
    if !title.is_empty() {
        parameters.push(format!("title={}", encode(title)));
    }
    let order = match query.sort.as_deref() {
        Some("followedCount") => "followedCount",
        Some("latestUploadedChapter") => "latestUploadedChapter",
        Some("createdAt") => "createdAt",
        Some("relevance") => "relevance",
        _ if title.is_empty() => "followedCount",
        _ => "relevance",
    };
    parameters.push(format!("order[{order}]=desc"));

    let root = get_json(settings, &format!("/manga?{}", parameters.join("&")))?;
    let listing = mapping::manga_listing(&root, &settings.language);
    let returned = listing.items.len();
    Ok(NodePage {
        items: listing.items.iter().map(manga_node).collect(),
        next_cursor: next_cursor(offset, returned, listing.total),
        total: u64::try_from(listing.total).ok(),
        facets: vec![],
    })
}

// ----- chapters -------------------------------------------------------------------------------

fn chapter_page(
    settings: &Settings,
    manga: &str,
    offset: usize,
    size: usize,
) -> Result<NodePage, PluginError> {
    let size = size.clamp(DEFAULT_PAGE, 500);
    let mut parameters = vec![
        format!("limit={size}"),
        format!("offset={offset}"),
        "order[chapter]=asc".to_owned(),
        "includeUnavailable=1".to_owned(),
    ];
    repeated(
        &mut parameters,
        "translatedLanguage[]",
        std::slice::from_ref(&settings.language),
    );
    repeated(
        &mut parameters,
        "contentRating[]",
        &MATURITY.map(str::to_owned),
    );
    repeated(
        &mut parameters,
        "includes[]",
        &["scanlation_group".to_owned()],
    );
    let root = get_json(
        settings,
        &format!("/manga/{}/feed?{}", encode(manga), parameters.join("&")),
    )?;
    let listing = mapping::chapter_listing(&root);
    let parent = manga_id(manga);
    // The cursor counts the upstream's items, including skipped chapters with no pages.
    let upstream = root
        .get("data")
        .and_then(Value::as_array)
        .map_or(0, Vec::len);
    Ok(NodePage {
        items: listing
            .items
            .iter()
            .map(|c| chapter_node(c, Some(&parent)))
            .collect(),
        next_cursor: next_cursor(offset, upstream, listing.total),
        total: u64::try_from(listing.total).ok(),
        facets: vec![],
    })
}

// ----- pages ----------------------------------------------------------------------------------

/// Gets (and caches, to respect MangaDex's at-home rate limit) the image server details for a chapter.
fn at_home_for(settings: &Settings, chapter: &str) -> Result<AtHome, PluginError> {
    let key = format!("athome:{chapter}");
    let cached: Option<Value> =
        host::cache_get(&key).and_then(|bytes| serde_json::from_slice(&bytes).ok());
    let root = if let Some(cached) = cached {
        cached
    } else {
        let fresh = get_json(settings, &format!("/at-home/server/{}", encode(chapter)))?;
        if let Ok(bytes) = serde_json::to_vec(&fresh) {
            host::cache_put(&key, &bytes, AT_HOME_TTL_MS);
        }
        fresh
    };
    mapping::at_home(&root)
        .ok_or_else(|| error(ErrorCode::NotFound, "That chapter has no readable pages."))
}

fn mime_for(file: &str) -> &'static str {
    match file
        .rsplit('.')
        .next()
        .map(str::to_ascii_lowercase)
        .as_deref()
    {
        Some("png") => "image/png",
        Some("gif") => "image/gif",
        Some("webp") => "image/webp",
        _ => "image/jpeg",
    }
}

// ----- the exports ----------------------------------------------------------------------------

/// The settings screen's groups.
fn declared_categories() -> Vec<SettingCategory> {
    vec![
        SettingCategory {
            id: "connection".to_owned(),
            label: "Connection".to_owned(),
            description: Some("Provider identity and endpoint settings.".to_owned()),
        },
        SettingCategory {
            id: "search".to_owned(),
            label: "Search defaults".to_owned(),
            description: Some("Defaults applied when browsing and searching.".to_owned()),
        },
        SettingCategory {
            id: "advanced".to_owned(),
            label: "Advanced".to_owned(),
            description: Some("Diagnostics and low-level request behaviour.".to_owned()),
        },
    ]
}

/// The settings screen: categories and typed settings, carried over from the reference plugin.
fn declared_settings(
    maturity: Vec<Choice>,
    languages: Vec<Choice>,
) -> (Vec<SettingCategory>, Vec<SettingSpec>) {
    (
        declared_categories(),
        vec![
            SettingSpec {
                description: Some("Optional account identifier.".to_owned()),
                help: Some(
                    "Public MangaDex endpoints need no account; stored as a normal value."
                        .to_owned(),
                ),
                placeholder: Some("reader@example.com".to_owned()),
                max_length: Some(120),
                ..setting(
                    "connection.username",
                    "Account name",
                    SettingType::Text,
                    "connection",
                )
            },
            SettingSpec {
                help: Some(
                    "Not used by public endpoints. Stored encrypted by the host.".to_owned(),
                ),
                ..setting(
                    "connection.apiToken",
                    "API token",
                    SettingType::Secret,
                    "connection",
                )
            },
            SettingSpec {
                description: Some("Preferred translated language.".to_owned()),
                default_values: vec!["en".to_owned()],
                choices: languages,
                ..setting(
                    "search.defaultLanguage",
                    "Default language",
                    SettingType::Choice,
                    "search",
                )
            },
            SettingSpec {
                description: Some("Maturity ratings shown by default.".to_owned()),
                default_values: vec!["safe".to_owned(), "suggestive".to_owned()],
                choices: maturity,
                ..setting(
                    "search.defaultMaturity",
                    "Visible maturity ratings",
                    SettingType::MultiChoice,
                    "search",
                )
            },
            SettingSpec {
                description: Some("Log each request.".to_owned()),
                default_values: vec!["false".to_owned()],
                ..setting(
                    "advanced.enableVerboseLogging",
                    "Verbose logging",
                    SettingType::Boolean,
                    "advanced",
                )
            },
            SettingSpec {
                description: Some("Per-request timeout.".to_owned()),
                help: Some("Shown only when verbose logging is on.".to_owned()),
                default_values: vec!["15000".to_owned()],
                visible_when: Some(SettingCondition {
                    key: "advanced.enableVerboseLogging".to_owned(),
                    equals: "true".to_owned(),
                }),
                ..setting(
                    "advanced.requestTimeout",
                    "Request timeout",
                    SettingType::Duration,
                    "advanced",
                )
            },
            SettingSpec {
                description: Some("For mirrors, proxies and tests.".to_owned()),
                placeholder: Some(DEFAULT_API.to_owned()),
                max_length: Some(200),
                ..setting(
                    "advanced.apiBaseUrl",
                    "API base URL",
                    SettingType::Text,
                    "advanced",
                )
            },
        ],
    )
}

impl meta::Guest for MangaDex {
    fn describe() -> Result<PluginInfo, PluginError> {
        let maturity = vec![
            choice("safe", "Safe", true),
            choice("suggestive", "Suggestive", true),
            choice("erotica", "Erotica", false),
            choice("pornographic", "Pornographic", false),
        ];
        let languages = vec![
            choice("en", "English", true),
            choice("ja", "Japanese", false),
            choice("ko", "Korean", false),
            choice("zh", "Chinese", false),
        ];
        let language_choices: Vec<Choice> = languages
            .iter()
            .map(|language| choice(&language.value, &language.label, false))
            .collect();
        let (categories, settings) = declared_settings(maturity.clone(), languages);
        Ok(PluginInfo {
            kinds: vec![
                kind("manga", "Manga", &["manga.chapter"], &[]),
                kind("manga.chapter", "Chapter", &["manga.page"], &[]),
                kind("manga.page", "Page", &[], &["image"]),
            ],
            filters: vec![
                multi_filter(
                    "status",
                    "Status",
                    "emma.filter.status",
                    vec![
                        choice("ongoing", "Ongoing", false),
                        choice("completed", "Completed", false),
                        choice("hiatus", "Hiatus", false),
                        choice("cancelled", "Cancelled", false),
                    ],
                ),
                multi_filter(
                    "maturity",
                    "Maturity",
                    "emma.filter.maturity",
                    maturity.clone(),
                ),
                multi_filter(
                    "demographic",
                    "Demographic",
                    "emma.filter.demographic",
                    vec![
                        choice("shounen", "Shounen", false),
                        choice("shoujo", "Shoujo", false),
                        choice("josei", "Josei", false),
                        choice("seinen", "Seinen", false),
                    ],
                ),
                multi_filter(
                    "language",
                    "Translated language",
                    "emma.filter.language",
                    language_choices,
                ),
                tag_filter(),
            ],
            sorts: vec![
                choice("relevance", "Relevance", true),
                choice("followedCount", "Most followed", false),
                choice("latestUploadedChapter", "Recently updated", false),
                choice("createdAt", "Newest", false),
            ],
            categories,
            settings,
        })
    }

    fn health() -> HealthReport {
        // /ping answers plain text ("pong"), not JSON.
        match get(&Settings::load(), "/ping") {
            Ok(_) => HealthReport {
                ok: true,
                detail: None,
            },
            Err(failure) => HealthReport {
                ok: false,
                detail: Some(failure.message),
            },
        }
    }
}

impl catalog::Guest for MangaDex {
    fn search(query: Query) -> Result<NodePage, PluginError> {
        let (offset, size) = window(query.cursor.as_deref(), query.limit)?;
        manga_page(&Settings::load(), &query, offset, size)
    }

    fn children(parent: Option<String>, request: PageRequest) -> Result<NodePage, PluginError> {
        let (offset, size) = window(request.cursor.as_deref(), request.limit)?;
        let settings = Settings::load();
        match parent.as_deref() {
            None => manga_page(
                &settings,
                &Query {
                    text: String::new(),
                    kinds: vec![],
                    filters: vec![],
                    sort: None,
                    cursor: None,
                    limit: None,
                },
                offset,
                size,
            ),
            Some(id) => {
                if let Some(manga) = id.strip_prefix("manga:") {
                    chapter_page(&settings, manga, offset, size)
                } else if let Some(chapter) = id.strip_prefix("chapter:") {
                    let at_home = at_home_for(&settings, chapter)?;
                    let total = at_home.files.len();
                    let count = size.clamp(DEFAULT_PAGE, 500);
                    let items = page_nodes(chapter, &at_home, offset, count);
                    let end = offset + items.len();
                    Ok(NodePage {
                        next_cursor: (end < total).then(|| end.to_string()),
                        total: u64::try_from(total).ok(),
                        items,
                        facets: vec![],
                    })
                } else {
                    Err(error(
                        ErrorCode::NotFound,
                        "That node has no children here.",
                    ))
                }
            }
        }
    }

    fn get(ids: Vec<String>) -> Result<Vec<Node>, PluginError> {
        let settings = Settings::load();
        let wanted = |prefix: &str| -> Vec<String> {
            ids.iter()
                .filter_map(|id| id.strip_prefix(prefix).map(str::to_owned))
                .collect()
        };
        let mut found: Vec<Node> = Vec::new();
        for batch in wanted("manga:").chunks(100) {
            let mut parameters = vec!["limit=100".to_owned()];
            repeated(&mut parameters, "ids[]", batch);
            repeated(
                &mut parameters,
                "includes[]",
                &[
                    "cover_art".to_owned(),
                    "author".to_owned(),
                    "artist".to_owned(),
                ],
            );
            repeated(
                &mut parameters,
                "contentRating[]",
                &MATURITY.map(str::to_owned),
            );
            let root = get_json(&settings, &format!("/manga?{}", parameters.join("&")))?;
            found.extend(
                mapping::manga_listing(&root, &settings.language)
                    .items
                    .iter()
                    .map(manga_node),
            );
        }
        for batch in wanted("chapter:").chunks(100) {
            let mut parameters = vec!["limit=100".to_owned()];
            repeated(&mut parameters, "ids[]", batch);
            repeated(
                &mut parameters,
                "includes[]",
                &["scanlation_group".to_owned(), "manga".to_owned()],
            );
            repeated(
                &mut parameters,
                "contentRating[]",
                &MATURITY.map(str::to_owned),
            );
            let root = get_json(&settings, &format!("/chapter?{}", parameters.join("&")))?;
            found.extend(
                mapping::chapter_listing(&root)
                    .items
                    .iter()
                    .map(|c| chapter_node(c, None)),
            );
        }
        // Return in the order requested, omitting what MangaDex does not know.
        Ok(ids
            .iter()
            .filter_map(|id| found.iter().find(|node| &node.id == id).cloned())
            .collect())
    }
}

impl assets::Guest for MangaDex {
    fn resolve(request: ResolveRequest) -> Result<Vec<Asset>, PluginError> {
        let (true, Some(page)) = (request.role == "image", parse_page_id(&request.node)) else {
            return Err(error(
                ErrorCode::Unsupported,
                "Only a page can be resolved, and only to an image.",
            ));
        };
        let at_home = at_home_for(&Settings::load(), page.chapter)?;
        let url = format!(
            "{}/{}/{}/{}",
            at_home.base_url, page.segment, page.hash, page.file
        );
        Ok(vec![Asset {
            mime: mime_for(page.file).to_owned(),
            delivery: Delivery::Remote(Remote {
                url,
                headers: vec![],
                expires_ms: Some(host::now_ms() + i64::from(AT_HOME_TTL_MS)),
            }),
            size: None,
            props: vec![text_prop(
                "emma.quality",
                if page.segment == "data" {
                    "original"
                } else {
                    "data-saver"
                },
            )],
        }])
    }

    fn read(_token: String, _range: Option<assets::ByteRange>) -> Result<Chunk, PluginError> {
        Err(error(
            ErrorCode::NotFound,
            "This plugin has no proxied assets.",
        ))
    }
}

impl search_assist::Guest for MangaDex {
    /// Completes the `tag` filter: the value sent back is the MangaDex tag id, the label its English name.
    fn suggest(filter_id: String, text: String, limit: u32) -> Result<Vec<Choice>, PluginError> {
        if filter_id != "tag" {
            return Err(error(
                ErrorCode::InvalidArgument,
                format!("'{filter_id}' is not a lookup filter of this plugin."),
            ));
        }
        let settings = Settings::load();
        let root = get_json(&settings, "/manga/tag")?;
        let size = usize::try_from(limit.clamp(1, 20)).unwrap_or(10);
        Ok(mapping::match_tags(&mapping::tags(&root), &text, size)
            .into_iter()
            .map(|tag| choice(&tag.id, &tag.name, false))
            .collect())
    }

    /// Adds MangaDex's rating and follower count. The returned titles are placeholders: the host merges only props.
    fn enrich(ids: Vec<String>) -> Result<Vec<Node>, PluginError> {
        let settings = Settings::load();
        let manga: Vec<String> = ids
            .iter()
            .filter_map(|id| id.strip_prefix("manga:").map(str::to_owned))
            .take(100)
            .collect();
        if manga.is_empty() {
            return Ok(vec![]);
        }
        let mut parameters = Vec::new();
        repeated(&mut parameters, "manga[]", &manga);
        let root = get_json(
            &settings,
            &format!("/statistics/manga?{}", parameters.join("&")),
        )?;
        Ok(mapping::statistics(&root)
            .into_iter()
            .map(|stats| {
                let mut props = Vec::new();
                if let Some(rating) = stats.rating {
                    props.push(prop("emma.rating", PropValue::Number(rating)));
                }
                if let Some(follows) = stats.follows {
                    props.push(prop("mangadex.follows", PropValue::Integer(follows)));
                }
                Node {
                    id: manga_id(&stats.id),
                    kind: "manga".to_owned(),
                    title: manga_id(&stats.id),
                    parent: None,
                    thumbnail: None,
                    props,
                }
            })
            .collect())
    }
}

bindings::export!(MangaDex with_types_in bindings);
