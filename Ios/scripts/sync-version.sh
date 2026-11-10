#!/bin/sh
set -eu
here=$(cd "$(dirname "$0")/.." && pwd)
version=$(sed -n 's:.*<VoidstrapVersion>\([^<]*\)</VoidstrapVersion>.*:\1:p' "$here/../Directory.Build.props" | head -n 1)
[ -n "$version" ] || { echo "VoidstrapVersion is missing" >&2; exit 1; }
plist="$here/Info.plist"
for key in CFBundleShortVersionString CFBundleVersion; do
    awk -v key="$key" -v value="$version" '
        found { sub(/<string>[^<]*<\/string>/, "<string>" value "</string>"); found = 0 }
        index($0, "<key>" key "</key>") { found = 1 }
        { print }
    ' "$plist" > "$plist.tmp" && mv "$plist.tmp" "$plist"
done
echo "$version"
