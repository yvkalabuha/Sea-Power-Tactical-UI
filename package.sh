#!/usr/bin/env bash
# Packs the interface mod for distribution.
#   package.sh ui                    dist/SeaPowerTacticalUI-<version>.zip
#       BepInEx/plugins/SeaPowerTacticalUI/{SeaPowerTacticalUI.dll,README.md,lang/}
#   (no loader: the Workshop package carries SeaPowerTacticalUI.Loader.dll in loader/, T-314)
#   package.sh workshop [--out DIR]  (T-105) the Steam Workshop mod folder DIR/workshop/<version>/SeaPowerTacticalUI/
#       and DIR/SeaPowerTacticalUI-workshop-<version>.zip (DIR defaults to dist/); nothing is uploaded.
#       The folder is checked by workshop_check.py (allowed files, no sockets / voice / dev code, licence, thumbnail ...).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

TARGET="${1:-}"
case "$TARGET" in
  ui|workshop) shift ;;
  *) echo "usage: package.sh ui | package.sh workshop [--out DIR]" >&2; exit 2 ;;
esac
OUT_DIR="$HERE/dist"
if [[ "$TARGET" == workshop ]]; then
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --out) [[ $# -ge 2 ]] || { echo "--out needs a folder" >&2; exit 2; }; OUT_DIR="$2"; shift 2 ;;
      *) echo "usage: package.sh workshop [--out DIR]" >&2; exit 2 ;;
    esac
  done
fi

DLL="$HERE/out/SeaPowerTacticalUI.dll"
README="$HERE/SeaPowerTacticalUI/README.md"
MODINFO="$HERE/SeaPowerTacticalUI/ModInfo.cs"
[[ -f "$DLL" ]] || { echo "build first: $HERE/build.sh (missing $DLL)"; exit 1; }
[[ -f "$README" ]] || { echo "missing $README"; exit 1; }
[[ -f "$MODINFO" ]] || { echo "missing $MODINFO"; exit 1; }

version="$(sed -n 's/.*Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$MODINFO" | head -n 1)"
[[ -n "$version" ]] || { echo "version not found in $MODINFO"; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf -- "$stage"' EXIT

if [[ "$TARGET" == workshop ]]; then
  PDB="$HERE/out/SeaPowerTacticalUI.pdb"
  LOADER_DLL="$HERE/out-loader/SeaPowerTacticalUI.Loader.dll"   # T-314 (ADR-0121)
  [[ -f "$LOADER_DLL" ]] || { echo "build first: $HERE/build.sh (missing $LOADER_DLL)"; exit 1; }
  WS="$HERE/workshop"
  UI="$HERE/SeaPowerTacticalUI"
  for f in "$PDB" "$UI/CHANGELOG.md" "$UI/TRANSLATING.md" "$UI/SeaPowerTacticalUI.csproj" "$WS/_info.ini" "$WS/LICENSE" "$WS/NOTICE" "$WS/BUILD.md" "$WS/thumbnail.jpg" "$HERE/Common/actions.json" "$HERE/workshop_check.py"; do
    [[ -f "$f" ]] || { echo "missing $f"; exit 1; }
  done
  [[ -d "$UI/lang" ]] || { echo "missing $UI/lang"; exit 1; }
  command -v zip >/dev/null || { echo "zip is not installed"; exit 1; }
  mod="$stage/pack/SeaPowerTacticalUI"
  mkdir -p "$mod/source" "$mod/loader"
  cp -- "$LOADER_DLL" "$mod/loader/"
  cp -- "$DLL" "$PDB" "$README" "$UI/CHANGELOG.md" "$UI/TRANSLATING.md" "$mod/"
  cp -- "$WS/_info.ini" "$WS/LICENSE" "$WS/NOTICE" "$WS/thumbnail.jpg" "$mod/"
  cp -r -- "$UI/lang" "$mod/lang"
  # corresponding source (GPL): the mod, the shared Common/ compiled into the same DLL, build notes, licence. No voice / dev / tests.
  src="$stage/src"
  mkdir -p "$src/SeaPowerTacticalUI" "$src/Common"
  (cd "$UI" && find . -maxdepth 1 -type f \( -name '*.cs' -o -name '*.csproj' \) -exec cp -- {} "$src/SeaPowerTacticalUI/" \;)
  cp -r -- "$UI/lang" "$src/SeaPowerTacticalUI/lang"
  mkdir -p "$src/SeaPowerTacticalUI.Loader"
  (cd "$HERE/SeaPowerTacticalUI.Loader" && find . -maxdepth 1 -type f \( -name '*.cs' -o -name '*.csproj' \) -exec cp -- {} "$src/SeaPowerTacticalUI.Loader/" \;)
  (cd "$HERE/Common" && find . -type f \( -name '*.cs' -o -name 'actions.json' \) -exec cp --parents -- {} "$src/Common/" \;)
  cp -- "$WS/BUILD.md" "$WS/LICENSE" "$src/"
  (cd "$src" && zip -q -r -D "$mod/source/SeaPowerTacticalUI-$version-src.zip" .)
  game_args=()
  game_dir="${GAME_DIR:-/home/user/drive/SteamLibrary/steamapps/common/Sea Power}"
  [[ -d "$game_dir" ]] && game_args=(--game "$game_dir")
  python3 "$HERE/workshop_check.py" "$mod" --version "$version" ${game_args[@]+"${game_args[@]}"} || { echo "workshop package check failed, nothing packed" >&2; exit 1; }
  final="$OUT_DIR/workshop/$version"
  rm -rf -- "$final"
  mkdir -p "$final"
  cp -r -- "$mod" "$final/SeaPowerTacticalUI"
  archive="$OUT_DIR/SeaPowerTacticalUI-workshop-$version.zip"
  rm -f -- "$archive"
  (cd "$stage/pack" && zip -q -r -D "$archive" SeaPowerTacticalUI)
  # publishing helpers for the customer (not part of the mod folder): description texts and the version note
  cp -- "$WS/description_en.bbcode" "$WS/description_ru.bbcode" "$final/" 2>/dev/null || true
  [[ -f "$WS/description_cn.bbcode" ]] && cp -- "$WS/description_cn.bbcode" "$final/"   # T-338: Simplified Chinese (Steam "schinese")
  {
    sed -n "/^## $version\([[:space:]]\|\$\)/,/^## /p" "$UI/CHANGELOG.md" | sed '1d;$ { /^## /d }'
  } > "$final/changenote.txt"
  [[ -s "$final/changenote.txt" ]] || echo "$version" > "$final/changenote.txt"
  echo "folder $final/SeaPowerTacticalUI"
  echo "packed $archive"
  exit 0
fi

plugin_rel="BepInEx/plugins/SeaPowerTacticalUI"
mkdir -p "$stage/$plugin_rel"
cp -- "$DLL" "$README" "$stage/$plugin_rel/"
[[ -d "$HERE/SeaPowerTacticalUI/lang" ]] && cp -r -- "$HERE/SeaPowerTacticalUI/lang" "$stage/$plugin_rel/lang"

mkdir -p "$HERE/dist"
archive="$HERE/dist/SeaPowerTacticalUI-$version.zip"
rm -f -- "$archive"
(cd "$stage" && zip -q -r -D "$archive" BepInEx)
echo "packed $archive"
