import re
from pathlib import Path
from xml.etree import ElementTree


MAX_SOURCE_BYTES = 4 * 1024 * 1024
SOURCE_EXTENSIONS = {".cs", ".csproj", ".props", ".targets", ".xaml", ".xml"}

directory = Path(input("Enter project path: ").strip()).expanduser().resolve()
resource_file = directory / "Resources" / "Strings.resx"
if not directory.is_dir() or not resource_file.is_file():
	raise SystemExit("The project path does not contain Resources and Strings.resx")

root = ElementTree.parse(resource_file).getroot()
existing = {
	element.attrib["name"]
	for element in root.findall("data")
	if "name" in element.attrib
}
found = set()

for filename in directory.rglob("*"):
	if not filename.is_file() or filename.is_symlink() or filename.suffix.lower() not in SOURCE_EXTENSIONS:
		continue
	try:
		relative = filename.relative_to(directory)
	except ValueError:
		continue
	if any(part.lower() in {"bin", "obj", "resources"} for part in relative.parts[:-1]):
		continue
	try:
		if filename.stat().st_size > MAX_SOURCE_BYTES:
			print(f"Skipped oversized source {relative}")
			continue
		contents = filename.read_text(encoding="utf-8")
	except (OSError, UnicodeError):
		print(f"Could not open {relative}")
		continue

	for match in re.findall(r"Strings\.([a-zA-Z0-9_]+)", contents):
		if "_" in match:
			found.add(match.replace("_", "."))

	found.update(re.findall(r'FromTranslation\s*=\s*"([a-zA-Z0-9.]+)"', contents))

for entry in sorted(existing - found):
	if not entry.startswith("Enums."):
		print(entry)
