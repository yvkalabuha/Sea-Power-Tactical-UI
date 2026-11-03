#!/usr/bin/env python3
# ruff: noqa: E501
"""T-105: offline checks of the Steam Workshop mod folder made by `package.sh workshop`. Stdlib only.

Exit code: 0 ok, 1 violations (printed, one per line), 2 bad input.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
import zipfile
from pathlib import Path

DLL = "SeaPowerTacticalUI.dll"
LOADER = "loader/SeaPowerTacticalUI.Loader.dll"   # T-314 (ADR-0121): own BepInEx loader, the only file allowed in loader/
REQUIRED: frozenset[str] = frozenset(
    {DLL, LOADER, "SeaPowerTacticalUI.pdb", "_info.ini", "LICENSE", "NOTICE", "README.md", "CHANGELOG.md", "TRANSLATING.md", "thumbnail.jpg"}
)
# allowed names (relative, "/" separated); patterns: lang tables and the source archive
ALLOWED: frozenset[str] = REQUIRED
LANG_RE = re.compile(r"^lang/language_[a-z]{2,3}/[^/]+\.ini$")
SOURCE_RE = re.compile(r"^source/SeaPowerTacticalUI-[0-9][0-9A-Za-z.+-]*-src\.zip$")

FORBIDDEN_IN_DLL = (
    "System.Net.Sockets", "TcpListener", "TcpClient", "System.Net.HttpWebRequest", "UnityWebRequest", "DevServer", "DevPlugin",
    "FakeProc", "FakeTask", "MakeFake", "AllocProbe", "ProcThreadsTests", "ModThreadsTests", "ProcTableTests", "HbLoopTests",   # T-303: /proc test fixtures
    "AutoIgnoreStartupWarnings", "PumpDialogs",   # T-306: the UI mod never presses Ignore
)
# T-314: assembly references the loader must not have (exact names in the #Strings heap: "\0Name\0")
FORBIDDEN_LOADER_REFS = ("Assembly-CSharp", "Seapower-Scripts", "AnchorChain", "SeaPowerTacticalUI", "VoiceBotBridge", "0Harmony")
THUMB_MAX = 1_048_576
SOURCE_REQUIRED = (
    "SeaPowerTacticalUI/SeaPowerTacticalUI.csproj", "SeaPowerTacticalUI/UiCore.cs", "Common/GameApi.cs",
    "SeaPowerTacticalUI.Loader/SeaPowerTacticalUI.Loader.csproj", "SeaPowerTacticalUI.Loader/LoaderLogic.cs",   # T-314
)
SOURCE_FORBIDDEN_DIRS = ("VoiceBotBridge", "SeaPowerTacticalUI.Dev", "tests", "obj", "bin", "out")
ORIGINAL_REL = "Sea Power_Data/StreamingAssets/original"


def _rel_files(mod_dir: Path) -> list[str]:
    return sorted(p.relative_to(mod_dir).as_posix() for p in mod_dir.rglob("*") if p.is_file())


def _is_allowed(rel: str) -> bool:
    return rel in ALLOWED or bool(LANG_RE.match(rel)) or bool(SOURCE_RE.match(rel))


def _jpeg_size(data: bytes) -> tuple[int, int] | None:
    """(width, height) from the first SOF marker, None if not found."""
    i = 2
    while i + 9 < len(data):
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7 or marker == 0xFF:
            i += 1 if marker == 0xFF else 2
            continue
        length = int.from_bytes(data[i + 2 : i + 4], "big")
        if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
            return int.from_bytes(data[i + 7 : i + 9], "big"), int.from_bytes(data[i + 5 : i + 7], "big")
        i += 2 + length
    return None


def _ini_sections(text: str) -> dict[str, dict[str, str]]:
    out: dict[str, dict[str, str]] = {}
    cur: dict[str, str] | None = None
    for raw in text.splitlines():
        line = raw.strip()
        if line.startswith("[") and line.endswith("]"):
            cur = out.setdefault(line[1:-1], {})
        elif cur is not None and "=" in line and not line.startswith(("#", ";")):
            k, v = line.split("=", 1)
            cur[k.strip()] = v.strip()
    return out


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


def _check_dll(mod_dir: Path, problems: list[str]) -> None:
    dll = mod_dir / DLL
    if not dll.is_file():
        return
    data = dll.read_bytes()
    for word in FORBIDDEN_IN_DLL:
        if word.encode("ascii") in data or word.encode("utf-16-le") in data:
            problems.append(f"{DLL}: forbidden name in the metadata: {word}")


def _check_loader(mod_dir: Path, problems: list[str]) -> None:
    """T-314: the loader DLL has no forbidden names and references neither the game, nor AnchorChain, nor the mod (heuristic over #Strings)."""
    dll = mod_dir / LOADER
    if not dll.is_file():
        return
    data = dll.read_bytes()
    for word in FORBIDDEN_IN_DLL:
        if word.encode("ascii") in data or word.encode("utf-16-le") in data:
            problems.append(f"{LOADER}: forbidden name in the metadata: {word}")
    for name in FORBIDDEN_LOADER_REFS:
        if b"\x00" + name.encode("ascii") + b"\x00" in data:
            problems.append(f"{LOADER}: references {name}")


def _check_originals(mod_dir: Path, rels: list[str], game_dir: Path, problems: list[str]) -> None:
    orig = game_dir / ORIGINAL_REL
    if not orig.is_dir():
        return
    by_size: dict[int, list[str]] = {}
    for rel in rels:
        by_size.setdefault((mod_dir / rel).stat().st_size, []).append(rel)
    hashes: dict[str, str] = {}
    for f in orig.rglob("*"):
        if not f.is_file():
            continue
        size = f.stat().st_size
        if size not in by_size:
            continue
        digest = hashlib.sha256(f.read_bytes()).hexdigest()
        for rel in by_size[size]:
            if rel not in hashes:
                hashes[rel] = hashlib.sha256((mod_dir / rel).read_bytes()).hexdigest()
            if hashes[rel] == digest:
                problems.append(f"{rel}: identical to a game file under original/ ({f.relative_to(orig).as_posix()})")


def _check_info(mod_dir: Path, problems: list[str]) -> None:
    p = mod_dir / "_info.ini"
    if not p.is_file():
        return
    sec = _ini_sections(_read(p))
    for lang in ("Language_en", "Language_ru"):
        for key in ("Name", "Description"):
            if not sec.get(lang, {}).get(key):
                problems.append(f"_info.ini: [{lang}] {key} is missing")
    if not sec.get("Compatibility", {}).get("ApproximateVersion"):
        problems.append("_info.ini: [Compatibility] ApproximateVersion is missing")


def _check_texts(mod_dir: Path, version: str, problems: list[str]) -> None:
    p = mod_dir / "LICENSE"
    if p.is_file():
        t = _read(p)
        for need in ("GNU GENERAL PUBLIC LICENSE", "Version 3, 29 June 2007", "Additional permission under GNU GPL version 3 section 7"):
            if need not in t:
                problems.append(f"LICENSE: missing text: {need}")
        if "<COPYRIGHT HOLDER>" in t:
            problems.append("LICENSE: placeholder <COPYRIGHT HOLDER> is still there")
    p = mod_dir / "NOTICE"
    if p.is_file():
        t = _read(p)
        for name in ("BepInEx", "HarmonyX", "MonoMod", "AnchorChain"):
            if name not in t:
                problems.append(f"NOTICE: {name} is not mentioned")
        if "<COPYRIGHT HOLDER>" in t:
            problems.append("NOTICE: placeholder <COPYRIGHT HOLDER> is still there")
    p = mod_dir / "README.md"
    if p.is_file():
        t = _read(p)
        if not t.startswith(f"# Sea Power Tactical UI {version}"):
            problems.append(f"README.md: header must be '# Sea Power Tactical UI {version}'")
        for need in ("AnchorChain.Preloader", "WINEDLLOVERRIDES", "BepInEx 5"):
            if need not in t:
                problems.append(f"README.md: missing text: {need}")


def _check_thumbnail(mod_dir: Path, problems: list[str]) -> None:
    p = mod_dir / "thumbnail.jpg"
    if not p.is_file():
        return
    data = p.read_bytes()
    if data[:3] != b"\xff\xd8\xff":
        problems.append("thumbnail.jpg: not a JPEG")
        return
    if len(data) > THUMB_MAX:
        problems.append(f"thumbnail.jpg: larger than 1 MiB ({len(data)} bytes)")
    size = _jpeg_size(data)
    if size != (512, 512):
        problems.append(f"thumbnail.jpg: must be 512x512, got {size[0]}x{size[1]}" if size else "thumbnail.jpg: size not found, must be 512x512")


def _check_source(mod_dir: Path, rels: list[str], problems: list[str]) -> None:
    zips = [r for r in rels if SOURCE_RE.match(r)]
    if not zips:
        problems.append("source/SeaPowerTacticalUI-<version>-src.zip: missing")
        return
    for rel in zips:
        try:
            with zipfile.ZipFile(mod_dir / rel) as z:
                names = z.namelist()
        except zipfile.BadZipFile:
            problems.append(f"{rel}: not a zip")
            continue
        for need in SOURCE_REQUIRED:
            if need not in names:
                problems.append(f"{rel}: missing {need}")
        for n in names:
            parts = n.split("/")
            if any(part in SOURCE_FORBIDDEN_DIRS for part in parts[:-1]) or parts[0] in SOURCE_FORBIDDEN_DIRS:
                problems.append(f"{rel}: forbidden entry {n}")


def check_tree(mod_dir: Path, version: str, game_dir: Path | None) -> list[str]:
    """Violations of the Workshop folder (empty list = ok)."""
    problems: list[str] = []
    rels = _rel_files(mod_dir)
    for rel in rels:
        name = rel.rsplit("/", 1)[-1]
        if (name.startswith("VoiceBotBridge") and name.endswith(".dll")) or name.endswith(".Dev.dll"):
            problems.append(f"{rel}: voice / dev DLL must not be packed")
        elif not _is_allowed(rel):
            problems.append(f"{rel}: file is not on the allowed list")
    for rel in sorted(REQUIRED):
        if rel not in rels:
            problems.append(f"{rel}: missing")
    if not any(r.startswith("lang/language_en/") for r in rels) or not any(r.startswith("lang/language_ru/") for r in rels):
        problems.append("lang/language_en and lang/language_ru: tables missing")
    _check_dll(mod_dir, problems)
    _check_loader(mod_dir, problems)
    if game_dir is not None:
        _check_originals(mod_dir, [r for r in rels if (mod_dir / r).is_file()], game_dir, problems)
    _check_info(mod_dir, problems)
    _check_texts(mod_dir, version, problems)
    _check_thumbnail(mod_dir, problems)
    _check_source(mod_dir, rels, problems)
    return problems


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Check the Sea Power Tactical UI Workshop folder")
    ap.add_argument("mod_dir", type=Path)
    ap.add_argument("--version", required=True)
    ap.add_argument("--game", type=Path, default=None, help="game folder: no packed file may equal a file of StreamingAssets/original")
    args = ap.parse_args(argv)
    if not args.mod_dir.is_dir():
        print(f"not a folder: {args.mod_dir}", file=sys.stderr)
        return 2
    game = args.game if args.game is not None and args.game.is_dir() else None
    problems = check_tree(args.mod_dir, args.version, game)
    for p in problems:
        print(p)
    if problems:
        print(f"workshop_check: {len(problems)} violation(s)")
        return 1
    print("workshop_check: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
