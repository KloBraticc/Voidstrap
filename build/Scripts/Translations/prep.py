import os
import re
import shutil
import uuid
from pathlib import Path
from xml.etree import ElementTree


MAX_RESOURCE_BYTES = 16 * 1024 * 1024
LOCALE_PATTERN = re.compile(r"^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$")

exports = Path(input("Path of exported translation files: ").strip()).expanduser().resolve()
destination = Path(input("Destination resources folder: ").strip()).expanduser().resolve()
if not exports.is_dir():
	raise SystemExit("The exported translation folder does not exist")
destination.mkdir(parents=True, exist_ok=True)

seen = set()
for filename in sorted(exports.rglob("Strings.resx")):
	if not filename.is_file() or filename.is_symlink():
		continue
	locale = filename.parent.name
	if not LOCALE_PATTERN.fullmatch(locale):
		raise SystemExit(f"Invalid locale folder: {locale}")
	locale_key = locale.lower()
	if locale_key in seen:
		raise SystemExit(f"Duplicate locale export: {locale}")
	seen.add(locale_key)
	if filename.stat().st_size <= 0 or filename.stat().st_size > MAX_RESOURCE_BYTES:
		raise SystemExit(f"Invalid resource size for locale: {locale}")
	try:
		ElementTree.parse(filename)
	except ElementTree.ParseError as exception:
		raise SystemExit(f"Invalid resource XML for locale {locale}: {exception}") from exception

	target = destination / f"Strings.{locale}.resx"
	temporary = destination / f".{target.name}.{uuid.uuid4().hex}.tmp"
	try:
		shutil.copyfile(filename, temporary)
		os.replace(temporary, target)
	finally:
		if temporary.exists():
			temporary.unlink()
	print(f"Copied locale {locale}")
