#!/usr/bin/env bash
# Runs selected repo pytest files against THIS branch tree in a mirror root. Main tree is not written to.
#   mirror/plugins/sea_power/bridge/<entry>  -> symlink to this folder's entry (UI mod, Common, stands of the branch, out/)
#   mirror/plugins/sea_power/bridge/<other>  -> COPY of the main-tree entry absent here (other stands, install.sh, VoiceBotBridge ...),
#                                               without bin/obj: those stands compile ../SeaPowerTacticalUI = the branch sources
#   mirror/plugins/sea_power/<other>, games/, tools/ -> symlinks to the repo (read-only use)
#   tests/ copied, tests/data/ui_config_keys.json replaced by tests-data/ (no [Loader] key in 0.30.x).
# Main-only tests (beta files absent from this branch, 0.31 docs/loader) are deselected below with the reason.
# usage: run_pytest.sh <mirror dir> tests/test_x.py ...
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../../.." && pwd)"
MAIN="$REPO/plugins/sea_power/bridge"
M="$1"; shift
rm -rf -- "$M"; mkdir -p "$M/plugins/sea_power/bridge"
for e in "$REPO/plugins/sea_power"/* "$REPO/plugins/sea_power"/.[!.]*; do
  [[ -e "$e" ]] || continue; n="$(basename "$e")"
  [[ "$n" == bridge || "$n" == __pycache__ ]] && continue
  ln -s "$e" "$M/plugins/sea_power/$n"
done
for e in "$HERE"/*; do ln -s "$e" "$M/plugins/sea_power/bridge/$(basename "$e")"; done
for e in "$MAIN"/* "$MAIN"/.[!.]*; do
  [[ -e "$e" ]] || continue; n="$(basename "$e")"
  case "$n" in branches|dist|out|out-*|__pycache__) continue;; esac
  [[ -e "$M/plugins/sea_power/bridge/$n" ]] && continue
  rsync -a --exclude bin --exclude obj --exclude __pycache__ "$e" "$M/plugins/sea_power/bridge/"
done
cp -r "$REPO/tests" "$M/tests"; cp "$REPO/pyproject.toml" "$M/"
ln -s "$REPO/tools" "$M/tools"   # shared QA/bench scripts (tests read tools/bench, tools/qa)
ln -s "$REPO/games" "$M/games"; ln -s "$REPO/DECISIONS.md" "$M/DECISIONS.md"   # docs/decisions read by some tests
cp "$HERE/tests-data/ui_config_keys.json" "$M/tests/data/ui_config_keys.json"
DESELECT=(
  # T-309 MapPx/Emit, T-311 coverage budget, broker/stand wiring of the beta core: main only
  --deselect tests/test_sea_power_coverage_budget.py --deselect tests/test_sea_power_radar_t225.py::test_broker_is_wired_into_the_stands
  --deselect tests/test_sea_power_air_radar_t241.py::test_stands_compile_the_core
  --deselect tests/test_sea_power_range_rings_static.py::test_overlay_draws_through_emit_with_the_premultiplied_material
)
if [[ -f "$HERE/tests-data/main_only.txt" ]]; then
  while IFS= read -r l; do l="${l%%#*}"; l="${l//[[:space:]]/}"; [[ -n "$l" ]] && DESELECT+=(--deselect "$l"); done < "$HERE/tests-data/main_only.txt"
fi
cd "$M" && nice -n 10 uv run --project "$REPO" pytest -q -n 8 --dist loadfile -p no:cacheprovider -c "$M/pyproject.toml" --rootdir "$M" "${DESELECT[@]}" "$@"
