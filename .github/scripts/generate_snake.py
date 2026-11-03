import json
import os
import sys
from datetime import date, timedelta
from pathlib import Path
from urllib.request import Request, urlopen


THEMES = {
    "dark": {
        "card": "#1C1B20", "surface": "#26252C", "outline": "#45444E",
        "text": "#EAE7F0", "muted": "#8F8B99", "ring": "#D0BCFF",
        "heat": ["#3B3A49", "#57567A", "#7B7FC0", "#9BA0E8", "#C3C6F6"],
        "heat_text": ["#C6C2D0", "#E8E6F2", "#14131C", "#14131C", "#14131C"],
    },
    "light": {
        "card": "#FCFAFF", "surface": "#F1EDF6", "outline": "#C9C3D2",
        "text": "#1B1820", "muted": "#79747E", "ring": "#6F55B8",
        "heat": ["#E7E4EE", "#C9CCF4", "#A3A8EC", "#7B80DC", "#5C61C4"],
        "heat_text": ["#4A4552", "#26243A", "#1B1A2E", "#14131C", "#FFFFFF"],
    },
}

BADGE = "M 51.0 25.0 Q 55.2 23.3 58.2 29.2 Q 64.3 26.6 66.4 30.6 Q 70.8 32.0 69.3 38.4 Q 75.6 40.4 74.6 44.8 Q 77.1 48.7 71.8 52.7 Q 75.4 58.2 71.8 61.0 Q 71.1 65.5 64.6 65.2 Q 63.8 71.7 59.2 71.6 Q 55.8 74.6 51.0 70.1 Q 46.2 74.6 42.8 71.6 Q 38.2 71.7 37.4 65.2 Q 30.9 65.5 30.2 61.0 Q 26.6 58.2 30.2 52.7 Q 24.9 48.7 27.4 44.8 Q 26.4 40.4 32.7 38.4 Q 31.2 32.0 35.6 30.6 Q 37.7 26.6 43.8 29.2 Q 46.8 23.3 51.0 25.0 Z"
FIRE = "M13.5.67s.74 2.65.74 4.8c0 2.06-1.35 3.73-3.41 3.73-2.07 0-3.63-1.67-3.63-3.73l.03-.36C5.21 7.51 4 10.62 4 14c0 4.42 3.58 8 8 8s8-3.58 8-8C20 8.61 17.41 3.8 13.5.67zM11.71 19c-1.78 0-3.22-1.4-3.22-3.14 0-1.62 1.05-2.76 2.81-3.12 1.77-.36 3.6-1.21 4.62-2.58.39 1.29.59 2.65.59 4.04 0 2.65-2.15 4.8-4.8 4.8z"
FONT = "Segoe UI, Roboto, Helvetica, Arial, sans-serif"


def calendar_start(today):
    start = today - timedelta(days=20)
    return start - timedelta(days=(start.weekday() + 1) % 7)


def fetch_counts(owner, token, today):
    query = """query($login: String!, $from: DateTime!, $to: DateTime!) {
      user(login: $login) {
        contributionsCollection(from: $from, to: $to) {
          contributionCalendar { weeks { contributionDays { date contributionCount } } }
        }
      }
    }"""
    payload = json.dumps({"query": query, "variables": {
        "login": owner,
        "from": f"{calendar_start(today).isoformat()}T00:00:00Z",
        "to": f"{today.isoformat()}T23:59:59Z",
    }}).encode()
    request = Request("https://api.github.com/graphql", data=payload, headers={
        "Authorization": f"Bearer {token}", "Content-Type": "application/json",
        "User-Agent": "Voidstrap-contribution-calendar",
    })
    with urlopen(request, timeout=30) as response:
        data = json.load(response)
    if data.get("errors") or not data.get("data", {}).get("user"):
        raise RuntimeError("GitHub did not return the contribution calendar")
    calendar = data["data"]["user"]["contributionsCollection"]["contributionCalendar"]
    return {date.fromisoformat(day["date"]): day["contributionCount"]
            for week in calendar["weeks"] for day in week["contributionDays"]}


