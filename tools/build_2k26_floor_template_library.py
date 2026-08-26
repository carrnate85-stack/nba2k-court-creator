from __future__ import annotations

import argparse
from io import BytesIO
import json
from pathlib import Path
import re

from PIL import Image, ImageFilter

from export_2k26_court_texture import (
    choose_format,
    make_dds,
    oodle_decompress,
    read_tld_metadata,
    top_mip_bytes,
)


WOOD_FLOOR = re.compile(
    r"^floor_(?P<floor>[a-z0-9]+)_court_wood(?P<wood>\d*)_basecolor\.[0-9a-f]+\.mip0$",
    re.IGNORECASE,
)
COLLEGE_FLOOR_KEYS = {
    "arizonawildcats",
    "baylorbears",
    "dukebluedevils",
    "floridagators",
    "houstoncougars",
    "kansasjayhawks",
    "kentuckywildcats",
    "louisvillecardinals",
    "michiganstatespartans",
    "michiganwolverines",
    "ohiostatebuckeyes",
    "purdueboilermakers",
    "texaslonghorns",
    "uclabruins",
    "uconnhuskies",
    "unctarheels",
}
HISTORIC_NBA_FLOOR_KEYS = {
    "bobcats2011",
    "bucks2015",
    "bulls2016",
    "cavaliers2011",
    "cavaliers2016",
    "clippers2015",
    "clippers2022",
    "grizzlies2016",
    "jazz2016",
    "kings2016",
    "knicks2016",
    "nets2012",
    "nuggets2016",
    "pacers2005",
    "pistons2016",
    "raptors2016",
    "rockets2003",
    "rockets2016",
    "spurs1998",
    "suns2016",
    "thunder2016",
    "timberwolves2011",
    "wizards2014",
}
INTERNATIONAL_FLOOR_KEYS = {
    "barcelona",
    "basquetmadrid",
    "fcmalaga",
    "fiba",
    "fibagame",
    "madrid",
    "multileague",
    "paris",
    "strasbourg",
}
MODE_FLOOR_KEYS = {
    "aau",
    "aaugym",
    "clutchtime",
    "gleagueignite",
    "hsgym",
    "matchmaking",
    "myteam",
    "scrimmage",
    "statechampionship",
    "summerleaguegeneric",
}
ARENA_TEAM_NAMES = {
    "000": "Philadelphia 76ers",
    "001": "Milwaukee Bucks",
    "002": "Washington Wizards",
    "003": "Chicago Bulls",
    "004": "Cleveland Cavaliers",
    "005": "Boston Celtics",
    "006": "Los Angeles Clippers",
    "008": "Memphis Grizzlies",
    "009": "Atlanta Hawks",
    "010": "Miami Heat",
    "011": "New Orleans Pelicans",
    "012": "Utah Jazz",
    "013": "Sacramento Kings",
    "014": "New York Knicks",
    "015": "Los Angeles Lakers",
    "016": "Orlando Magic",
    "017": "Dallas Mavericks",
    "018": "Brooklyn Nets",
    "019": "Denver Nuggets",
    "020": "Indiana Pacers",
    "021": "Detroit Pistons",
    "022": "Toronto Raptors",
    "023": "Houston Rockets",
    "024": "Oklahoma City Thunder",
    "025": "San Antonio Spurs",
    "026": "Phoenix Suns",
    "027": "Minnesota Timberwolves",
    "028": "Portland Trail Blazers",
    "029": "Golden State Warriors",
    "031": "Charlotte Hornets / Bobcats",
}
WNBA_ARENA_NAMES = {
    "300": "Las Vegas Aces",
    "301": "Atlanta Dream",
    "302": "Indiana Fever / Pacers Arena",
    "303": "New York Liberty / Nets Arena",
    "304": "Minnesota Lynx / Timberwolves Arena",
    "305": "Phoenix Mercury / Suns Arena",
    "306": "Washington Mystics",
    "307": "Chicago Sky",
    "308": "Los Angeles Sparks / Lakers Arena",
    "309": "Seattle Storm",
    "310": "Connecticut Sun",
    "311": "Dallas Wings",
    "315": "Golden State Valkyries / Chase Center",
    "316": "Toronto Tempo",
    "317": "Portland Fire",
}
HISTORIC_ARENA_NAMES = {
    "551": "1985-86 Chicago Bulls",
    "552": "1985-86 Boston Celtics / Boston Garden",
    "553": "1987-93 Chicago Bulls / Chicago Stadium",
    "554": "1986-87 Atlanta Hawks",
    "555": "1980-90 Cleveland Cavaliers",
    "556": "1989-90 Detroit Pistons",
    "557": "1990-91 Los Angeles Lakers",
    "558": "1990-91 Portland Trail Blazers",
    "559": "1995-96 Chicago Bulls / United Center",
    "560": "1994-95 New York Knicks",
    "562": "1995-96 Seattle SuperSonics",
    "564": "1997-98 Utah Jazz",
    "570": "1964-65 Los Angeles Lakers",
    "571": "1970-71 Milwaukee Bucks",
    "572": "1971-72 Los Angeles Lakers",
    "574": "1971-72 New York Knicks",
    "576": "1984-85 Milwaukee Bucks",
    "583": "1990-91 Golden State Warriors",
    "586": "1992-93 Charlotte Hornets",
    "588": "1993-94 Denver Nuggets",
    "589": "1993-94 Houston Rockets",
    "590": "1994-95 Orlando Magic",
    "591": "1997-98 Los Angeles Lakers",
    "592": "1997-98 San Antonio Spurs",
    "593": "2001-02 Sacramento Kings",
    "594": "1976-77 Philadelphia 76ers",
    "595": "2000-01 Philadelphia 76ers",
    "620": "1999-00 Toronto Raptors",
    "621": "1999-00 Portland Trail Blazers",
    "622": "2000-01 Los Angeles Lakers",
    "623": "2002-03 Dallas Mavericks",
    "624": "2003-04 Detroit Pistons",
    "625": "2003-04 Minnesota Timberwolves",
    "626": "2004-05 Phoenix Suns",
    "627": "2005-06 Miami Heat",
    "628": "2006-07 Cleveland Cavaliers",
    "629": "2007-08 Boston Celtics",
    "630": "2007-08 Houston Rockets",
    "631": "2012-13 Miami Heat",
    "632": "1996-97 Miami Heat",
    "633": "1999-00 New York Knicks",
    "634": "2015-16 / 2016-17 Golden State Warriors",
    "635": "2001-02 New Jersey Nets",
    "636": "2004-05 San Antonio Spurs",
    "637": "2006-07 Golden State Warriors",
    "638": "2006-07 Washington Wizards",
    "639": "2007-08 Denver Nuggets",
    "641": "2010-11 Chicago Bulls",
    "642": "2010-11 Dallas Mavericks",
    "644": "2011-12 New York Knicks",
    "645": "2002-03 Phoenix Suns",
    "646": "2009-10 Portland Trail Blazers",
    "647": "2013-14 San Antonio Spurs",
    "648": "2013-14 Los Angeles Clippers",
    "649": "2015-16 Cleveland Cavaliers",
    "924": "2018-19 Toronto Raptors",
}
EVENT_ARENA_NAMES = {
    "700": "Summer League",
    "701": "Generic Event Arena",
    "728": "2K Sports Practice Gym",
    "729": "2K15 / 2K Sports Practice Gym",
    "800": "Historic Decades Arena",
    "852": "Expansion Arena 852",
    "853": "Expansion Arena 853",
    "854": "Expansion Arena 854",
    "855": "Expansion Arena 855",
    "856": "Expansion Arena 856",
    "857": "Expansion Arena 857",
    "858": "Expansion Arena 858",
    "859": "Expansion Arena 859",
    "860": "Expansion Arena 860",
    "861": "Expansion Arena 861",
    "906": "Rec Center Gym",
}
VARIANT_LABELS = {
    "city": "City",
    "statement": "Statement",
    "classic": "Classic",
    "event": "Event",
}
SPECIAL_FLOOR_NAMES = {
    "305rebel": "UNLV Rebels",
    "aau": "AAU",
    "aaugym": "AAU Gym",
    "allstar2021": "NBA All-Star 2021",
    "allstar2022": "NBA All-Star 2022",
    "arizonawildcats": "Arizona Wildcats",
    "barcelona": "Barcelona",
    "basquetmadrid": "Basquet Madrid",
    "baylorbears": "Baylor Bears",
    "bobcats2011": "Charlotte Bobcats 2011",
    "bucks2015": "Milwaukee Bucks 2015",
    "bulls2016": "Chicago Bulls 2016",
    "cavaliers2011": "Cleveland Cavaliers 2011",
    "cavaliers2016": "Cleveland Cavaliers 2016",
    "clippers2015": "Los Angeles Clippers 2015",
    "clippers2022": "Los Angeles Clippers 2022",
    "clutchtime": "Clutch Time",
    "clutchtime1980": "Clutch Time 1980s",
    "clutchtime1990": "Clutch Time 1990s",
    "clutchtime2000": "Clutch Time 2000s",
    "dukebluedevils": "Duke Blue Devils",
    "floridagators": "Florida Gators",
    "decades": "Decades All-Star Arena",
    "fcmalaga": "FC Malaga",
    "fiba": "FIBA / International Arena",
    "fibagame": "FIBA Game Arena",
    "gleagueignite": "G League Ignite",
    "grizzlies2016": "Memphis Grizzlies 2005-06 / 2012-13",
    "houstoncougars": "Houston Cougars",
    "hsgym": "High School Gym",
    "jazz2016": "Utah Jazz 2016",
    "kansasjayhawks": "Kansas Jayhawks",
    "kentuckywildcats": "Kentucky Wildcats",
    "kentuckywildcatswomens": "Kentucky Wildcats Womens",
    "kings2016": "Sacramento Kings 2016",
    "knicks2016": "New York Knicks 2016",
    "lakers1983": "1986-87 Los Angeles Lakers",
    "louisvillecardinals": "Louisville Cardinals",
    "madrid": "Madrid",
    "matchmakingb1sponsor": "Matchmaking B1 Sponsor",
    "michiganstatespartans": "Michigan State Spartans",
    "michiganwolverines": "Michigan Wolverines",
    "multileague": "Multi-League Arena",
    "myteam": "MyTEAM",
    "nets2012": "Brooklyn Nets 2012",
    "nuggets2016": "Denver Nuggets 2016",
    "ohiostatebuckeyes": "Ohio State Buckeyes",
    "pacers2005": "Indiana Pacers 2005",
    "paris": "Paris",
    "pistons2016": "Detroit Pistons 2016",
    "purdueboilermakers": "Purdue Boilermakers",
    "raptors2016": "Toronto Raptors 2016",
    "rockets2003": "Houston Rockets 2003",
    "rockets2016": "Houston Rockets 2016",
    "scrimmage5v5": "Scrimmage 5v5",
    "spurs1998": "San Antonio Spurs 1998",
    "statechampionship": "State Championship / Academy Arena",
    "strasbourg": "Strasbourg / Minneapolis Lakers",
    "summerleaguegeneric": "Summer League Generic",
    "suns2016": "Phoenix Suns 2016",
    "texaslonghorns": "Texas Longhorns",
    "thunder2016": "Oklahoma City Thunder 2016",
    "timberwolves2011": "Minnesota Timberwolves 2011",
    "uclabruins": "UCLA Bruins",
    "uconnhuskies": "UConn Huskies",
    "unctarheels": "UNC Tar Heels",
    "unctarheelswomens": "UNC Tar Heels Womens",
    "wizards2014": "Washington Wizards 2014",
    "wnbaallstar2025": "WNBA All-Star 2025",
}


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build a small local NBA 2K26 court floor template library."
    )
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--extracted-root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--limit", type=int, default=8)
    parser.add_argument("--save-dds", action="store_true")
    parser.add_argument("--no-clean-speckles", action="store_true")
    args = parser.parse_args()

    game_root = Path(args.game_root)
    extracted_root = Path(args.extracted_root)
    output_root = Path(args.output)
    image_root = output_root / "images"
    dds_root = output_root / "dds"
    image_root.mkdir(parents=True, exist_ok=True)
    if args.save_dds:
        dds_root.mkdir(parents=True, exist_ok=True)

    candidates = find_candidates(extracted_root)
    if args.limit > 0:
        candidates = candidates[: args.limit]

    templates = []
    for index, mip0_path in enumerate(candidates, start=1):
        tld_path = mip0_path.with_suffix(".tld")
        width, height, chain_bytes = read_tld_metadata(tld_path)
        fourcc, raw_size = choose_format(width, height, chain_bytes, "auto")
        raw_texture = oodle_decompress(game_root, mip0_path.read_bytes(), raw_size)

        display_height = height // 2 if "court_wood" in mip0_path.name.lower() else height
        display_size = top_mip_bytes(width, display_height, 8 if fourcc == "DXT1" else 16)
        display_texture = raw_texture[:display_size]
        dds_data = make_dds(width, display_height, fourcc, display_texture)

        template_id = template_id_for(mip0_path)
        name = friendly_name(mip0_path)
        category = category_for_name(name)
        png_path = image_root / f"{template_id}.png"
        with Image.open(BytesIO(dds_data)) as image:
            display_image = image.convert("RGBA")
        if not args.no_clean_speckles:
            display_image = clean_decode_speckles(display_image)
        display_image.putalpha(255)
        display_image.save(png_path)

        dds_path = None
        if args.save_dds:
            dds_path = dds_root / f"{template_id}.dds"
            dds_path.write_bytes(dds_data)

        templates.append(
            {
                "id": template_id,
                "name": name,
                "path": relative_to_asset_root(png_path),
                "texturePath": relative_to_asset_root(dds_path) if dds_path else None,
                "sourceMip0": str(mip0_path),
                "sourceTld": str(tld_path),
                "width": width,
                "height": display_height,
                "format": fourcc,
                "cleaned": not args.no_clean_speckles,
                "category": category,
            }
        )
        print(f"{index}/{len(candidates)} {name}")

    index_path = output_root / "nba2k26_floor_templates.json"
    index_path.write_text(
        json.dumps(
            {
                "name": "NBA 2K26 Floor Templates",
                "templates": templates,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"wrote {index_path} ({len(templates)} templates)")


def find_candidates(extracted_root: Path) -> list[Path]:
    shared_root = extracted_root / "shared"
    candidates = [
        path
        for path in shared_root.rglob("*.mip0")
        if WOOD_FLOOR.match(path.name) and path.with_suffix(".tld").exists()
    ]
    unique: dict[str, Path] = {}
    for path in sorted(candidates, key=candidate_sort_key):
        unique.setdefault(friendly_name(path), path)
    return list(unique.values())


def candidate_sort_key(path: Path) -> tuple[int, int, str]:
    match = WOOD_FLOOR.match(path.name)
    if not match:
        return (9999, 99, path.name)
    floor = match.group("floor")
    wood = match.group("wood") or "1"
    number = int(floor) if floor.isdigit() else 9000
    return (number, int(wood), path.name)


def template_id_for(path: Path) -> str:
    name = path.name.rsplit(".", 2)[0]
    safe = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return f"nba2k26-{safe}"


def friendly_name(path: Path) -> str:
    match = WOOD_FLOOR.match(path.name)
    if not match:
        return path.stem

    floor_token = match.group("floor").casefold()
    wood = f"Wood{match.group('wood') or '1'}"
    mapped = team_floor_name(floor_token, wood)
    if mapped is not None:
        return mapped
    if floor_token in WNBA_ARENA_NAMES:
        return f"{WNBA_ARENA_NAMES[floor_token]} ({floor_token}) Court {wood}"
    if floor_token in HISTORIC_ARENA_NAMES:
        return f"{HISTORIC_ARENA_NAMES[floor_token]} ({floor_token}) Court {wood}"
    if floor_token in EVENT_ARENA_NAMES:
        return f"{EVENT_ARENA_NAMES[floor_token]} ({floor_token}) Court {wood}"
    if floor_token in SPECIAL_FLOOR_NAMES:
        return f"{SPECIAL_FLOOR_NAMES[floor_token]} Court {wood}"

    return f"{title_floor_token(floor_token)} Court {wood}"


def team_floor_name(floor_token: str, wood: str) -> str | None:
    if floor_token in ARENA_TEAM_NAMES:
        return f"{ARENA_TEAM_NAMES[floor_token]} ({floor_token}) Court {wood}"

    match = re.fullmatch(r"(?P<arena>\d{3})(?P<variant>[a-z]+)", floor_token)
    if not match:
        return None

    arena = match.group("arena")
    team = ARENA_TEAM_NAMES.get(arena)
    if team is None:
        return None

    variant = VARIANT_LABELS.get(match.group("variant"), match.group("variant").title())
    return f"{team} ({arena}) {variant} Court {wood}"


def title_floor_token(value: str) -> str:
    words = re.findall(r"[a-z]+|\d+", value)
    titled = []
    for word in words:
        if word == "allstar":
            titled.append("All-Star")
        elif word == "aau":
            titled.append("AAU")
        elif word == "myteam":
            titled.append("MyTEAM")
        elif word == "wnba":
            titled.append("WNBA")
        elif word == "5v5":
            titled.append("5v5")
        else:
            titled.append(word.title())
    return " ".join(titled)


def category_for_name(name: str) -> str:
    token = name.casefold().replace(" ", "")
    spaced = name.casefold()
    if "city" in token:
        return "City Edition"
    if "statement" in token:
        return "Statement Edition"
    if "classic" in token:
        return "Classic Edition"
    if "wnba" in token:
        return "WNBA"
    if re.search(r"\((30[0-9]|31[01567])\)", spaced):
        return "WNBA"
    historic_match = re.search(r"\((\d{3})\)", spaced)
    if historic_match and historic_match.group(1) in HISTORIC_ARENA_NAMES:
        return "Historic NBA"
    event_match = re.search(r"\((\d{3})\)", spaced)
    if event_match and event_match.group(1) in EVENT_ARENA_NAMES:
        return "All-Star & Events"
    if "allstar" in token or "event" in token:
        return "All-Star & Events"
    if any(key in token for key in COLLEGE_FLOOR_KEYS):
        return "College"
    if any(key in token for key in HISTORIC_NBA_FLOOR_KEYS):
        return "Historic NBA"
    if any(key in token for key in INTERNATIONAL_FLOOR_KEYS):
        return "International"
    if any(key in token for key in MODE_FLOOR_KEYS):
        return "Modes & Generic"
    if re.search(r"\(\d{3}\)", spaced) and all(
        variant not in token for variant in ("city", "statement", "classic", "event")
    ):
        return "NBA"
    if re.match(r"\d{3}courtwood", token):
        return "NBA"
    return "Special"


def clean_decode_speckles(image: Image.Image) -> Image.Image:
    try:
        import numpy as np
    except ImportError:
        return clean_decode_speckles_slow(image)

    source = image.convert("RGBA")
    source_alpha = source.getchannel("A")
    cleaned = source.convert("RGB")
    alpha_array = np.asarray(source_alpha)
    for _ in range(2):
        median = cleaned.filter(ImageFilter.MedianFilter(3))
        pixels = np.asarray(cleaned, dtype=np.int16)
        median_pixels = np.asarray(median, dtype=np.int16)
        luma = (
            pixels[:, :, 0] * 30 + pixels[:, :, 1] * 59 + pixels[:, :, 2] * 11
        ) // 100
        median_luma = (
            median_pixels[:, :, 0] * 30
            + median_pixels[:, :, 1] * 59
            + median_pixels[:, :, 2] * 11
        ) // 100
        color_delta = np.abs(pixels - median_pixels).sum(axis=2)
        dark_outlier = (median_luma - luma > 24) & (luma < 170) & (color_delta > 30)
        blue_outlier = (
            (pixels[:, :, 2] > pixels[:, :, 1] + 10)
            | (pixels[:, :, 2] > pixels[:, :, 0] + 8)
            | ((pixels[:, :, 2] > median_pixels[:, :, 2] + 18) & (color_delta > 24))
        )
        transparent_outlier = alpha_array < 255
        mask_array = np.where(
            transparent_outlier | dark_outlier | blue_outlier, 255, 0
        ).astype("uint8")
        mask = Image.fromarray(mask_array)
        cleaned = Image.composite(median, cleaned, mask)
    cleaned.putalpha(255)
    return cleaned


def clean_decode_speckles_slow(image: Image.Image) -> Image.Image:
    source = image.convert("RGBA")
    source_alpha = source.getchannel("A")
    cleaned = source.convert("RGB")
    for _ in range(2):
        median = cleaned.filter(ImageFilter.MedianFilter(3))
        mask = Image.new("L", cleaned.size, 0)
        pixels = cleaned.load()
        median_pixels = median.load()
        alpha_pixels = source_alpha.load()
        mask_pixels = mask.load()
        width, height = cleaned.size
        for y in range(height):
            for x in range(width):
                red, green, blue = pixels[x, y]
                median_red, median_green, median_blue = median_pixels[x, y]
                luma = (red * 30 + green * 59 + blue * 11) // 100
                median_luma = (
                    median_red * 30 + median_green * 59 + median_blue * 11
                ) // 100
                color_delta = (
                    abs(red - median_red)
                    + abs(green - median_green)
                    + abs(blue - median_blue)
                )
                dark_outlier = median_luma - luma > 24 and luma < 170 and color_delta > 30
                blue_outlier = (
                    blue > green + 10
                    or blue > red + 8
                    or (blue > median_blue + 18 and color_delta > 24)
                )
                transparent_outlier = alpha_pixels[x, y] < 255
                if transparent_outlier or dark_outlier or blue_outlier:
                    mask_pixels[x, y] = 255
        cleaned = Image.composite(median, cleaned, mask)
    cleaned.putalpha(255)
    return cleaned


def relative_to_asset_root(path: Path | None) -> str | None:
    if path is None:
        return None
    asset_root = Path.home() / "OneDrive" / "Documents" / "2kcourtmodder"
    try:
        return str(path.relative_to(asset_root))
    except ValueError:
        return str(path)


if __name__ == "__main__":
    main()
