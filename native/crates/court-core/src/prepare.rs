//! First-run floor library preparation from the installed game (port of
//! `tools/prepare_portable.py` with the helpers it uses from
//! `build_2k26_floor_template_library.py` and `export_2k26_court_texture.py`).
//!
//! Textures are read straight from the game's archives (read-only) instead of being extracted
//! into a scratch folder first; decoding, speckle cleaning and thumbnails follow Pillow.
use crate::error::{Error, Result, check};
use crate::game::{self, ManifestEntry};
use crate::lock::FileLock;
use crate::raster::{self, Filter, Rgba};
use crate::{files, oodle, pyjson};
use rayon::prelude::*;
use regex::Regex;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Duration;

/// Progress reported while preparing (the Python script's stdout protocol).
#[derive(Clone, Debug, PartialEq)]
pub enum Event {
    Status(String),
    /// `FIRST_FLOOR_READY`: the index holds one usable floor (`preparationComplete` false).
    FirstFloorReady,
    Progress { done: usize, total: usize },
}

const GAME_VERSION: &str = "2k27";
const INDEX_NAME: &str = "nba2k27_floor_templates.json";
const MAX_MEMBER_BYTES: u64 = 64 * 1024 * 1024;
const MAX_INDEX_BYTES: usize = 64 * 1024 * 1024;
const INDEX_STYLE: pyjson::Style = pyjson::Style { indent: Some(2), ensure_ascii: true, sort_keys: false, compact: false };

static WOOD_FLOOR: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^floor_(?P<floor>[a-z0-9]+)_court_wood(?P<wood>[0-9]*)_basecolor\.[0-9a-f]+\.mip0$").unwrap());