def render(counts, today, theme):
    start = calendar_start(today)
    days = [start + timedelta(days=i) for i in range((today - start).days + 1)]
    if any(day not in counts or not isinstance(counts[day], int) or counts[day] < 0 for day in days):
        raise ValueError("The contribution calendar is incomplete")
    total = sum(counts[today - timedelta(days=i)] for i in range(21))
    maximum = max((counts[day] for day in days), default=1)
    rows = (len(days) + 6) // 7
    points = [(col * 44, row * 48) for row in range(rows)
              for col in (range(7) if row % 2 == 0 else range(6, -1, -1))]
    points += points[-2:0:-1]
    duration = len(points) * 0.3
    frames = "".join(f"{i / len(points) * 100:.3f}%{{transform:translate({x}px,{y}px)}}"
                     for i, (x, y) in enumerate(points))
    c = THEMES[theme]
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="840" height="390" viewBox="0 0 840 390" role="img" aria-label="Animated contribution calendar for the last 21 days">',
             f'<style>@keyframes crawl{{{frames}100%{{transform:translate(0px,0px)}}}}'
             f'.snake{{animation:crawl {duration:.1f}s linear infinite}}'
             '@media (prefers-reduced-motion:reduce){.snake{animation:none}}</style>',
             f'<rect width="840" height="390" rx="30" fill="{c["card"]}"/>',
             f'<path d="{BADGE}" fill="#5C61C4"/>',
             f'<path d="{FIRE}" transform="translate(40 38)" fill="#FFFFFF"/>',
             f'<text x="90" y="58" font-family="{FONT}" font-size="24" font-weight="700" fill="{c["text"]}">Contribution snake</text>',
             '<rect x="28" y="100" width="396" height="262" rx="26" fill="#5C61C4"/>',
             '<g transform="translate(0 68)">',
             f'<path d="{BADGE}" transform="translate(35 90) scale(1.5)" fill="#A8A2E8"/>',
             f'<path d="{FIRE}" transform="translate(97 151)" fill="#2B2A6E"/>',
             f'<text x="162" y="179" font-family="{FONT}" font-size="42" font-weight="800" fill="#FFFFFF">{total:,}</text>',
             f'<text x="162" y="206" font-family="{FONT}" font-size="15" fill="#DDD9F2">Contributions in the last 21 days</text>',
             '</g>',
             f'<rect x="440" y="100" width="372" height="262" rx="26" fill="{c["surface"]}"/>',
             f'<text x="464" y="129" font-family="{FONT}" font-size="17" font-weight="700" fill="{c["text"]}">Last 21 days</text>']
    for col, letter in enumerate("SMTWTFS"):
        parts.append(f'<text x="{464 + col * 44 + 19}" y="152" font-family="{FONT}" font-size="12" text-anchor="middle" fill="{c["muted"]}">{letter}</text>')
    for i in range(rows * 7):
        x, y = 464 + (i % 7) * 44, 162 + (i // 7) * 48
        if i >= len(days):
            parts.append(f'<rect x="{x}" y="{y}" width="38" height="42" rx="12" fill="none" stroke="{c["outline"]}" stroke-width="1.5" stroke-dasharray="4 5"/>')
            continue
        day = days[i]
        count = counts[day]
        ratio = 0 if maximum <= 4 else count / maximum
        level = 0 if count == 0 else 1 if ratio <= 0.25 else 2 if ratio <= 0.5 else 3 if ratio <= 0.75 else 4
        parts.append(f'<rect x="{x}" y="{y}" width="38" height="42" rx="12" fill="{c["heat"][level]}"/>')
        parts.append(f'<text x="{x + 19}" y="{y + 27}" font-family="{FONT}" font-size="14" font-weight="600" text-anchor="middle" fill="{c["heat_text"][level]}">{day.day}</text>')
        if day == today:
            parts.append(f'<rect x="{x - 2.5}" y="{y - 2.5}" width="43" height="47" rx="14" fill="none" stroke="{c["ring"]}" stroke-width="2"/>')
    for i in range(7, 0, -1):
        radius = max(2.8, 5.2 - i * 0.3)
        parts.append(f'<circle class="snake" cx="483" cy="168" r="{radius}" fill="{c["ring"]}" style="animation-delay:-{i * 0.035:.3f}s"/>')
    parts.append(f'<g class="snake"><circle cx="483" cy="168" r="5.5" fill="{c["ring"]}"/><circle cx="481.5" cy="166.5" r="0.8" fill="{c["surface"]}"/><circle cx="484.5" cy="166.5" r="0.8" fill="{c["surface"]}"/></g>')
    parts.append('</svg>')
    return "".join(parts)


def main():
    today = date.today()
    counts = fetch_counts(os.environ["GITHUB_REPOSITORY_OWNER"], os.environ["GITHUB_TOKEN"], today)
    output = Path(sys.argv[1])
    (output / "github-contribution-grid-snake.svg").write_text(render(counts, today, "light"), encoding="utf-8")
    (output / "github-contribution-grid-snake-dark.svg").write_text(render(counts, today, "dark"), encoding="utf-8")


if __name__ == "__main__":
    main()
