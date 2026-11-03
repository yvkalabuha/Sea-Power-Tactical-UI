#!/usr/bin/env bash
# Customer-run: installs the Sea Power bridge mods into the game's BepInEx/plugins.
#   install.sh [voice|ui|all|dev|ui-ac|loader] [--uninstall]      default: all
#   voice = VoiceBotBridge (voice bot), ui = SeaPowerTacticalUI (interface mod).
#   dev = SeaPowerTacticalUI.Dev (developer test channel, T-139); never part of "all".
#   ui-ac = (T-105, development) the Steam Workshop mod folder, exactly as package.sh workshop builds it, into
#     <game>/Sea Power_Data/StreamingAssets/SeaPowerTacticalUI (the AnchorChain entry); never part of "all".
#   loader = (T-314, ADR-0121) SeaPowerTacticalUI.Loader.dll (the Workshop loader) into BepInEx/plugins; never part of "all".
# --uninstall removes the mod folder(s) only; BepInEx config files stay.
# The game must be closed. Agents never run this (tests use a fake GAME_DIR).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME_DIR="${GAME_DIR:-/home/user/drive/SteamLibrary/steamapps/common/Sea Power}"
PLUGINS="$GAME_DIR/BepInEx/plugins"
CONFIG="$GAME_DIR/BepInEx/config"
VOICE_DLL="$HERE/out/VoiceBotBridge.dll"
UI_DLL="$HERE/out/SeaPowerTacticalUI.dll"
VOICE_DEST="$PLUGINS/VoiceBotBridge"
UI_DEST="$PLUGINS/SeaPowerTacticalUI"
DEV_DLL="$HERE/out-dev/SeaPowerTacticalUI.Dev.dll"
DEV_DEST="$PLUGINS/SeaPowerTacticalUI.Dev"
DEV_CFG="$CONFIG/ru.voicebot.seapower.ui.dev.cfg"
DEV_SESSION_DIR="$CONFIG/SeaPowerTacticalUI.Dev"
LOADER_DLL="$HERE/out-loader/SeaPowerTacticalUI.Loader.dll"
LOADER_DEST="$PLUGINS/SeaPowerTacticalUI.Loader.dll"
AC_DEST="$GAME_DIR/Sea Power_Data/StreamingAssets/SeaPowerTacticalUI"

target=all
uninstall=0
for arg in "$@"; do
  case "$arg" in
    voice|ui|all|dev|ui-ac|loader) target="$arg" ;;
    --uninstall) uninstall=1 ;;
    *) echo "usage: install.sh [voice|ui|all|dev|ui-ac|loader] [--uninstall]" >&2; exit 2 ;;
  esac
done

want_voice=0
want_ui=0
want_dev=0
want_ac=0
want_loader=0
if [[ "$target" == loader ]]; then
  want_loader=1
elif [[ "$target" == dev ]]; then
  want_dev=1
elif [[ "$target" == ui-ac ]]; then
  want_ac=1
else
  [[ "$target" != ui ]] && want_voice=1
  [[ "$target" != voice ]] && want_ui=1
  # T-149: the voice mod is paused and not built by default; "all" then installs the UI mod only
  if [[ "$target" == all ]] && (( ! uninstall )) && [[ ! -f "$VOICE_DLL" ]]; then
    want_voice=0
    echo "note: $VOICE_DLL not built (voice mod paused), skipping voice; build.sh all builds it"
  fi
fi

if (( ! uninstall )); then
  if (( want_voice )) && [[ ! -f "$VOICE_DLL" ]]; then
    echo "build first: $HERE/build.sh (missing $VOICE_DLL)"; exit 1
  fi
  if (( want_ui )) && [[ ! -f "$UI_DLL" ]]; then
    echo "build first: $HERE/build.sh (missing $UI_DLL)"; exit 1
  fi
  if (( want_loader )) && [[ ! -f "$LOADER_DLL" ]]; then
    echo "build first: $HERE/build.sh (missing $LOADER_DLL)"; exit 1
  fi
  if (( want_dev )) && [[ ! -f "$DEV_DLL" ]]; then
    echo "build first: $HERE/build.sh (missing $DEV_DLL)"; exit 1
  fi
fi

if pgrep -x "Sea Power.exe" >/dev/null; then
  echo "close the game first"; exit 1
fi

if (( want_loader )); then
  if (( uninstall )); then
    [[ -f "$LOADER_DEST" ]] && { rm -f -- "$LOADER_DEST"; echo "removed $LOADER_DEST"; }
    exit 0
  fi
  mkdir -p "$PLUGINS"
  cp -f -- "$LOADER_DLL" "$LOADER_DEST"
  echo "installed $LOADER_DEST"
  exit 0
fi

