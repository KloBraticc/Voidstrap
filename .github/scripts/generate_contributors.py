import base64
import html
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = "KloBraticc/Voidstrap"
COLUMNS = 12
SIZE = 64
GAP = 5
CAPTION = 28
AGENT = {"User-Agent": "Voidstrap-contributors"}


def fetch(url):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=AGENT), timeout=30) as response:
                return response.read()
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            if attempt == 4:
                raise
            time.sleep(5 * (attempt + 1))


def contributors():
    page = fetch(
        f"https://github.com/{REPO}/contributors_list?count=500&current_repository={REPO.split('/')[1]}&items_to_show=500"
    ).decode("utf-8", "replace")
    logins = []
    for login in re.findall(r'href="https://github\.com/([A-Za-z0-9-]+)"', page):
        if login not in logins:
            logins.append(login)
    if not logins:
        raise SystemExit("no contributors were found")
    return logins


def build(logins):
    rows = (len(logins) + COLUMNS - 1) // COLUMNS
    width = COLUMNS * (SIZE + GAP) + GAP
    height = rows * (SIZE + GAP) + GAP + CAPTION
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        f'<defs><clipPath id="c"><circle cx="{SIZE / 2}" cy="{SIZE / 2}" r="{SIZE / 2}"/></clipPath></defs>',
    ]
    for index, login in enumerate(logins):
        data = fetch(f"https://github.com/{login}.png?size=128")
        kind = "image/png" if data[:4] == b"\x89PNG" else "image/jpeg"
        x = GAP + (index % COLUMNS) * (SIZE + GAP)
        y = GAP + (index // COLUMNS) * (SIZE + GAP)
        name = html.escape(login)
        parts.append(
            f'<a xlink:href="https://github.com/{name}" target="_blank"><g transform="translate({x},{y})"><title>{name}</title>'
            f'<image width="{SIZE}" height="{SIZE}" clip-path="url(#c)" xlink:href="data:{kind};base64,{base64.b64encode(data).decode()}"/>'
            f'<circle cx="{SIZE / 2}" cy="{SIZE / 2}" r="{SIZE / 2 - 0.5}" fill="none" stroke="#c0c0c0" stroke-width="1"/></g></a>'
        )
    parts.append(
        f'<text x="{GAP}" y="{height - 9}" font-family="-apple-system, BlinkMacSystemFont, Segoe UI, Helvetica, Arial, sans-serif" font-size="14" fill="#8b949e">Styled after contrib.rocks</text>'
    )
    parts.append("</svg>")
    return "\n".join(parts)


if __name__ == "__main__":
    target = Path(sys.argv[1] if len(sys.argv) > 1 else ".") / "contributors.svg"
    target.write_text(build(contributors()), encoding="utf-8")
