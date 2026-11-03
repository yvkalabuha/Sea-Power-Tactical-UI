#!/usr/bin/env python3
"""Move the UI settings of VoiceBot Bridge 0.13 into the Sea Power Tactical UI config.

Usage: python3 migrate_cfg.py <BepInEx/config dir>
Exit code: 0 = ok (also "nothing to do"), 2 = error.

BepInEx .cfg files are parsed by hand: configparser would drop the comments.
"""
# ruff: noqa: E501

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from pathlib import Path

UI_SECTIONS: tuple[str, ...] = ("RadarCoverage", "Radial", "Overview", "Menu")

OLD_NAME = "ru.voicebot.seapower.bridge.cfg"
NEW_NAME = "ru.voicebot.seapower.ui.cfg"
BACKUP_NAME = OLD_NAME + ".bak-0.13"
NEW_GUID = "ru.voicebot.seapower.ui"


@dataclass
class MigrateResult:
    changed: bool
    moved: list[str]
    backup: Path | None
    reason: str


def _split_lines(text: str) -> list[str]:
    """Split on \\n only, keeping the terminator (and a preceding \\r)."""
    parts = text.split("\n")
    lines = [p + "\n" for p in parts[:-1]]
    if parts[-1]:
        lines.append(parts[-1])
    return lines


def _section_name(line: str) -> str | None:
    s = line.strip()
    if s.startswith("[") and s.endswith("]") and len(s) > 2:
        return s[1:-1]
    return None


def _parse(text: str) -> tuple[list[str], list[tuple[str, list[str]]]]:
    """Return (preamble lines, [(section name, lines incl. header)])."""
    preamble: list[str] = []
    sections: list[tuple[str, list[str]]] = []
    for line in _split_lines(text):
        name = _section_name(line)
        if name is not None:
            sections.append((name, [line]))
        elif sections:
            sections[-1][1].append(line)
        else:
            preamble.append(line)
    return preamble, sections


def _find_entry(lines: list[str], key: str) -> list[str]:
    """Comment lines above `key = value` plus that line itself ([] if absent)."""
    block: list[str] = []
    for line in lines[1:]:
        s = line.strip()
        if s.startswith("#"):
            block.append(line)
        elif s == "":
            block = []
        elif "=" in s:
            if s.split("=", 1)[0].strip() == key:
                return [*block, line]
            block = []
        else:
            block = []
    return []


def _ensure_eol(lines: list[str], eol: str) -> list[str]:
    if lines and not lines[-1].endswith("\n"):
        lines = [*lines[:-1], lines[-1] + eol]
    return lines


def _write_atomic(path: Path, data: bytes) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_bytes(data)
    os.replace(tmp, path)


def migrate(config_dir: Path) -> MigrateResult:
    config_dir = Path(config_dir)
    old_path = config_dir / OLD_NAME
    new_path = config_dir / NEW_NAME
    if new_path.exists():
        return MigrateResult(False, [], None, "ui config already exists")
    if not old_path.is_file():
        return MigrateResult(False, [], None, "no old bridge config")

    raw = old_path.read_bytes()
    text = raw.decode("utf-8")
    eol = "\r\n" if "\r\n" in text else "\n"
    preamble, sections = _parse(text)
    by_name = {name: lines for name, lines in sections}
    moved = [n for n in UI_SECTIONS if n in by_name]
    if not moved:
        return MigrateResult(False, [], None, "no UI sections in the old config")

    backup = config_dir / BACKUP_NAME
    if not backup.exists():
        _write_atomic(backup, raw)

    new_lines: list[str] = [
        f"## Settings file was created by migrate_cfg.py from {OLD_NAME}{eol}",
        f"## Plugin GUID: {NEW_GUID}{eol}",
        eol,
    ]
    if "Harmony" in by_name:
        chunk = _ensure_eol(by_name["Harmony"], eol)
        new_lines += chunk
        if chunk[-1].strip():
            new_lines.append(eol)

    attack = ["[Attack]" + eol, eol]
    server = by_name.get("Server", [])
    old_attack = by_name.get("Attack", [])
    entries = (
        _find_entry(server, "AllowAttack"),
        _find_entry(old_attack, "NeutralSeekerLegNm"),
    )
    for entry in entries:
        if entry:
            attack += _ensure_eol(entry, eol) + [eol]
    if len(attack) > 2:
        new_lines += attack

    for name in moved:
        chunk = _ensure_eol(by_name[name], eol)
        new_lines += chunk
        if chunk[-1].strip():
            new_lines.append(eol)

    kept: list[str] = list(preamble)
    for name, lines in sections:
        if name not in UI_SECTIONS:
            kept += lines

    _write_atomic(new_path, "".join(new_lines).encode("utf-8"))
    _write_atomic(old_path, "".join(kept).encode("utf-8"))
    return MigrateResult(True, moved, backup, "migrated")