if (( want_ac )); then
  if (( uninstall )); then
    [[ -d "$AC_DEST" ]] && { rm -rf -- "$AC_DEST"; echo "removed $AC_DEST"; }
    exit 0
  fi
  ac_tmp="$(mktemp -d)"
  trap 'rm -rf -- "$ac_tmp"' EXIT
  bash "$HERE/package.sh" workshop --out "$ac_tmp" >/dev/null || { echo "workshop package failed, nothing installed" >&2; exit 1; }
  ac_src=""
  for d in "$ac_tmp"/workshop/*/SeaPowerTacticalUI; do ac_src="$d"; done
  [[ -d "$ac_src" ]] || { echo "workshop package folder not found, nothing installed" >&2; exit 1; }
  mkdir -p "$(dirname "$AC_DEST")"
  rm -rf -- "$AC_DEST"
  cp -r -- "$ac_src" "$AC_DEST"
  echo "installed $AC_DEST (Workshop layout, AnchorChain entry)"
  if [[ -f "$UI_DEST/SeaPowerTacticalUI.dll" ]]; then
    echo "WARNING: two copies of the interface mod: $UI_DEST (BepInEx) and $AC_DEST (AnchorChain)."
    echo "  Only one runs (the first to start). Prefer one: install.sh ui --uninstall"
  fi
  echo "enable it in the Mods window, start the game, then: grep -i 'Tactical UI' '$GAME_DIR/BepInEx/LogOutput.log'"
  exit 0
fi

if (( uninstall )); then
  if (( want_voice )) && [[ -d "$VOICE_DEST" ]]; then
    rm -rf -- "$VOICE_DEST"; echo "removed $VOICE_DEST"
  fi
  if (( want_ui )) && [[ -d "$UI_DEST" ]]; then
    rm -rf -- "$UI_DEST"; echo "removed $UI_DEST (config kept in $CONFIG)"
  fi
  if (( want_dev )); then
    [[ -d "$DEV_DEST" ]] && { rm -rf -- "$DEV_DEST"; echo "removed $DEV_DEST"; }
    [[ -d "$DEV_SESSION_DIR" ]] && { rm -rf -- "$DEV_SESSION_DIR"; echo "removed $DEV_SESSION_DIR"; }
  fi
  exit 0
fi

# Config migration for the interface mod (0.13.x -> 0.14 split). Runs before anything is copied.
if (( want_ui )); then
  if ! python3 "$HERE/migrate_cfg.py" "$CONFIG"; then
    echo "config migration failed, nothing installed"; exit 1
  fi
fi

# Removes copies of the same DLL left in other BepInEx/plugins folders (they would load twice).
remove_foreign() {
  local name="$1" own="$2" f
  [[ -d "$PLUGINS" ]] || return 0
  while IFS= read -r -d '' f; do
    rm -f -- "$f"; echo "removed foreign copy $f"
  done < <(find "$PLUGINS" -name "$name" -type f ! -path "$own/*" -print0)
}

if (( want_voice )); then
  remove_foreign "VoiceBotBridge.dll" "$VOICE_DEST"
  mkdir -p "$VOICE_DEST"
  cp -f -- "$VOICE_DLL" "$VOICE_DEST/"
  echo "installed $VOICE_DEST"
fi
if (( want_ui )); then
  remove_foreign "SeaPowerTacticalUI.dll" "$UI_DEST"
  mkdir -p "$UI_DEST"
  cp -f -- "$UI_DLL" "$UI_DEST/"
  # own texts of the interface mod (UiText reads lang/language_xx/*.ini next to the DLL)
  if [[ -d "$HERE/SeaPowerTacticalUI/lang" ]]; then
    rm -rf -- "$UI_DEST/lang"
    cp -r -- "$HERE/SeaPowerTacticalUI/lang" "$UI_DEST/lang"
  fi
  echo "installed $UI_DEST"
fi

if (( want_dev )); then
  remove_foreign "SeaPowerTacticalUI.Dev.dll" "$DEV_DEST"
  mkdir -p "$DEV_DEST" "$CONFIG"
  cp -f -- "$DEV_DLL" "$DEV_DEST/"
  if [[ -f "$DEV_CFG" ]] && grep -qE '^[[:space:]]*Enabled[[:space:]]*=' "$DEV_CFG"; then
    sed -i -E 's/^[[:space:]]*Enabled[[:space:]]*=.*/Enabled = true/' "$DEV_CFG"
  elif [[ -f "$DEV_CFG" ]]; then
    printf '\n[Dev]\nEnabled = true\n' >> "$DEV_CFG"
  else
    printf '[Dev]\n\nEnabled = true\n' > "$DEV_CFG"
  fi
  echo "installed $DEV_DEST (config: $DEV_CFG, Enabled = true)"
  echo "WARNING: dev-only mod, never ship it. It opens a token-protected loopback port and,"
  echo "  while [Dev] FireCommands = true (default), lets the agent fire at hostile targets."
  echo "  Set FireCommands = false in $DEV_CFG to forbid fire commands."
fi

if (( want_ui )) && [[ -f "$DEV_DEST/SeaPowerTacticalUI.Dev.dll" ]]; then
  echo "WARNING: the dev mod is installed in $DEV_DEST (dev-only, not for players)."
  echo "  Remove it: install.sh dev --uninstall"
fi

# UI alone next to a voice mod from another build (e.g. 0.13.x): the UI mod turns itself off.
if (( want_ui && ! want_voice )) && [[ -f "$VOICE_DEST/VoiceBotBridge.dll" ]] \
   && ! cmp -s "$VOICE_DEST/VoiceBotBridge.dll" "$VOICE_DLL"; then
  echo "WARNING: $VOICE_DEST holds a VoiceBotBridge.dll different from this build."
  echo "  An older one (< 0.14) turns SeaPowerTacticalUI off at game start."
  echo "  Update it: install.sh voice   or remove it: install.sh voice --uninstall"
fi

echo "start the game, then: grep -i -E 'VoiceBot|SeaPower' '$GAME_DIR/BepInEx/LogOutput.log'"
