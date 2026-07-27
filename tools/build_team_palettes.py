from __future__ import annotations

from html.parser import HTMLParser
import json
from pathlib import Path
import re
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "data" / "team_palettes.json"
BASE_URL = "https://teamcolorcodes.com"
START_PAGES = {
    "NBA": "https://teamcolorcodes.com/nba-team-color-codes/",
    "NCAA D1": "https://teamcolorcodes.com/ncaa-color-codes/",
}
NBA_TEAMS = {
    "Atlanta Hawks",
    "Boston Celtics",
    "Brooklyn Nets",
    "Charlotte Hornets",
    "Chicago Bulls",
    "Cleveland Cavaliers",
    "Dallas Mavericks",
    "Denver Nuggets",
    "Detroit Pistons",
    "Golden State Warriors",
    "Houston Rockets",
    "Indiana Pacers",
    "Los Angeles Clippers",
    "Los Angeles Lakers",
    "Memphis Grizzlies",
    "Miami Heat",
    "Milwaukee Bucks",
    "Minnesota Timberwolves",
    "New Orleans Pelicans",
    "New York Knicks",
    "Oklahoma City Thunder",
    "Orlando Magic",
    "Philadelphia 76ers",
    "Phoenix Suns",
    "Portland Trail Blazers",
    "Sacramento Kings",
    "San Antonio Spurs",
    "Toronto Raptors",
    "Utah Jazz",
    "Washington Wizards",
}


class LinkParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.links: list[tuple[str, str]] = []
        self._href: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag != "a":
            return
        attrs_dict = dict(attrs)
        self._href = attrs_dict.get("href")

    def handle_data(self, data: str) -> None:
        if self._href and data.strip():
            self.links.append((data.strip(), self._href))

    def handle_endtag(self, tag: str) -> None:
        if tag == "a":
            self._href = None


class TextParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.parts: list[str] = []
        self._ignored_depth = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag in {"script", "style"}:
            self._ignored_depth += 1

    def handle_endtag(self, tag: str) -> None:
        if tag in {"script", "style"} and self._ignored_depth:
            self._ignored_depth -= 1

    def handle_data(self, data: str) -> None:
        if self._ignored_depth:
            return
        text = " ".join(data.split())
        if text:
            self.parts.append(text)


def main() -> None:
    palettes: list[dict] = []
    seen_urls: set[str] = set()
    for league, url in START_PAGES.items():
        for team_name, team_url in team_links(url, league):
            if team_url in seen_urls:
                continue
            seen_urls.add(team_url)
            colors = page_colors(team_url)
            if not colors:
                continue
            palettes.append(
                {
                    "league": league,
                    "team": clean_team_name(team_name),
                    "source": team_url,
                    "colors": colors,
                }
            )

    palettes.sort(key=lambda item: (item["league"], item["team"]))
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(
        json.dumps(
            {
                "source": "https://teamcolorcodes.com",
                "generated_from": START_PAGES,
                "palettes": palettes,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"Wrote {len(palettes)} team palettes to {OUTPUT}")


def team_links(url: str, league: str) -> list[tuple[str, str]]:
    html = fetch(url)
    parser = LinkParser()
    parser.feed(html)
    links: list[tuple[str, str]] = []
    if league == "NBA":
        wanted = NBA_TEAMS
    else:
        text = page_text(html)
        team_section = text.split("Browse By Team", 1)[-1].split("Copyright", 1)[0]
        wanted = set(
            line
            for line in team_section.splitlines()
            if len(line) > 2
            and not line.startswith("Browse")
            and not re.fullmatch(r"[A-Z]", line)
            and not line.startswith("####")
        )

    for label, href in parser.links:
        if "color-codes" not in href and "-colors" not in href:
            continue
        label_name = label.replace(chr(8217), "'").replace(" Colors", "")
        if label_name not in wanted:
            continue
        absolute = href if href.startswith("http") else f"{BASE_URL}{href}"
        if absolute.rstrip("/") in {url.rstrip("/"), BASE_URL}:
            continue
        if league != "NBA" and any(skip in absolute for skip in ("/nba-", "/nfl-", "/nhl-", "/mlb-")):
            continue
        if league != "NBA" and label in {"NCAA", "NCAA Division II", "NCAA Division III"}:
            continue
        links.append((label_name, absolute))
    return links


def page_colors(url: str) -> list[dict]:
    text = page_text(fetch(url))
    matches = table_color_matches(text)
    if not matches:
        matches = estimate_color_matches(text)
    colors: list[dict] = []
    seen: set[str] = set()
    for name, hex_color in matches:
        name = clean_color_name(name)
        hex_color = hex_color.upper()
        if not name or hex_color in seen:
            continue
        seen.add(hex_color)
        colors.append({"name": name, "hex": hex_color})
    return colors[:8]


def table_color_matches(text: str) -> list[tuple[str, str]]:
    start = text.find("Color Name\nRGB Color Code")
    if start == -1:
        return []
    section = text[start:].split("Contents", 1)[0]
    lines = [line.strip() for line in section.splitlines() if line.strip()]
    try:
        index = lines.index("HEX Color Code") + 1
    except ValueError:
        return []
    matches: list[tuple[str, str]] = []
    while index < len(lines):
        name = lines[index]
        if name.startswith("#") or name.startswith("(") or name.startswith("PMS "):
            index += 1
            continue
        hex_index = None
        for offset in range(1, 7):
            if index + offset >= len(lines):
                break
            if re.fullmatch(r"#[0-9A-Fa-f]{6}", lines[index + offset]):
                hex_index = index + offset
                break
        if hex_index is None:
            index += 1
            continue
        matches.append((name, lines[hex_index]))
        index = hex_index + 1
    return matches


def estimate_color_matches(text: str) -> list[tuple[str, str]]:
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    matches: list[tuple[str, str]] = []
    for index, line in enumerate(lines):
        match = re.match(r"Hex\s+Color:\s*(#[0-9A-Fa-f]{6});?", line, re.IGNORECASE)
        if not match:
            continue
        name = ""
        for candidate in reversed(lines[max(0, index - 4) : index]):
            if candidate.startswith(("PANTONE:", "RGB:", "CMYK:", "Hex ")):
                continue
            name = candidate
            break
        if name:
            matches.append((name, match.group(1)))
    if matches:
        return matches
    return re.findall(
        r"([A-Za-z0-9 .'&\-/]+?)\s+Hex\s+Color:\s*(#[0-9A-Fa-f]{6});?",
        text,
        flags=re.IGNORECASE,
    )


def page_text(html: str) -> str:
    parser = TextParser()
    parser.feed(html)
    return "\n".join(parser.parts).replace(chr(8217), "'")


def fetch(url: str) -> str:
    request = Request(url, headers={"User-Agent": "NBA2KCourtCreator/1.0"})
    with urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8", errors="replace")


def clean_team_name(name: str) -> str:
    return " ".join(name.replace(chr(8217), "'").replace(" Colors", "").split())


def clean_color_name(name: str) -> str:
    name = re.sub(r"^(Image:|Buy Matching Paint|PANTONE:).*", "", name).strip()
    name = re.sub(r"^.*?([A-Z][A-Za-z0-9 .'&\-/]+)$", r"\1", name)
    name = " ".join(name.replace("colour", "color").split())
    if "Color Code" in name or name in {"Color Name", "HEX"}:
        return ""
    return name


if __name__ == "__main__":
    main()