ASW_HOTKEY_OLD = (
    "LeftAlt+A"  # 0.15 default: A is the game's own key (rule T-082), the key was never usable
)
ASW_HOTKEY_NEW = "LeftAlt+U|LeftAlt+J|F7"  # 0.16.0: the first candidate no game binding uses
OVERVIEW_COLUMNS_OLD = "icon,type,dist,brg,spd,alt,crs"  # default before 0.16.3 (T-111)
OVERVIEW_COLUMNS_NEW = OVERVIEW_COLUMNS_OLD + ",idn"
OVERVIEW_COLUMNS_0_20 = "icon,type,dist,brg,spd,alt,dep,crs,cpa,age,idn"  # default of 0.20..0.28 (T-153, ADR-0045); config step 4 replaces it when untouched
OVERVIEW_COLUMNS_HITS = (
    OVERVIEW_COLUMNS_0_20 + ",hits,dmg"
)  # new installs (T-168, ADR-0064 item 17): never written by upgrade_ui_cfg except by step 4


@dataclass(frozen=True)
class UiCfgStep:
    """One versioned edit of the UI config. Mirrors UiCfgStep in UiConfigMigrator.cs (a test compares both tables)."""

    version: int
    op: str  # "Remove" | "Rename" | "Replace" | "Copy" | "ClearUnlessListed"
    section: str
    key: str
    new_key: str | None = None  # Rename: the new name; Copy: the target key (same section)
    old_value: str | None = (
        None  # Replace: the value is replaced when it equals this old default (quotes ignored). ClearUnlessListed: the list key
    )
    new_value: str | None = (
        None  # Replace: the new default. ClearUnlessListed: the item the list must name
    )