pub const BROKEN_SOURCE_TEXTURES: &[&str] = &["floor_300_court_wood1_basecolor"];
const COLLEGE_FLOOR_KEYS: &[&str] = &[
    "arizonawildcats", "baylorbears", "dukebluedevils", "floridagators", "houstoncougars", "kansasjayhawks", "kentuckywildcats", "louisvillecardinals",
    "michiganstatespartans", "michiganwolverines", "ohiostatebuckeyes", "purdueboilermakers", "texaslonghorns", "uclabruins", "uconnhuskies", "unctarheels",
];
const HISTORIC_NBA_FLOOR_KEYS: &[&str] = &[
    "bobcats2011", "bucks2015", "bulls2016", "cavaliers2011", "cavaliers2016", "clippers2015", "clippers2022", "grizzlies2016", "hornets2002", "jazz2016",
    "jazz1986", "kings2016", "knicks2016", "nets2012", "nuggets2016", "pacers2005", "pistons2016", "raptors2016", "rockets2003", "rockets2016", "spurs1998",
    "suns2016", "thunder2016", "timberwolves2011", "wizards2014",
];
const INTERNATIONAL_FLOOR_KEYS: &[&str] = &["barcelona", "basquetmadrid", "fcmalaga", "fiba", "fibagame", "madrid", "multileague", "paris", "strasbourg", "usabasketball"];
const MODE_FLOOR_KEYS: &[&str] = &[
    "aau", "aaugym", "brickley", "clutchtime", "gleagueignite", "hsgym", "matchmaking", "myteam", "scrimmage", "statechampionship", "summerleaguegeneric", "top10rep",
];
const ARENA_TEAM_NAMES: &[(&str, &str)] = &[
    ("000", "Philadelphia 76ers"), ("001", "Milwaukee Bucks"), ("002", "Washington Wizards"), ("003", "Chicago Bulls"), ("004", "Cleveland Cavaliers"),
    ("005", "Boston Celtics"), ("006", "Los Angeles Clippers"), ("008", "Memphis Grizzlies"), ("009", "Atlanta Hawks"), ("010", "Miami Heat"),
    ("011", "New Orleans Pelicans"), ("012", "Utah Jazz"), ("013", "Sacramento Kings"), ("014", "New York Knicks"), ("015", "Los Angeles Lakers"),
    ("016", "Orlando Magic"), ("017", "Dallas Mavericks"), ("018", "Brooklyn Nets"), ("019", "Denver Nuggets"), ("020", "Indiana Pacers"),
    ("021", "Detroit Pistons"), ("022", "Toronto Raptors"), ("023", "Houston Rockets"), ("024", "Oklahoma City Thunder"), ("025", "San Antonio Spurs"),
    ("026", "Phoenix Suns"), ("027", "Minnesota Timberwolves"), ("028", "Portland Trail Blazers"), ("029", "Golden State Warriors"), ("031", "Charlotte Hornets / Bobcats"),
];
const WNBA_ARENA_NAMES: &[(&str, &str)] = &[
    ("300", "Las Vegas Aces"), ("301", "Atlanta Dream"), ("302", "Indiana Fever / Pacers Arena"), ("303", "New York Liberty / Nets Arena"),
    ("304", "Minnesota Lynx / Timberwolves Arena"), ("305", "Phoenix Mercury / Suns Arena"), ("306", "Washington Mystics"), ("307", "Chicago Sky"),
    ("308", "Los Angeles Sparks / Lakers Arena"), ("309", "Seattle Storm"), ("310", "Connecticut Sun"), ("311", "Dallas Wings"),
    ("315", "Golden State Valkyries / Chase Center"), ("316", "Toronto Tempo"), ("317", "Portland Fire"),
];
const HISTORIC_ARENA_NAMES: &[(&str, &str)] = &[
    ("551", "1985-86 Chicago Bulls"), ("552", "1985-86 Boston Celtics / Boston Garden"), ("553", "1987-93 Chicago Bulls / Chicago Stadium"),
    ("554", "1986-87 Atlanta Hawks"), ("555", "1980-90 Cleveland Cavaliers"), ("556", "1989-90 Detroit Pistons"), ("557", "1990-91 Los Angeles Lakers"),
    ("558", "1990-91 Portland Trail Blazers"), ("559", "1995-96 Chicago Bulls / United Center"), ("560", "1994-95 New York Knicks"),
    ("562", "1995-96 Seattle SuperSonics"), ("564", "1997-98 Utah Jazz"), ("570", "1964-65 Los Angeles Lakers"), ("571", "1970-71 Milwaukee Bucks"),
    ("572", "1971-72 Los Angeles Lakers"), ("574", "1971-72 New York Knicks"), ("576", "1984-85 Milwaukee Bucks"), ("583", "1990-91 Golden State Warriors"),
    ("586", "1992-93 Charlotte Hornets"), ("588", "1993-94 Denver Nuggets"), ("589", "1993-94 Houston Rockets"), ("590", "1994-95 Orlando Magic"),
    ("591", "1997-98 Los Angeles Lakers"), ("592", "1997-98 San Antonio Spurs"), ("593", "2001-02 Sacramento Kings"), ("594", "1976-77 Philadelphia 76ers"),
    ("595", "2000-01 Philadelphia 76ers"), ("620", "1999-00 Toronto Raptors"), ("621", "1999-00 Portland Trail Blazers"), ("622", "2000-01 Los Angeles Lakers"),
    ("623", "2002-03 Dallas Mavericks"), ("624", "2003-04 Detroit Pistons"), ("625", "2003-04 Minnesota Timberwolves"), ("626", "2004-05 Phoenix Suns"),
    ("627", "2005-06 Miami Heat"), ("628", "2006-07 Cleveland Cavaliers"), ("629", "2007-08 Boston Celtics"), ("630", "2007-08 Houston Rockets"),
    ("631", "2012-13 Miami Heat"), ("632", "1996-97 Miami Heat"), ("633", "1999-00 New York Knicks"), ("634", "2015-16 / 2016-17 Golden State Warriors"),
    ("635", "2001-02 New Jersey Nets"), ("636", "2004-05 San Antonio Spurs"), ("637", "2006-07 Golden State Warriors"), ("638", "2006-07 Washington Wizards"),
    ("639", "2007-08 Denver Nuggets"), ("641", "2010-11 Chicago Bulls"), ("642", "2010-11 Dallas Mavericks"), ("644", "2011-12 New York Knicks"),
    ("645", "2002-03 Phoenix Suns"), ("646", "2009-10 Portland Trail Blazers"), ("647", "2013-14 San Antonio Spurs"), ("648", "2013-14 Los Angeles Clippers"),
    ("649", "2015-16 Cleveland Cavaliers"), ("924", "2018-19 Toronto Raptors")
];
const EVENT_ARENA_NAMES: &[(&str, &str)] = &[
    ("700", "Summer League"), ("701", "Generic Event Arena"), ("728", "2K Sports Practice Gym"), ("729", "2K15 / 2K Sports Practice Gym"),
    ("800", "Historic Decades Arena"), ("852", "Expansion Arena 852"), ("853", "Expansion Arena 853"), ("854", "Expansion Arena 854"),
    ("855", "Expansion Arena 855"), ("856", "Expansion Arena 856"), ("857", "Expansion Arena 857"), ("858", "Expansion Arena 858"),
    ("859", "Expansion Arena 859"), ("860", "Expansion Arena 860"), ("861", "Expansion Arena 861"), ("906", "Rec Center Gym"),
];
const VARIANT_LABELS: &[(&str, &str)] = &[("city", "City"), ("statement", "Statement"), ("classic", "Classic"), ("event", "Event")];
const SPECIAL_FLOOR_NAMES: &[(&str, &str)] = &[
    ("305rebel", "UNLV Rebels"), ("aau", "AAU"), ("aaugym", "AAU Gym"), ("allstar2021", "NBA All-Star 2021"), ("allstar2022", "NBA All-Star 2022"),
    ("arizonawildcats", "Arizona Wildcats"), ("barcelona", "Barcelona"), ("basquetmadrid", "Basquet Madrid"), ("baylorbears", "Baylor Bears"),
    ("bobcats2011", "Charlotte Bobcats 2011"), ("bucks2015", "Milwaukee Bucks 2015"), ("bulls2016", "Chicago Bulls 2016"),
    ("cavaliers2011", "Cleveland Cavaliers 2011"), ("cavaliers2016", "Cleveland Cavaliers 2016"), ("clippers2015", "Los Angeles Clippers 2015"),
    ("clippers2022", "Los Angeles Clippers 2022"), ("clutchtime", "Clutch Time"), ("clutchtime1980", "Clutch Time 1980s"), ("clutchtime1990", "Clutch Time 1990s"),
    ("clutchtime2000", "Clutch Time 2000s"), ("dukebluedevils", "Duke Blue Devils"), ("floridagators", "Florida Gators"), ("decades", "Decades All-Star Arena"),
    ("fcmalaga", "FC Malaga"), ("fiba", "FIBA / International Arena"), ("fibagame", "FIBA Game Arena"), ("gleagueignite", "G League Ignite"),
    ("grizzlies2016", "Memphis Grizzlies 2005-06 / 2012-13"), ("houstoncougars", "Houston Cougars"), ("hsgym", "High School Gym"), ("jazz2016", "Utah Jazz 2016"),
    ("kansasjayhawks", "Kansas Jayhawks"), ("kentuckywildcats", "Kentucky Wildcats"), ("kentuckywildcatswomens", "Kentucky Wildcats Womens"),
    ("kings2016", "Sacramento Kings 2016"), ("knicks2016", "New York Knicks 2016"), ("lakers1983", "1986-87 Los Angeles Lakers"),
    ("louisvillecardinals", "Louisville Cardinals"), ("madrid", "Madrid"), ("matchmakingb1sponsor", "Matchmaking B1 Sponsor"),
    ("michiganstatespartans", "Michigan State Spartans"), ("michiganwolverines", "Michigan Wolverines"), ("multileague", "Multi-League Arena"), ("myteam", "MyTEAM"),
    ("nets2012", "Brooklyn Nets 2012"), ("nuggets2016", "Denver Nuggets 2016"), ("ohiostatebuckeyes", "Ohio State Buckeyes"), ("pacers2005", "Indiana Pacers 2005"),
    ("paris", "Paris"), ("pistons2016", "Detroit Pistons 2016"), ("purdueboilermakers", "Purdue Boilermakers"), ("raptors2016", "Toronto Raptors 2016"),
    ("rockets2003", "Houston Rockets 2003"), ("rockets2016", "Houston Rockets 2016"), ("scrimmage5v5", "Scrimmage 5v5"), ("spurs1998", "San Antonio Spurs 1998"),
    ("statechampionship", "State Championship / Academy Arena"), ("strasbourg", "Strasbourg / Minneapolis Lakers"), ("summerleaguegeneric", "Summer League Generic"),
    ("suns2016", "Phoenix Suns 2016"), ("texaslonghorns", "Texas Longhorns"), ("thunder2016", "Oklahoma City Thunder 2016"),
    ("timberwolves2011", "Minnesota Timberwolves 2011"), ("uclabruins", "UCLA Bruins"), ("uconnhuskies", "UConn Huskies"), ("unctarheels", "UNC Tar Heels"),
    ("unctarheelswomens", "UNC Tar Heels Womens"), ("wizards2014", "Washington Wizards 2014"), ("wnbaallstar2025", "WNBA All-Star 2025"),
];
pub const ADDITIONAL_FLOOR_SURFACES: &[(&str, &str)] = &[
    ("floor_301_court_basecolor", "Atlanta Dream (301) Court Wood1"),
    ("floor_307_court_basecolor", "Chicago Sky (307) Court Wood1"),
    ("floor_308_court_basecolor", "Los Angeles Sparks / Lakers Arena (308) Court Wood1"),
    ("floor_309_court_basecolor", "Seattle Storm (309) Court Wood1"),
    ("floor_310_court_basecolor", "Connecticut Sun (310) Court Wood1"),
    ("floor_311_court_basecolor", "Dallas Wings (311) Court Wood1"),
    ("floor_316_court_wood1_basecolor_blue", "Toronto Tempo (316) Court Wood1"),
    ("floor_412_court_wood_angled_basecolor", "Unknown Arena (412) Angled Court Wood1"),
    ("floor_622_court_basecolor", "2000-01 Los Angeles Lakers (622) Court Wood1"),
    ("floor_626_court_basecolor", "2004-05 Phoenix Suns (626) Court Wood1"),
    ("floor_628_court_basecolor", "2006-07 Cleveland Cavaliers (628) Court Wood2"),
    ("floor_630_court_light_basecolor", "2007-08 Houston Rockets (630) Court Wood1"),
    ("floor_630_court_dark_basecolor", "2007-08 Houston Rockets (630) Court Wood2"),
    ("floor_648_court_basecolor", "2013-14 Los Angeles Clippers (648) Court Wood1"),
    ("floor_800_court_basecolor", "Historic Decades Arena (800) Court Wood1"),
    ("floor_854_court_basecolor", "Expansion Arena 854 (854) Court Wood1"),
    ("floor_855_court_basecolor", "Expansion Arena 855 (855) Court Wood1"),
    ("floor_855_court_lightwood_basecolor", "Expansion Arena 855 (855) Court Wood2"),
    ("floor_856_court_basecolor", "Expansion Arena 856 (856) Court Wood1"),
    ("floor_856_court_lightewood_basecolor", "Expansion Arena 856 (856) Court Wood2"),
    ("floor_857_court_basecolor", "Expansion Arena 857 (857) Court Wood1"),
    ("floor_860_court_basecolor", "Expansion Arena 860 (860) Court Wood1"),
    ("floor_924_court_basecolor", "2018-19 Toronto Raptors (924) Court Wood1"),
    ("floor_allstar2023_court_basecolor", "NBA All-Star 2023 Court Wood1"),
    ("floor_brickleyfacility_court_basecolor", "Brickley Facility Court Wood1"),
    ("floor_decades_court_basecolor", "Decades All-Star Arena Court Wood1"),
    ("floor_fiba_court_basecolor", "FIBA / International Arena Court Wood1"),
    ("floor_hornets2002_court_basecolor", "Charlotte Hornets 2002 Court Wood1"),
    ("floor_jazz1986_court_basecolor", "Utah Jazz 1986 Court Wood1"),
    ("floor_myteam250ktournament_court_basecolor", "MyTEAM 250K Tournament Court Wood1"),
    ("floor_top10rep_court_wood1", "Top 10 Rep Court Wood1"),
    ("floor_top10rep_court_wood2", "Top 10 Rep Court Wood2"),
    ("floor_usab2016_court_basecolor", "USA Basketball Court Wood1"),
];

