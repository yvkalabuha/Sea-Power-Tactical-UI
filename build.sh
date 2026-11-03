#!/usr/bin/env bash
# Stable line 0.30.x (game branch 0.8.7-261021): builds the UI mod and runs the offline checks of THIS tree only.
# Outputs stay inside this folder (out/, out-selfcheck-ui/, <stand>/bin); the game dir is only read.
# No loader (T-314 is beta-only), no dev mod: the installed dev DLL of the main tree is kept (no shared-file changes).
#   build.sh            mod + selfcheck-ui
#   build.sh stands     also captest, aswtest, puretest, coretest, striketest, strikewintest
set -euo pipefail
export PATH="$PATH:$HOME/.dotnet" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME_DIR="${GAME_DIR:-/home/user/drive/SteamLibrary/steamapps/common/Sea Power}"
MANAGED="$GAME_DIR/Sea Power_Data/Managed"
AC_ARGS=()
[[ -n "${ANCHORCHAIN_DLL:-}" ]] && AC_ARGS=(-p:AnchorChainDll="$ANCHORCHAIN_DLL")
dotnet build "$HERE/SeaPowerTacticalUI" -c Release -p:GameDir="$GAME_DIR" ${AC_ARGS[@]+"${AC_ARGS[@]}"} -p:UseSharedCompilation=false
dotnet build "$HERE/selfcheck" -c Release -p:GameDir="$GAME_DIR" -p:Flavor=ui -nologo -v q -p:UseSharedCompilation=false
echo "== selfcheck ui =="
DOTNET_ROLL_FORWARD=Major dotnet "$HERE/out-selfcheck-ui/selfcheck-ui.dll" "$MANAGED"
echo "== link smoke (ui <-> link slot) =="
DOTNET_ROLL_FORWARD=Major dotnet "$HERE/out-selfcheck-ui/selfcheck-ui.dll" link-smoke "$MANAGED"
if [[ "${1:-}" == stands ]]; then
  for s in captest aswtest puretest coretest striketest strikewintest; do
    dotnet build "$HERE/$s" -c Release -nologo -v q -p:GameDir="$GAME_DIR" -p:UseSharedCompilation=false
  done
fi
echo "built: $HERE/out/SeaPowerTacticalUI.dll"