UI_CFG_CURRENT = 13
VERSION_SECTION = "Ui"
VERSION_KEY = "ConfigVersion"
# Keep in sync with UiConfigMigrator.Steps (UiConfigMigrator.cs); tests/test_sea_power_ui_config_migrations.py compares them.
UI_CFG_STEPS: tuple[UiCfgStep, ...] = (
    UiCfgStep(2, "Remove", "Overview", "Units"),
    UiCfgStep(2, "Remove", "Overview", "DoubleClickZoom"),
    UiCfgStep(2, "Remove", "RadarCoverage", "RefreshSeconds"),
    UiCfgStep(2, "Remove", "Menu", "Language"),
    # 0.28.0 (live check 0.8.7): the old defaults were taken by game bindings; only the untouched old default is replaced.
    UiCfgStep(3, "Replace", "RadarCoverage", "Hotkey", None, "LeftAlt+R", "LeftAlt+V|LeftAlt+Y"),
    UiCfgStep(3, "Replace", "ContactInfo", "Hotkey", None, "LeftAlt+I", "LeftAlt+N|LeftAlt+E"),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Depth",
        None,
        "LeftAlt+LeftShift+Alpha1",
        "LeftAlt+LeftShift+Keypad1",
    ),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Speed",
        None,
        "LeftAlt+LeftShift+Alpha2",
        "LeftAlt+LeftShift+Keypad2",
    ),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Ttg",
        None,
        "LeftAlt+LeftShift+Alpha3",
        "LeftAlt+LeftShift+Keypad3",
    ),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Cpa",
        None,
        "LeftAlt+LeftShift+Alpha4",
        "LeftAlt+LeftShift+Keypad4",
    ),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Age",
        None,
        "LeftAlt+LeftShift+Alpha5",
        "LeftAlt+LeftShift+Keypad5",
    ),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Noise",
        None,
        "LeftAlt+LeftShift+Alpha6",
        "LeftAlt+LeftShift+Keypad6",
    ),
    UiCfgStep(
        3,
        "Replace",
        "ContactInfo",
        "Hotkey.Waypoint",
        None,
        "LeftAlt+LeftShift+Alpha7",
        "LeftAlt+LeftShift+Keypad7",
    ),
    UiCfgStep(3, "Replace", "RangeRings", "Hotkey", None, "LeftAlt+Z", "LeftAlt+G|LeftAlt+B"),
    UiCfgStep(
        3,
        "Replace",
        "RangeRings",
        "Hotkey.Group",
        None,
        "LeftAlt+LeftShift+Z",
        "LeftAlt+LeftShift+B|LeftAlt+LeftShift+H",
    ),
    UiCfgStep(3, "Replace", "Settings", "Hotkey", None, "LeftAlt+Comma|F10", "LeftAlt+Comma"),
    # T-168: the Overview columns hits and dmg (hit estimate); only the untouched old default list gets them.
    UiCfgStep(
        4,
        "Replace",
        "Overview",
        "Columns",
        None,
        OVERVIEW_COLUMNS_0_20,
        OVERVIEW_COLUMNS_HITS,
    ),
    # T-171 (ADR-0068): Show.Weapons / Types become Show.Aaw / Show.Asuw / Show.Asw; the labels are on for everyone once.
    UiCfgStep(5, "Copy", "RangeRings", "Show.Weapons", "Show.Aaw"),
    UiCfgStep(5, "Copy", "RangeRings", "Show.Weapons", "Show.Asuw"),
    UiCfgStep(5, "Copy", "RangeRings", "Show.Weapons", "Show.Asw"),
    UiCfgStep(5, "ClearUnlessListed", "RangeRings", "Show.Aaw", None, "Types", "AAW"),
    UiCfgStep(5, "ClearUnlessListed", "RangeRings", "Show.Asuw", None, "Types", "ASuW"),
    UiCfgStep(5, "ClearUnlessListed", "RangeRings", "Show.Asw", None, "Types", "ASW"),
    UiCfgStep(5, "Remove", "RangeRings", "Show.Weapons"),
    UiCfgStep(5, "Remove", "RangeRings", "Types"),
    UiCfgStep(5, "Replace", "RangeRings", "Labels", None, "false", "true"),
    # 0.29.1 (customer 2026-10-22, ADR-0068 supplement): no category rings and no fire-sector layer (only the checked weapons draw rings):
    # their keys go; the sonar rings start off once (the old default "true").
    UiCfgStep(6, "Remove", "RangeRings", "Show.Aaw"),
    UiCfgStep(6, "Remove", "RangeRings", "Show.Asuw"),
    UiCfgStep(6, "Remove", "RangeRings", "Show.Asw"),
    UiCfgStep(6, "Remove", "RangeRings", "Show.Sectors"),
    UiCfgStep(6, "Remove", "RangeRings", "Show.Enemy"),
    UiCfgStep(6, "Remove", "RangeRings", "ShowMinRange"),
    UiCfgStep(6, "Remove", "RangeRings", "MaxSectorUnits"),
    UiCfgStep(6, "Replace", "RangeRings", "Show.Sonar", None, "true", "false"),
    # T-173 (ADR-0074): the attack question is gone: its timeout key goes.
    UiCfgStep(7, "Remove", "Asw", "ConfirmTimeoutS"),
    # T-176 (ADR-0076): ReplanDeg is replaced by ReanchorDeg.
    UiCfgStep(8, "Remove", "Asw", "ReplanDeg"),
    # T-179 (ADR-0079): [Cap] AlertOnUnknown becomes AlertTargets (air,unknown / air).
    UiCfgStep(9, "Copy", "Cap", "AlertOnUnknown", "AlertTargets"),
    UiCfgStep(9, "Replace", "Cap", "AlertTargets", None, "true", "air,unknown"),
    UiCfgStep(9, "Replace", "Cap", "AlertTargets", None, "false", "air"),
    UiCfgStep(9, "Remove", "Cap", "AlertOnUnknown"),
    # T-299: BuoyRevive on by default; a 0.30.25/0.30.26 file holds the old default false (on once, like Labels in v5)
    UiCfgStep(10, "Replace", "Harmony", "BuoyRevive", None, "false", "true"),
    # T-316 (ADR-0123): the MAD pass is off by default; a file up to 0.31.0 holds the old default true (off once)
    UiCfgStep(11, "Replace", "Asw", "MadVerify", None, "true", "false"),
    # T-330 (ADR-0128): lower return defaults (missiles 20 %, fuel floor 5 %); a file up to 0.31.x holds the old defaults (replaced once)
    UiCfgStep(12, "Replace", "Cap", "MissilePct", None, "0.25", "0.2"),
    UiCfgStep(12, "Replace", "Cap", "FuelFloorPct", None, "0.15", "0.05"),
    # T-337 (ADR-0131): the fuel floor is off by default (return only by the reserve in miles); the v12 default 0.05 becomes 0 once, own values stay
    UiCfgStep(13, "Replace", "Cap", "FuelFloorPct", None, "0.05", "0"),
)