fn lookup(table: &[(&str, &'static str)], key: &str) -> Option<&'static str> {
    table.iter().find(|(k, _)| *k == key).map(|(_, v)| *v)
}

/// Python's `name.rsplit(".", 2)[0]`.
fn strip_two_suffixes(name: &str) -> &str {
    let mut text = name;
    for _ in 0..2 {
        if let Some(index) = text.rfind('.') {
            text = &text[..index];
        }
    }
    text
}

/// `PurePath.suffix`: the last `.part` unless the name starts or ends with the dot.
fn suffix(name: &str) -> &str {
    match name.rfind('.') {
        Some(index) if index > 0 && index < name.len() - 1 => &name[index..],
        _ => "",
    }
}

fn stem(name: &str) -> &str {
    &name[..name.len() - suffix(name).len()]
}

/// Port of `surface_key` (texture name without its hash and `.mip0`, case-folded).
pub fn surface_key(name: &str) -> String {
    strip_two_suffixes(name).to_lowercase()
}

fn is_supported(name: &str) -> bool {
    WOOD_FLOOR.is_match(name) || lookup(ADDITIONAL_FLOOR_SURFACES, &surface_key(name)).is_some()
}

/// Port of `template_id_for`.
pub fn template_id_for(name: &str, game_version: &str) -> String {
    static UNSAFE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[^a-z0-9]+").unwrap());
    let lower = strip_two_suffixes(name).to_lowercase();
    let safe = UNSAFE.replace_all(&lower, "-");
    format!("nba{game_version}-{}", safe.trim_matches('-'))
}

fn int_token(token: &str) -> u128 {
    if !token.is_empty() && token.bytes().all(|b| b.is_ascii_digit()) { token.parse().unwrap_or(u128::MAX) } else { 9000 }
}

/// Port of `candidate_sort_key`: (arena number or 9000, wood number, file name).
pub fn candidate_sort_key(name: &str) -> (u128, u128, String) {
    static TOKEN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^floor_([a-z0-9]+)_court_").unwrap());
    static WOOD: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)wood([0-9]+)").unwrap());
    if let Some(found) = WOOD_FLOOR.captures(name) {
        let wood = found.name("wood").map(|m| m.as_str()).filter(|w| !w.is_empty()).unwrap_or("1");
        return (int_token(&found["floor"]), wood.parse().unwrap_or(u128::MAX), name.to_string());
    }
    let key = surface_key(name);
    let token = TOKEN.captures(&key).map(|c| c[1].to_string()).unwrap_or_default();
    let wood = lookup(ADDITIONAL_FLOOR_SURFACES, &key).and_then(|label| WOOD.captures(label)).map(|c| c[1].parse().unwrap_or(u128::MAX)).unwrap_or(1);
    (int_token(&token), wood, name.to_string())
}

/// Port of `title_floor_token`.
fn title_floor_token(value: &str) -> String {
    static WORDS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[a-z]+|[0-9]+").unwrap());
    WORDS
        .find_iter(value)
        .map(|word| match word.as_str() {
            "allstar" => "All-Star".to_string(),
            "aau" => "AAU".to_string(),
            "myteam" => "MyTEAM".to_string(),
            "wnba" => "WNBA".to_string(),
            "5v5" => "5v5".to_string(),
            other => title(other),
        })
        .collect::<Vec<_>>()
        .join(" ")
}

/// `str.title()` for a single ASCII-lowercase word.
fn title(word: &str) -> String {
    let mut chars = word.chars();
    chars.next().map(|first| first.to_uppercase().chain(chars).collect()).unwrap_or_default()
}

/// Port of `team_floor_name`.
fn team_floor_name(floor_token: &str, wood: &str) -> Option<String> {
    static VARIANT: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^(?P<arena>[0-9]{3})(?P<variant>[a-z]+)$").unwrap());
    if let Some(team) = lookup(ARENA_TEAM_NAMES, floor_token) {
        return Some(format!("{team} ({floor_token}) Court {wood}"));
    }
    let found = VARIANT.captures(floor_token)?;
    let arena = &found["arena"];
    let team = lookup(ARENA_TEAM_NAMES, arena)?;
    let variant = &found["variant"];
    let label = lookup(VARIANT_LABELS, variant).map(str::to_string).unwrap_or_else(|| title(variant));
    Some(format!("{team} ({arena}) {label} Court {wood}"))
}

/// Port of `friendly_name` for a `.mip0` file name.
pub fn friendly_name(name: &str) -> String {
    if let Some(additional) = lookup(ADDITIONAL_FLOOR_SURFACES, &surface_key(name)) {
        return additional.to_string();
    }
    let Some(found) = WOOD_FLOOR.captures(name) else { return stem(name).to_string() };
    let floor_token = found["floor"].to_lowercase();
    let wood = format!("Wood{}", found.name("wood").map(|m| m.as_str()).filter(|w| !w.is_empty()).unwrap_or("1"));
    if let Some(mapped) = team_floor_name(&floor_token, &wood) {
        return mapped;
    }
    for table in [WNBA_ARENA_NAMES, HISTORIC_ARENA_NAMES, EVENT_ARENA_NAMES] {
        if let Some(arena) = lookup(table, &floor_token) {
            return format!("{arena} ({floor_token}) Court {wood}");
        }
    }
    if let Some(special) = lookup(SPECIAL_FLOOR_NAMES, &floor_token) {
        return format!("{special} Court {wood}");
    }
    format!("{} Court {wood}", title_floor_token(&floor_token))
}

/// Port of `category_for_name`.
pub fn category_for_name(name: &str) -> &'static str {
    static WNBA: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\((30[0-9]|31[01567])\)").unwrap());
    static ARENA: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\(([0-9]{3})\)").unwrap());
    static UNKNOWN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\(4[0-9]{2}\)").unwrap());
    static UNKNOWN_TOKEN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^4[0-9]{2}courtwood").unwrap());
    static NBA_TOKEN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^[0-9]{3}courtwood").unwrap());
    let spaced = name.to_lowercase();
    let token = spaced.replace(' ', "");
    if token.contains("city") {
        return "City Edition";
    }
    if token.contains("statement") {
        return "Statement Edition";
    }
    if token.contains("classic") {
        return "Classic Edition";
    }
    if token.contains("wnba") || WNBA.is_match(&spaced) {
        return "WNBA";
    }
    let arena = ARENA.captures(&spaced).map(|c| c[1].to_string());
    if arena.as_deref().is_some_and(|a| lookup(HISTORIC_ARENA_NAMES, a).is_some()) {
        return "Historic NBA";
    }
    if arena.as_deref().is_some_and(|a| lookup(EVENT_ARENA_NAMES, a).is_some()) {
        return "All-Star & Events";
    }
    if token.contains("allstar") || token.contains("event") {
        return "All-Star & Events";
    }
    let any = |keys: &[&str]| keys.iter().any(|key| token.contains(key));
    if any(COLLEGE_FLOOR_KEYS) {
        return "College";
    }
    if any(HISTORIC_NBA_FLOOR_KEYS) {
        return "Historic NBA";
    }
    if any(INTERNATIONAL_FLOOR_KEYS) {
        return "International";
    }
    if any(MODE_FLOOR_KEYS) {
        return "Modes & Generic";
    }
    if UNKNOWN.is_match(&spaced) || UNKNOWN_TOKEN.is_match(&token) {
        return "Unknown";
    }
    if arena.is_some() && !["city", "statement", "classic", "event"].iter().any(|v| token.contains(v)) {
        return "NBA";
    }
    if NBA_TOKEN.is_match(&token) {
        return "NBA";
    }
    "Special"
}

/// Port of `read_tld_metadata`: (width, height, mip-chain bytes).
pub fn read_tld_metadata(data: &[u8]) -> Result<(u32, u32, u32)> {
    if data.len() < 32 || &data[..4] != b"TLD " {
        return Err(Error::msg("Not a TLD sidecar."));
    }
    let width = u16::from_le_bytes([data[16], data[17]]) as u32;
    let height = u16::from_le_bytes([data[18], data[19]]) as u32;
    let chain = u32::from_le_bytes([data[24], data[25], data[26], data[27]]);
    if width == 0 || height == 0 || chain == 0 {
        return Err(Error::msg("Could not read texture metadata from a TLD sidecar."));
    }
    Ok((width, height, chain))
}

/// Port of `top_mip_bytes`.
pub fn top_mip_bytes(width: u32, height: u32, block_bytes: u64) -> u64 {
    (width as u64).div_ceil(4).max(1) * (height as u64).div_ceil(4).max(1) * block_bytes
}

/// Port of `mip_chain_bytes`.
pub fn mip_chain_bytes(mut width: u32, mut height: u32, block_bytes: u64) -> u64 {
    let mut total = 0;
    loop {
        total += top_mip_bytes(width, height, block_bytes);
        if width <= 1 && height <= 1 {
            return total;
        }
        width = (width / 2).max(1);
        height = (height / 2).max(1);
    }
}

/// Port of `choose_format(..., "auto")`: the block format whose full chain matches the TLD.
pub fn choose_format(width: u32, height: u32, chain_bytes: u32) -> Result<(&'static str, u64)> {
    for (fourcc, block) in [("DXT1", 8), ("DXT5", 16)] {
        if mip_chain_bytes(width, height, block) == chain_bytes as u64 {
            return Ok((fourcc, top_mip_bytes(width, height, block)));
        }
    }
    Err(Error::msg(format!(
        "Unknown DDS block format for {width}x{height}, chain bytes {chain_bytes}. Expected one of: DXT1={}, DXT5={}",
        mip_chain_bytes(width, height, 8),
        mip_chain_bytes(width, height, 16)
    )))
}

/// Port of `make_dds`: a minimal single-level FourCC DDS around block data.
pub fn make_dds(width: u32, height: u32, fourcc: &str, texture: &[u8]) -> Vec<u8> {
    let mut header = [0u8; 124];
    let mut put = |offset: usize, value: u32| header[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
    put(0, 124);
    put(4, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000);
    put(8, height);
    put(12, width);
    put(16, texture.len() as u32);
    put(72, 32);
    put(76, 0x4);
    put(104, 0x1000);
    header[80..84].copy_from_slice(&fourcc.as_bytes()[..4]);
    let mut out = Vec::with_capacity(128 + texture.len());
    out.extend_from_slice(b"DDS ");
    out.extend_from_slice(&header);
    out.extend_from_slice(texture);
    out
}

#[inline]
fn median9(mut p: [u8; 9]) -> u8 {
    let mut s = |a: usize, b: usize| {
        if p[a] > p[b] {
            p.swap(a, b);
        }
    };
    for (a, b) in [(1, 2), (4, 5), (7, 8), (0, 1), (3, 4), (6, 7), (1, 2), (4, 5), (7, 8), (0, 3), (5, 8), (4, 7), (3, 6), (1, 4), (2, 5), (4, 7), (4, 2), (6, 4), (4, 2)] {
        s(a, b);
    }
    p[4]
}

/// Pillow `MedianFilter(3)` on the RGB bands (edges replicated, as `Image.expand` does).
fn median_rgb(image: &Rgba) -> Rgba {
    let (w, h) = (image.width as usize, image.height as usize);
    let mut out = vec![0u8; w * h * 4];
    out.par_chunks_exact_mut(w * 4).enumerate().for_each(|(y, line)| {
        let rows = [y.saturating_sub(1), y, (y + 1).min(h - 1)].map(|r| &image.data[r * w * 4..(r + 1) * w * 4]);
        for x in 0..w {
            let xs = [x.saturating_sub(1), x, (x + 1).min(w - 1)];
            for c in 0..3 {
                let mut values = [0u8; 9];
                for (i, row) in rows.iter().enumerate() {
                    for (j, &xx) in xs.iter().enumerate() {
                        values[i * 3 + j] = row[xx * 4 + c];
                    }
                }
                line[x * 4 + c] = median9(values);
            }
            line[x * 4 + 3] = 255;
        }
    });
    Rgba { width: image.width, height: image.height, data: out }
}

/// Port of `clean_decode_speckles` (numpy variant): two passes replacing dark, blue and
/// transparent decode outliers by their 3x3 median. Returns opaque RGBA.
pub fn clean_decode_speckles(image: &Rgba) -> Rgba {
    let alpha: Vec<u8> = image.data.chunks_exact(4).map(|p| p[3]).collect();
    let mut cleaned = image.clone();
    for _ in 0..2 {
        let median = median_rgb(&cleaned);
        cleaned.data.par_chunks_exact_mut(4).zip(median.data.par_chunks_exact(4)).zip(alpha.par_iter()).for_each(|((p, m), &a)| {
            let (r, g, b) = (p[0] as i32, p[1] as i32, p[2] as i32);
            let (mr, mg, mb) = (m[0] as i32, m[1] as i32, m[2] as i32);
            let luma = (r * 30 + g * 59 + b * 11) / 100;
            let median_luma = (mr * 30 + mg * 59 + mb * 11) / 100;
            let delta = (r - mr).abs() + (g - mg).abs() + (b - mb).abs();
            let dark = median_luma - luma > 24 && luma < 170 && delta > 30;
            let blue = b > g + 10 || b > r + 8 || (b > mb + 18 && delta > 24);
            if a < 255 || dark || blue {
                p[..3].copy_from_slice(&m[..3]);
            }
        });
    }
    cleaned.set_alpha(255);
    cleaned
}

/// Pillow `thumbnail` target size (`preserve_aspect_ratio`), or None when no resize is needed.
fn thumbnail_size(width: u32, height: u32, max_width: u32, max_height: u32) -> Option<(u32, u32)> {
    if max_width >= width && max_height >= height {
        return None;
    }
    let (x, y) = (max_width as f64, max_height as f64);
    let aspect = width as f64 / height as f64;
    let round_aspect = |number: f64, key: &dyn Fn(f64) -> f64| -> u32 {
        let (low, high) = (number.floor(), number.ceil());
        let chosen = if key(high) < key(low) { high } else { low };
        chosen.max(1.0) as u32
    };
    if x / y >= aspect {
        Some((round_aspect(y * aspect, &|n| (aspect - n / y).abs()), max_height))
    } else {
        Some((max_width, round_aspect(x / aspect, &|n| if n == 0.0 { 0.0 } else { (aspect - x / n).abs() })))
    }
}

/// Pillow `Image.reduce(factor, box)` (Reduce.c box averaging with its fixed-point rounding).
fn reduce(image: &Rgba, fx: usize, fy: usize, bx: [usize; 4]) -> Rgba {
    let (bw, bh) = (bx[2] - bx[0], bx[3] - bx[1]);
    let (ow, oh) = (bw.div_ceil(fx), bh.div_ceil(fy));
    let w = image.width as usize;
    let mut out = vec![0u8; ow * oh * 4];
    out.par_chunks_exact_mut(ow * 4).enumerate().for_each(|(oy, line)| {
        let y0 = bx[1] + oy * fy;
        let y1 = (y0 + fy).min(bx[3]);
        for ox in 0..ow {
            let x0 = bx[0] + ox * fx;
            let x1 = (x0 + fx).min(bx[2]);
            let scale = ((x1 - x0) * (y1 - y0)) as u32;
            let multiplier = (4294967296.0f32 / (256 * scale) as f32) as u32 as u64;
            let amend = (scale / 2) as u64;
            let mut sums = [amend; 4];
            for y in y0..y1 {
                for x in x0..x1 {
                    for c in 0..4 {
                        sums[c] += image.data[(y * w + x) * 4 + c] as u64;
                    }
                }
            }
            for c in 0..4 {
                line[ox * 4 + c] = ((sums[c] * multiplier) as u32 as u64 >> 24) as u8;
            }
        }
    });
    Rgba { width: ow as u32, height: oh as u32, data: out }
}

/// Pillow `image.convert("RGB").thumbnail(size, LANCZOS)` with its default `reducing_gap=2.0`
/// (box `reduce` first, then Lanczos on the remaining box). Alpha must already be opaque.
pub fn pillow_thumbnail(image: &Rgba, max_width: u32, max_height: u32) -> Rgba {
    let Some((width, height)) = thumbnail_size(image.width, image.height, max_width, max_height) else { return image.clone() };
    if (width, height) == (image.width, image.height) {
        return image.clone();
    }
    let (iw, ih) = (image.width as f64, image.height as f64);
    let mut box_ = [0.0, 0.0, iw, ih];
    let gap = 2.0;
    let factor_x = ((iw / width as f64 / gap) as usize).max(1);
    let factor_y = ((ih / height as f64 / gap) as usize).max(1);
    let mut source = std::borrow::Cow::Borrowed(image);
    if factor_x > 1 || factor_y > 1 {
        // `_get_safe_box` with Lanczos support 3.0.
        let support_x = 2.5 * (box_[2] - box_[0]) / width as f64;
        let support_y = 2.5 * (box_[3] - box_[1]) / height as f64;
        let safe = [
            ((box_[0] - support_x) as i64).max(0) as usize,
            ((box_[1] - support_y) as i64).max(0) as usize,
            ((box_[2] + support_x).ceil() as usize).min(image.width as usize),
            ((box_[3] + support_y).ceil() as usize).min(image.height as usize),
        ];
        source = std::borrow::Cow::Owned(reduce(image, factor_x, factor_y, safe));
        let (fx, fy) = (factor_x as f64, factor_y as f64);
        box_ = [(box_[0] - safe[0] as f64) / fx, (box_[1] - safe[1] as f64) / fy, (box_[2] - safe[0] as f64) / fx, (box_[3] - safe[1] as f64) / fy];
    }
    // Pillow parses the resize box as C floats.
    raster::resample(&source, width, height, Filter::Lanczos, box_.map(|v| v as f32 as f64))
}

/// A supported texture pair found in the manifest.
#[derive(Clone, Debug)]
pub struct Candidate {
    /// The `.mip0` file name (e.g. `floor_000_court_wood1_basecolor.<hash>.mip0`).
    pub name: String,
    pub mip0: ManifestEntry,
    pub tld: ManifestEntry,
}

/// Normalized `PurePosixPath` parts (empty and `.` components dropped).
fn posix_parts(path: &str) -> Vec<&str> {
    let mut parts: Vec<&str> = path.split('/').filter(|p| !p.is_empty() && *p != ".").collect();
    if path.starts_with('/') {
        parts.insert(0, "/");
    }
    parts
}

/// Manifest scan of `prepare_portable.main` plus `find_candidates` de-duplication and the
/// `BROKEN_SOURCE_TEXTURES` filter. Validation errors abort, as in Python.
pub fn find_candidates(game_root: &Path, manifest: &[ManifestEntry]) -> Result<Vec<Candidate>> {
    let game = files::normalized(game_root);
    let mut entries: HashMap<String, ManifestEntry> = HashMap::new();
    let mut checked_archives = std::collections::HashSet::new();
    for entry in manifest {
        let parts = posix_parts(&entry.path);
        let Some(name) = parts.last().copied() else { continue };
        let ext = suffix(name);
        if parts.first() != Some(&"shared") || !(ext == ".mip0" || ext == ".tld") {
            continue;
        }
        let texture_name = format!("{}.mip0", stem(name));
        if !is_supported(&texture_name) {
            continue;
        }
        let joined = parts.join("/");
        if parts.contains(&"..") || joined.contains(':') || joined.contains('\\') {
            return Err(Error::msg("Invalid game texture path"));
        }
        if entry.size == 0 || entry.size > MAX_MEMBER_BYTES {
            return Err(Error::msg("Invalid texture range"));
        }
        if !checked_archives.contains(&entry.chunk) {
            if !files::normalized(&game.join(&entry.chunk)).starts_with(&game) {
                return Err(Error::msg("Archive path leaves the game folder"));
            }
            checked_archives.insert(entry.chunk.clone());
        }
        entries.insert(joined, entry.clone());
    }
    let mut found: Vec<(String, Candidate)> = Vec::new();
    for (path, entry) in &entries {
        let name = path.rsplit('/').next().unwrap_or(path);
        if suffix(name) != ".mip0" || !is_supported(name) {
            continue;
        }
        let partner = format!("{}.tld", &path[..path.len() - 5]);
        if let Some(tld) = entries.get(&partner) {
            found.push((path.clone(), Candidate { name: name.to_string(), mip0: entry.clone(), tld: tld.clone() }));
        }
    }
    if found.is_empty() {
        return Err(Error::msg("The installed game has no supported floor textures."));
    }
    // Deterministic stand-in for `rglob` order, then the stable Python sort.
    found.sort_by(|a, b| a.0.cmp(&b.0));
    found.sort_by_cached_key(|(_, c)| candidate_sort_key(&c.name));
    let mut seen = std::collections::HashSet::new();
    let candidates: Vec<Candidate> = found
        .into_iter()
        .map(|(_, c)| c)
        .filter(|c| seen.insert(friendly_name(&c.name)))
        .filter(|c| !BROKEN_SOURCE_TEXTURES.contains(&surface_key(&c.name).as_str()))
        .collect();
    if candidates.is_empty() {
        return Err(Error::msg("No supported floors were found."));
    }
    Ok(candidates)
}

fn catalog_dir(library_root: &Path) -> PathBuf {
    library_root.join("court_floor_templates").join("nba2k27")
}

/// Index path relative to the library root, with Python's Windows separators.
fn relative(kind: &str, file: &str) -> String {
    format!("court_floor_templates\\nba2k27\\{kind}\\{file}")
}

/// Port of `prepare_image`: decodes, cleans and writes one floor (reusing existing files).
pub fn prepare_image(candidate: &Candidate, game_root: &Path, catalog: &Path, cancelled: &dyn Fn() -> bool) -> Result<Value> {
    let tld = game::read_member(game_root, &candidate.tld, MAX_MEMBER_BYTES).map_err(|e| e.context("Could not read the floor texture metadata: "))?;
    let (width, height, chain) = read_tld_metadata(&tld)?;
    let (fourcc, raw_size) = choose_format(width, height, chain)?;
    let identity = template_id_for(&candidate.name, GAME_VERSION);
    let png = catalog.join("images").join(format!("{identity}.png"));
    let thumbnail = catalog.join("thumbnails").join(format!("{identity}.jpg"));
    let display_height = height / 2;
    let mut image = None;
    if !png.exists() {
        check(cancelled)?;
        let compressed = game::read_member(game_root, &candidate.mip0, MAX_MEMBER_BYTES).map_err(|e| e.context("Could not read the floor texture: "))?;
        let texture = oodle::decompress(game_root, &compressed, raw_size as usize)?;
        let size = top_mip_bytes(width, display_height, if fourcc == "DXT1" { 8 } else { 16 }) as usize;
        let top = texture.get(..size).ok_or_else(|| Error::msg("The floor texture is truncated."))?;
        let decoded = raster::decode_dds(&make_dds(width, display_height, fourcc, top))?;
        check(cancelled)?;
        let cleaned = clean_decode_speckles(&decoded);
        drop(decoded);
        check(cancelled)?;
        files::write_atomic(&png, &raster::encode_png(&cleaned, false)?, &[]).map_err(|e| e.context(&format!("Could not write {}: ", png.display())))?;
        image = Some(cleaned);
    }
    if !thumbnail.exists() {
        check(cancelled)?;
        let mut source = match image {
            Some(image) => image,
            None => raster::decode(&files::read_bounded(&png, 512 * 1024 * 1024)?, Some("png"))?,
        };
        source.set_alpha(255); // convert("RGB")
        let small = pillow_thumbnail(&source, 360, 180);
        files::write_atomic(&thumbnail, &raster::encode_jpeg_rgb(&small, 82)?, &[]).map_err(|e| e.context(&format!("Could not write {}: ", thumbnail.display())))?;
    }
    let name = friendly_name(&candidate.name);
    let category = category_for_name(&name);
    Ok(json!({
        "id": identity,
        "name": name,
        "path": relative("images", &format!("{identity}.png")),
        "thumbnailPath": relative("thumbnails", &format!("{identity}.jpg")),
        "texturePath": null,
        "width": width,
        "height": display_height,
        "format": fourcc,
        "cleaned": true,
        "category": category,
    }))
}

/// Port of `publish_catalog` (atomic `json.dumps(indent=2)` index).
fn publish_catalog(catalog: &Path, templates: &[Value], complete: bool) -> Result<()> {
    let document = json!({
        "name": "NBA 2K27 Floor Templates",
        "gameVersion": GAME_VERSION,
        "preparationComplete": complete,
        "templates": templates,
    });
    files::write_atomic(&catalog.join(INDEX_NAME), pyjson::dumps(&document, INDEX_STYLE).as_bytes(), &[])
}

fn complete_count(library_root: &Path) -> Option<usize> {
    let data = pyjson::read_file(&catalog_dir(library_root).join(INDEX_NAME), MAX_INDEX_BYTES, 32).ok()?;
    if data.get("preparationComplete").is_some_and(|v| v != &Value::Bool(true)) {
        return None;
    }
    let templates = data.get("templates")?.as_array()?;
    let exists = |item: &Value, key: &str| item.get(key).and_then(Value::as_str).is_some_and(|p| !p.contains('\0') && library_root.join(p).is_file());
    (!templates.is_empty() && templates.iter().all(|item| exists(item, "path") && exists(item, "thumbnailPath"))).then_some(templates.len())
}

/// Port of `ready()`: a complete index whose every image and thumbnail exists.
/// (Python also requires prepared stock geometry; that check belongs to the geometry port.)
pub fn ready(library_root: &Path) -> bool {
    complete_count(library_root).is_some()
}

/// Port of `prepare_portable.main` after game discovery: builds the floor library into
/// `<library_root>/court_floor_templates/nba2k27`, publishing a partial index after the first
/// floor and the complete, id-sorted index at the end. Returns the number of floors.
pub fn prepare(library_root: &Path, game_root: &Path, report: &(dyn Fn(Event) + Sync), cancelled: &(dyn Fn() -> bool + Sync)) -> Result<usize> {
    if let Some(count) = complete_count(library_root) {
        report(Event::Status("Court library is ready.".into()));
        return Ok(count);
    }
    let catalog = catalog_dir(library_root);
    std::fs::create_dir_all(&catalog)?;
    let _lock = FileLock::acquire(&catalog.join("prepare.lock"), Duration::from_secs(240))?;
    if let Some(count) = complete_count(library_root) {
        return Ok(count);
    }
    std::fs::create_dir_all(catalog.join("images"))?;
    std::fs::create_dir_all(catalog.join("thumbnails"))?;
    report(Event::Status("Reading floor textures from the installed game…".into()));
    let manifest = game::read_manifest(game_root)?;
    let candidates = find_candidates(game_root, &manifest)?;
    drop(manifest);
    check(cancelled)?;
    report(Event::Status("Preparing the first court floor…".into()));
    let mut result = vec![prepare_image(&candidates[0], game_root, &catalog, cancelled)?];
    publish_catalog(&catalog, &result, false)?;
    report(Event::FirstFloorReady);
    let total = candidates.len();
    let workers = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1).clamp(1, 4);
    let next = AtomicUsize::new(1);
    let stop = AtomicBool::new(false);
    let halted = || stop.load(Ordering::Relaxed) || cancelled();
    let mut failure: Option<Error> = None;
    std::thread::scope(|scope| {
        let (sender, receiver) = std::sync::mpsc::channel::<Result<Value>>();
        for _ in 0..workers.min(total.saturating_sub(1)) {
            let sender = sender.clone();
            let (next, candidates, catalog, halted) = (&next, &candidates, &catalog, &halted);
            scope.spawn(move || {
                loop {
                    if halted() {
                        break;
                    }
                    let index = next.fetch_add(1, Ordering::Relaxed);
                    let Some(candidate) = candidates.get(index) else { break };
                    let outcome = prepare_image(candidate, game_root, catalog, halted).map_err(|e| e.context(&format!("{}: ", candidate.name)));
                    if sender.send(outcome).is_err() {
                        break;
                    }
                }
            });
        }
        drop(sender);
        for outcome in receiver {
            match outcome {
                Ok(template) => {
                    result.push(template);
                    report(Event::Progress { done: result.len(), total });
                }
                Err(error) => {
                    stop.store(true, Ordering::Relaxed);
                    if failure.is_none() && !(error.0.ends_with(Error::cancelled().0.as_str()) && cancelled()) {
                        failure = Some(error);
                    }
                }
            }
        }
    });
    let sort = |items: &mut Vec<Value>| items.sort_by(|a, b| a["id"].as_str().cmp(&b["id"].as_str()));
    if let Some(error) = failure {
        return Err(error);
    }
    if cancelled() || result.len() < total {
        // Keep everything finished so far usable; the next run resumes from the files.
        sort(&mut result);
        publish_catalog(&catalog, &result, false)?;
        return Err(Error::cancelled());
    }
    sort(&mut result);
    publish_catalog(&catalog, &result, true)?;
    // Load check (`load_stock_state()['floorLibraryCount']`).
    let loaded = crate::catalog::load_library(library_root).map(|library| library.images.len()).unwrap_or(0);
    if loaded != result.len() {
        return Err(Error::msg("The new floor catalog failed its load check."));
    }
    report(Event::Status(format!("Court library ready: {} floors.", result.len())));
    Ok(result.len())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_and_categories() {
        let cases = [
            ("floor_000_court_wood1_basecolor.0123abcd.mip0", "Philadelphia 76ers (000) Court Wood1", "NBA"),
            ("floor_001statement_court_wood1_basecolor.ab.mip0", "Milwaukee Bucks (001) Statement Court Wood1", "Statement Edition"),
            ("floor_015city_court_wood2_basecolor.ab.mip0", "Los Angeles Lakers (015) City Court Wood2", "City Edition"),
            ("floor_005xmas_court_wood_basecolor.ab.mip0", "Boston Celtics (005) Xmas Court Wood1", "NBA"),
            ("floor_301_court_wood1_basecolor.ab.mip0", "Atlanta Dream (301) Court Wood1", "WNBA"),
            ("floor_553_court_wood1_basecolor.ab.mip0", "1987-93 Chicago Bulls / Chicago Stadium (553) Court Wood1", "Historic NBA"),
            ("floor_700_court_wood1_basecolor.ab.mip0", "Summer League (700) Court Wood1", "All-Star & Events"),
            ("floor_dukebluedevils_court_wood1_basecolor.ab.mip0", "Duke Blue Devils Court Wood1", "College"),
            ("floor_allstar2024_court_wood1_basecolor.ab.mip0", "All-Star 2024 Court Wood1", "Special"),
            ("floor_wnbaallstar2025_court_wood1_basecolor.ab.mip0", "WNBA All-Star 2025 Court Wood1", "WNBA"),
            ("floor_412_court_wood1_basecolor.ab.mip0", "412 Court Wood1", "Unknown"),
            ("floor_999_court_wood1_basecolor.ab.mip0", "999 Court Wood1", "NBA"),
            ("floor_scrimmage5v5_court_wood1_basecolor.ab.mip0", "Scrimmage 5v5 Court Wood1", "Modes & Generic"),
            ("floor_foo2k_court_wood3_basecolor.ab.mip0", "Foo 2 K Court Wood3", "Special"),
            ("floor_412_court_wood_angled_basecolor.ab.mip0", "Unknown Arena (412) Angled Court Wood1", "Unknown"),
            ("floor_usab2016_court_basecolor.ab.mip0", "USA Basketball Court Wood1", "International"),
            ("floor_top10rep_court_wood2.ab.mip0", "Top 10 Rep Court Wood2", "Modes & Generic"),
        ];
        for (file, name, category) in cases {
            assert_eq!(friendly_name(file), name, "{file}");
            assert_eq!(category_for_name(name), category, "{name}");
        }
        assert_eq!(friendly_name("floor_x_court_basecolor.ab.mip0"), "floor_x_court_basecolor.ab");
        assert!(is_supported("FLOOR_000_Court_Wood1_BaseColor.0A.mip0"));
        assert!(!is_supported("floor_000_court_wood1_basecolor.zz.mip0"));
        assert!(is_supported("floor_855_court_lightwood_basecolor.12.mip0"));
    }

    #[test]
    fn ids_and_sorting() {
        assert_eq!(template_id_for("floor_000_court_wood1_basecolor.0123.mip0", "2k27"), "nba2k27-floor-000-court-wood1-basecolor");
        assert_eq!(template_id_for("Floor__A.b.c.mip0", "2k27"), "nba2k27-floor-a-b");
        assert_eq!(surface_key("Floor_X.1.mip0"), "floor_x");
        assert_eq!(candidate_sort_key("floor_005_court_wood2_basecolor.a.mip0"), (5, 2, "floor_005_court_wood2_basecolor.a.mip0".into()));
        assert_eq!(candidate_sort_key("floor_city_court_wood_basecolor.a.mip0").0, 9000);
        assert_eq!(candidate_sort_key("floor_628_court_basecolor.a.mip0"), (628, 2, "floor_628_court_basecolor.a.mip0".into()));
        assert_eq!(candidate_sort_key("floor_top10rep_court_wood2.a.mip0").0, 9000);
        assert_eq!(candidate_sort_key("floor_top10rep_court_wood2.a.mip0").1, 2);
    }

    #[test]
    fn tld_and_formats() {
        let mut tld = vec![0u8; 32];
        tld[..4].copy_from_slice(b"TLD ");
        tld[16..18].copy_from_slice(&4096u16.to_le_bytes());
        tld[18..20].copy_from_slice(&4096u16.to_le_bytes());
        let chain = mip_chain_bytes(4096, 4096, 8) as u32;
        tld[24..28].copy_from_slice(&chain.to_le_bytes());
        assert_eq!(read_tld_metadata(&tld).unwrap(), (4096, 4096, chain));
        assert_eq!(choose_format(4096, 4096, chain).unwrap(), ("DXT1", 8 * 1024 * 1024));
        assert_eq!(choose_format(4096, 4096, mip_chain_bytes(4096, 4096, 16) as u32).unwrap().0, "DXT5");
        assert!(choose_format(4096, 4096, 7).is_err());
        assert!(read_tld_metadata(&tld[..31]).is_err());
        tld[0] = b'X';
        assert!(read_tld_metadata(&tld).is_err());
        assert_eq!(mip_chain_bytes(4, 4, 8), 8 + 8 + 8);
        let dds = make_dds(8, 4, "DXT1", &[0u8; 16]);
        assert_eq!(dds.len(), 128 + 16);
        assert_eq!(raster::decode_dds(&dds).unwrap().width, 8);
    }

    #[test]
    fn median_and_thumbnail_size() {
        let values = [9u8, 3, 7, 1, 8, 2, 6, 4, 5];
        assert_eq!(median9(values), 5);
        for seed in 0..500u32 {
            let v: [u8; 9] = std::array::from_fn(|i| ((seed.wrapping_mul(2654435761).wrapping_add(i as u32 * 40503)) >> 7) as u8);
            let mut sorted = v;
            sorted.sort();
            assert_eq!(median9(v), sorted[4]);
        }
        assert_eq!(thumbnail_size(4096, 2048, 360, 180), Some((360, 180)));
        assert_eq!(thumbnail_size(1000, 333, 360, 180), Some((360, 120)));
        assert_eq!(thumbnail_size(100, 50, 360, 180), None);
        let image = Rgba::new(4096, 2048, [10, 20, 30, 255]);
        let thumb = pillow_thumbnail(&image, 360, 180);
        assert_eq!((thumb.width, thumb.height), (360, 180));
        assert!(thumb.data.chunks_exact(4).all(|p| p[..3] == [10, 20, 30]));
    }
}
