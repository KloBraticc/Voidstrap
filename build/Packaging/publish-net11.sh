publish_net11() {
  local root="$1" rid="$2" version="$3" stage="$4" destination="$5"
  local cli="${VOIDSTRAP_NET11_DOTNET:-${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}}"
  if [ -z "$cli" ] || [ ! -x "$cli" ]; then
    cli="$(command -v dotnet)"
  fi
  local sdk
  sdk="$("$cli" --list-sdks | awk '$1 ~ /^11[.][0-9]+[.][0-9]+$/ { version=$1 } END { print version }')"
  if [ -z "$sdk" ]; then
    echo 'The .NET 11 SDK is required to package both runtimes' >&2
    return 1
  fi
  local work="$stage/net11"
  mkdir -p "$work"
  sed -E 's/("version"[[:space:]]*:[[:space:]]*")[^"]+/\1'"$sdk"'/' "$root/global.json" > "$work/global.json"
  (
    cd "$work"
    "$cli" publish "$root/src/Voidstrap.Cross/Voidstrap.Cross.csproj" -c Release -r "$rid" --self-contained true \
      -o "$work/publish" -p:VoidstrapDotNet11=true -p:VoidstrapLinuxPackagingRoot="$stage/" \
      -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false
  )
  install -m 755 "$work/publish/Voidstrap" "$destination"
}