def _key_of(line: str) -> str | None:
    s = line.strip()
    if not s or s.startswith(("#", "[")) or "=" not in s:
        return None
    return s.split("=", 1)[0].strip() or None


def detect_version(text: str) -> int:
    """[Ui] ConfigVersion; 1 when the file has entries but no such key (or junk); current for an empty text."""
    lines = _split_lines(text)
    if not any(_key_of(line) for line in lines):
        return UI_CFG_CURRENT
    section: str | None = None
    for line in lines:
        name = _section_name(line)
        if name is not None:
            section = name
        elif section == VERSION_SECTION and _key_of(line) == VERSION_KEY:
            value = line.split("=", 1)[1].strip()
            try:
                v = int(value)
            except ValueError:
                return 1
            return v if v >= 1 else 1
    return 1


def _find_key(lines: list[str], section: str, key: str) -> tuple[int, int] | None:
    """(start of the comment block, index of the `key = value` line) of an entry, or None."""
    cur: str | None = None
    block_start = 0
    for i, line in enumerate(lines):
        name = _section_name(line)
        if name is not None:
            cur = name
            block_start = i + 1
            continue
        s = line.strip()
        if s == "":
            block_start = i + 1
            continue
        if s.startswith("#"):
            continue
        if cur == section and _key_of(line) == key:
            return block_start, i
        block_start = i + 1
    return None


def _drop_key(lines: list[str], section: str, key: str) -> bool:
    """Delete `[section] key` with the comment lines above it in place (one doubled blank line goes too)."""
    found = _find_key(lines, section, key)
    if found is None:
        return False
    start, idx = found
    end = idx + 1
    if (
        end < len(lines)
        and lines[end].strip() == ""
        and start > 0
        and lines[start - 1].strip() == ""
    ):
        end += 1  # keep a single blank line between the neighbours
    del lines[start:end]
    return True


def _rename_key(lines: list[str], section: str, key: str, new_key: str) -> bool:
    """Move the value of `key` to `new_key` unless `new_key` already exists."""
    found = _find_key(lines, section, key)
    if found is None or _find_key(lines, section, new_key) is not None:
        return False
    idx = found[1]
    line = lines[idx]
    pos = line.index(key)
    lines[idx] = line[:pos] + new_key + line[pos + len(key) :]
    return True


