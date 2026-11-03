#!/usr/bin/env bash
# Build SeaPowerTacticalUI(.Dev) with a user-local .NET SDK (~/.dotnet). Reads game DLLs, never writes to the game dir.
#   build.sh          default (T-149): UI mod only; the voice mod VoiceBotBridge is paused and skipped
#   build.sh all      (or VOICEBOT_BUILD=all) also builds VoiceBotBridge and runs selfcheck voice
set -euo pipefail
MODE="${1:-${VOICEBOT_BUILD:-ui}}"
case "$MODE" in ui|all) ;; *) echo "usage: build.sh [ui|all]" >&2; exit 2 ;; esac
export PATH="$PATH:$HOME/.dotnet" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME_DIR="${GAME_DIR:-/home/user/drive/SteamLibrary/steamapps/common/Sea Power}"
if [[ "$MODE" == all ]]; then
  dotnet build "$HERE/VoiceBotBridge" -c Release -p:GameDir="$GAME_DIR" -p:UseSharedCompilation=false
fi
# T-105: AnchorChain.dll is only referenced (AcEntry); ANCHORCHAIN_DLL overrides the default Workshop path of the csproj
AC_ARGS=()
[[ -n "${ANCHORCHAIN_DLL:-}" ]] && AC_ARGS=(-p:AnchorChainDll="$ANCHORCHAIN_DLL")
dotnet build "$HERE/SeaPowerTacticalUI" -c Release -p:GameDir="$GAME_DIR" ${AC_ARGS[@]+"${AC_ARGS[@]}"} -p:UseSharedCompilation=false
# T-314 (ADR-0121): own Workshop loader (BepInEx plugin, out-loader/) and the offline stand of its logic
dotnet build "$HERE/SeaPowerTacticalUI.Loader" -c Release -p:GameDir="$GAME_DIR" -p:UseSharedCompilation=false
dotnet build "$HERE/loadertest" -c Release -nologo -v q -p:UseSharedCompilation=false
echo "== loadertest =="
DOTNET_ROLL_FORWARD=Major dotnet "$HERE/loadertest/bin/loadertest.dll"
# developer test channel (ADR-0046): separate DLL in out-dev/, never packaged; its offline stand must pass
dotnet build "$HERE/SeaPowerTacticalUI.Dev" -c Release -p:GameDir="$GAME_DIR" -p:UseSharedCompilation=false
dotnet build "$HERE/devtest" -c Release -nologo -v q -p:UseSharedCompilation=false
echo "== devtest selftest =="
DOTNET_ROLL_FORWARD=Major dotnet "$HERE/devtest/bin/devtest.dll" selftest
# optional offline member check against the installed game version, one run per mod
if [[ "$MODE" == all ]]; then
  dotnet build "$HERE/selfcheck" -c Release -p:GameDir="$GAME_DIR" -p:Flavor=voice -nologo -v q -p:UseSharedCompilation=false
fi
dotnet build "$HERE/selfcheck" -c Release -p:GameDir="$GAME_DIR" -p:Flavor=ui -nologo -v q -p:UseSharedCompilation=false
if [[ "$MODE" == all ]]; then
  echo "== selfcheck voice =="
  DOTNET_ROLL_FORWARD=Major dotnet "$HERE/out-selfcheck/selfcheck.dll" "$GAME_DIR/Sea Power_Data/Managed"
fi
echo "== selfcheck ui =="
DOTNET_ROLL_FORWARD=Major dotnet "$HERE/out-selfcheck-ui/selfcheck-ui.dll" "$GAME_DIR/Sea Power_Data/Managed"
echo "== link smoke (ui <-> link slot) =="
DOTNET_ROLL_FORWARD=Major dotnet "$HERE/out-selfcheck-ui/selfcheck-ui.dll" link-smoke "$GAME_DIR/Sea Power_Data/Managed"
[[ "$MODE" == all ]] && echo "built: $HERE/out/VoiceBotBridge.dll"
echo "built: $HERE/out/SeaPowerTacticalUI.dll"
echo "built: $HERE/out-loader/SeaPowerTacticalUI.Loader.dll"
echo "built: $HERE/out-dev/SeaPowerTacticalUI.Dev.dll (dev only)"
