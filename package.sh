#!/usr/bin/env bash
# Stable line 0.30.x: Workshop folder of THIS tree, layout of the 0.30.30 package (no loader/).
#   package.sh   -> ../../dist/workshop/<version>/SeaPowerTacticalUI/ + ../../dist/SeaPowerTacticalUI-workshop-<version>.zip
# Descriptions (workshop/description_*.bbcode) are the 0.30.30 texts. Checked by the branch workshop_check.py.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT_DIR="$(cd "$HERE/../../dist" && pwd)"
UI="$HERE/SeaPowerTacticalUI"; WS="$HERE/workshop"
DLL="$HERE/out/SeaPowerTacticalUI.dll"; PDB="$HERE/out/SeaPowerTacticalUI.pdb"
for f in "$DLL" "$PDB" "$UI/README.md" "$UI/CHANGELOG.md" "$UI/TRANSLATING.md" "$UI/ModInfo.cs" "$WS/_info.ini" "$WS/LICENSE" "$WS/NOTICE" "$WS/BUILD.md" "$WS/thumbnail.jpg"; do
  [[ -f "$f" ]] || { echo "missing $f (build first: $HERE/build.sh)"; exit 1; }
done
version="$(sed -n 's/.*Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$UI/ModInfo.cs" | head -n 1)"
[[ "$version" == 0.30.* ]] || { echo "not a 0.30.x version: $version"; exit 1; }
stage="$(mktemp -d)"; trap 'rm -rf -- "$stage"' EXIT
mod="$stage/pack/SeaPowerTacticalUI"; mkdir -p "$mod/source"
cp -- "$DLL" "$PDB" "$UI/README.md" "$UI/CHANGELOG.md" "$UI/TRANSLATING.md" "$mod/"
cp -- "$WS/_info.ini" "$WS/LICENSE" "$WS/NOTICE" "$WS/thumbnail.jpg" "$mod/"
cp -r -- "$UI/lang" "$mod/lang"
src="$stage/src"; mkdir -p "$src/SeaPowerTacticalUI" "$src/Common"
(cd "$UI" && find . -maxdepth 1 -type f \( -name '*.cs' -o -name '*.csproj' \) -exec cp -- {} "$src/SeaPowerTacticalUI/" \;)
cp -r -- "$UI/lang" "$src/SeaPowerTacticalUI/lang"
(cd "$HERE/Common" && find . -type f \( -name '*.cs' -o -name 'actions.json' \) -exec cp --parents -- {} "$src/Common/" \;)
cp -- "$WS/BUILD.md" "$WS/LICENSE" "$src/"
(cd "$src" && zip -q -r -D "$mod/source/SeaPowerTacticalUI-$version-src.zip" .)
game_dir="${GAME_DIR:-/home/user/drive/SteamLibrary/steamapps/common/Sea Power}"
game_args=(); [[ -d "$game_dir" ]] && game_args=(--game "$game_dir")
python3 "$HERE/workshop_check.py" "$mod" --version "$version" ${game_args[@]+"${game_args[@]}"} || { echo "workshop package check failed" >&2; exit 1; }
final="$OUT_DIR/workshop/$version"; rm -rf -- "$final"; mkdir -p "$final"
cp -r -- "$mod" "$final/SeaPowerTacticalUI"
archive="$OUT_DIR/SeaPowerTacticalUI-workshop-$version.zip"; rm -f -- "$archive"
(cd "$stage/pack" && zip -q -r -D "$archive" SeaPowerTacticalUI)
cp -- "$WS/description_en.bbcode" "$WS/description_ru.bbcode" "$final/"
[[ -f "$WS/description_cn.bbcode" ]] && cp -- "$WS/description_cn.bbcode" "$final/"   # T-338: Simplified Chinese (Steam "schinese")
if [[ -n "${CHANGENOTE_FROM:-}" ]]; then
  # several versions since the one on the Workshop: sections $version ... $CHANGENOTE_FROM, headers kept
  awk -v top="## $version" -v last="## $CHANGENOTE_FROM" '
    index($0, top) == 1 && !on { on = 1 }
    on && /^## / && seen_last { exit }
    on && index($0, last) == 1 { seen_last = 1 }
    on { print }' "$UI/CHANGELOG.md" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' > "$final/changenote.txt"
else
  sed -n "/^## $version\([[:space:]]\|\$\)/,/^## /p" "$UI/CHANGELOG.md" | sed '1d;$ { /^## /d }' > "$final/changenote.txt"
fi
echo "changenote $(wc -c < "$final/changenote.txt") bytes (Steam limit 8000)"
echo "folder $final/SeaPowerTacticalUI"
echo "packed $archive"