def _replace_value(lines: list[str], step: UiCfgStep, eol: str) -> bool:
    """Replace the value of `[section] key` when it still equals the old default (the user's own value stays)."""
    found = _find_key(lines, step.section, step.key)
    if found is None or step.old_value is None or step.new_value is None:
        return False
    idx = found[1]
    value = lines[idx].split("=", 1)[1].strip().strip("\"' ")
    if value != step.old_value:
        return False
    end = eol if lines[idx].endswith("\n") else ""
    lines[idx] = f"{step.key} = {step.new_value}{end}"
    return True


def _value_of(line: str) -> str:
    return line.split("=", 1)[1].strip()


def _insert_after(lines: list[str], index: int, entry: str, eol: str) -> None:
    lines[:] = _ensure_eol(lines[: index + 1], eol) + [entry + eol] + lines[index + 1 :]


def _copy_value(lines: list[str], step: UiCfgStep, eol: str) -> bool:
    """Copy: `[section] key` exists and `new_key` does not -> `new_key = value` right after the source line."""
    found = _find_key(lines, step.section, step.key)
    if found is None or step.new_key is None or _find_key(lines, step.section, step.new_key):
        return False
    _insert_after(lines, found[1], f"{step.new_key} = {_value_of(lines[found[1]])}", eol)
    return True


def _clear_unless_listed(lines: list[str], step: UiCfgStep, eol: str) -> bool:
    """ClearUnlessListed: the list key (`old_value`) exists and does not name the item (`new_value`; CSV, trimmed,
    any case) -> `key = false` (the line is set, or inserted after the list line)."""
    if step.old_value is None or step.new_value is None:
        return False
    listed = _find_key(lines, step.section, step.old_value)
    if listed is None:
        return False
    items = [x.strip().lower() for x in _value_of(lines[listed[1]]).strip("\"' ").split(",")]
    if step.new_value.lower() in items:
        return False
    found = _find_key(lines, step.section, step.key)
    if found is None:
        _insert_after(lines, listed[1], f"{step.key} = false", eol)
        return True
    if _value_of(lines[found[1]]).strip("\"' ") == "false":
        return False
    end = (
        "\r\n"
        if lines[found[1]].endswith("\r\n")
        else "\n"
        if lines[found[1]].endswith("\n")
        else ""
    )
    lines[found[1]] = f"{step.key} = false{end}"
    return True


def _set_version(lines: list[str], eol: str) -> None:
    vline = f"{VERSION_KEY} = {UI_CFG_CURRENT}"
    found = _find_key(lines, VERSION_SECTION, VERSION_KEY)
    if found is not None:
        i = found[1]
        end = "\r\n" if lines[i].endswith("\r\n") else "\n" if lines[i].endswith("\n") else ""
        lines[i] = vline + end
        return
    hdr = last = -1
    for i, line in enumerate(lines):
        name = _section_name(line)
        if name is not None:
            if hdr >= 0:
                break
            if name == VERSION_SECTION:
                hdr = last = i
        elif hdr >= 0 and line.strip():
            last = i
    if hdr >= 0:
        lines[:] = _ensure_eol(lines[: last + 1], eol) + [vline + eol] + lines[last + 1 :]
        return
    if lines:
        lines[:] = _ensure_eol(lines, eol)
        if lines[-1].strip():
            lines.append(eol)
    lines += [f"[{VERSION_SECTION}]{eol}", eol, vline + eol]


def upgrade_ui_cfg(config_dir: Path) -> list[str]:
    """Edits of an existing UI config up to the current version; returns the changes made (empty = none).

    BepInEx keeps the stored value of a key whose default changed, so the old
    `[Asw] Hotkey = LeftAlt+A` would survive the update. New keys are added by the plugin itself with their defaults.
    Versioned steps (T-123, UI_CFG_STEPS): keys removed or renamed in a later version; `[Ui] ConfigVersion` is written.
    The old `[Menu] Language` removal (0.19.0, T-103) is the same table (version 2).
    `[RadarCoverage] RenderMode` is NOT touched: the default stays `auto` (the Noesis layer).
    A file with the same version and no value edits is left byte for byte as it is.
    """
    path = Path(config_dir) / NEW_NAME
    if not path.is_file():
        return []
    raw = path.read_bytes()
    text = raw.decode("utf-8")
    eol = "\r\n" if "\r\n" in text else "\n"
    lines = _split_lines(text)
    stored = detect_version(text)
    changes: list[str] = []
    for step in UI_CFG_STEPS:
        if step.version <= stored:
            continue
        if step.op == "Copy":
            if _copy_value(lines, step, eol):
                changes.append(f"[{step.section}] {step.key} -> {step.new_key} (copied)")
        elif step.op == "ClearUnlessListed":
            if _clear_unless_listed(lines, step, eol):
                changes.append(
                    f"[{step.section}] {step.key} = false ({step.old_value} has no {step.new_value})"
                )
        elif step.op == "Remove":
            if _drop_key(lines, step.section, step.key):
                changes.append(
                    f"[{step.section}] {step.key} removed (config version {step.version})"
                )
        elif step.op == "Replace":
            if _replace_value(lines, step, eol):
                changes.append(f"[{step.section}] {step.key} {step.old_value} -> {step.new_value}")
        elif step.new_key and _rename_key(lines, step.section, step.key, step.new_key):
            changes.append(f"[{step.section}] {step.key} -> {step.new_key}")
    section: str | None = None
    for i, line in enumerate(lines):
        name = _section_name(line)
        if name is not None:
            section = name
            continue
        s = line.strip()
        if section == "Asw" and not s.startswith("#") and "=" in s:
            key, value = (x.strip() for x in s.split("=", 1))
            if key == "Hotkey" and value == ASW_HOTKEY_OLD:
                end = eol if line.endswith("\n") else ""
                lines[i] = f"Hotkey = {ASW_HOTKEY_NEW}{end}"
                changes.append(f"[Asw] Hotkey {ASW_HOTKEY_OLD} -> {ASW_HOTKEY_NEW}")
        elif section == "Overview" and not s.startswith("#") and "=" in s:
            key, value = (x.strip() for x in s.split("=", 1))
            # only the untouched old default is extended: a customised list is the user's choice
            if key == "Columns" and value.strip("\"' ") == OVERVIEW_COLUMNS_OLD:
                end = eol if line.endswith("\n") else ""
                lines[i] = f"Columns = {OVERVIEW_COLUMNS_NEW}{end}"
                changes.append("[Overview] Columns += idn")
    if stored < UI_CFG_CURRENT:
        _set_version(lines, eol)
        changes.append(f"[{VERSION_SECTION}] {VERSION_KEY} {stored} -> {UI_CFG_CURRENT}")
    if not changes:
        return []
    backup = path.with_name(f"{NEW_NAME}.bak-v{stored}")
    if not backup.exists():
        _write_atomic(backup, raw)
    _write_atomic(path, "".join(lines).encode("utf-8"))
    return changes


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 1:
        print("usage: migrate_cfg.py <BepInEx/config dir>", file=sys.stderr)
        return 2
    cfg = Path(args[0])
    if not cfg.is_dir():
        print(f"migrate_cfg: not a directory: {cfg}", file=sys.stderr)
        return 2
    try:
        res = migrate(cfg)
    except (OSError, UnicodeDecodeError) as e:
        print(f"migrate_cfg: {e}", file=sys.stderr)
        return 2
    if res.changed:
        print(f"migrate_cfg: moved {', '.join(res.moved)}; backup {res.backup}")
    else:
        print(f"migrate_cfg: nothing to do ({res.reason})")
    try:
        for change in upgrade_ui_cfg(cfg):
            print(f"migrate_cfg: upgraded {change}")
    except (OSError, UnicodeDecodeError) as e:
        print(f"migrate_cfg: {e}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
