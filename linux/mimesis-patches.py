#!/usr/bin/env python3
# MIMESIS Patches for Linux - install, update, build and remove the patches (Steam + Proton)
# Patches by Mohamed Darwesh (@medovanx) - https://github.com/medovanx/mimesis-patches
#
# The Linux counterpart of the Windows installer (Installer/Engine.cs + Program.cs). MIMESIS only ships for Windows,
# so on Linux it runs under Proton. The patches are MelonLoader mods in <game>/Mods; MelonLoader is the *Windows* x64
# package, and Wine is told to load MelonLoader's version.dll through a per-app DLL override in the game's Proton
# prefix registry (otherwise Wine uses its own built-in version.dll and MelonLoader never starts).
#
# Python 3.8+, standard library only. Run with --help for the commands.

import argparse
import datetime
import hashlib
import http.client
import json
import os
import pwd
import re
import shlex
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

TOOL_VERSION = "1.0.0"
APP_ID = "2827200"
DEFAULT_REPO = "oliveira-sh/mimesis-patches"
USER_AGENT = "mimesis-patches-linux-installer"

# MelonLoader, the mod loader the patches run in (the one MIMESIS mods on Thunderstore use). The game is a Windows
# build running under Proton, so this is the Windows package, pinned and checksummed.
MELONLOADER_VERSION = "0.7.3"
MELONLOADER_URL = "https://github.com/LavaGang/MelonLoader/releases/download/v%s/MelonLoader.x64.zip" % MELONLOADER_VERSION
MELONLOADER_SHA256 = "5b2b2f3d1cd42b59ec886c5bdc2663edae87a0097a4f4a8f58c0965a99dda416"

# Wine DLL override, per app so other programs in the prefix are unaffected. As written in user.reg (Wine escapes the
# key separators as "\\").
REG_KEY = "\\\\".join(["Software", "Wine", "AppDefaults", "MIMESIS.exe", "DllOverrides"])
REG_NAME = "version"
REG_VALUE = "native,builtin"
LAUNCH_OPTION = 'WINEDLLOVERRIDES="version=n,b" %command%'

# Read by the in-game Patches window (under Wine) to show the update command for this install.
MARKER_NAME = "mimesis-patches-linux.json"

EXIT_FAILED, EXIT_USAGE, EXIT_RUNNING = 1, 2, 3

SCRIPT = Path(os.path.realpath(__file__))


class Patch:
    def __init__(self, name, about, who):
        self.name, self.about, self.who = name, about, who

    @property
    def dll(self):
        return self.name + "Runtime.dll"


# Same list as Installer/Engine.cs: name, what it does, who needs it.
PATCHES = [
    Patch("BiggerLobby", "10-player lobbies + difficulty scaling", "Host"),
    Patch("PSController", "PlayStation button icons", "Anyone"),
    Patch("SpectatorCam", "Free spectator camera", "Anyone"),
    Patch("Minimap", "Minimap of the level", "Anyone"),
    Patch("HostOptions", "Stamina, money, difficulty", "Host"),
    Patch("HudPercent", "Health / radiation % on the HUD", "Anyone"),
    Patch("Inventory", "Host sets 1-8 inventory slots", "Everyone"),
    Patch("LateJoin", "Join a game in progress", "Everyone"),
    Patch("Fov", "Field of view slider", "Anyone"),
    Patch("Revive", "Revive dead teammates", "Host"),
    Patch("FpsCounter", "FPS in the bottom-right corner", "Anyone"),
    Patch("Hallucinations", "Fake mimics at high radiation", "Everyone"),
]
BY_NAME = {p.name: p for p in PATCHES}
# Patches removed from this repo, with what replaced them. Their DLLs are deleted from Mods by install and uninstall:
# a leftover would keep running, and this tool would no longer list it.
RETIRED = {"MimicControl": "Hallucinations"}


class Fail(Exception):
    """An error with a friendly message; code is the exit code."""

    def __init__(self, msg, code=EXIT_FAILED):
        super().__init__(msg)
        self.code = code


# ---------------------------------------------------------------- output

COLOR = False


def paint(code, text):
    return "\033[%sm%s\033[0m" % (code, text) if COLOR else text


def say(msg=""):
    print(msg, flush=True)


def good(msg):
    say(paint("32", msg))


def warn(msg):
    sys.stdout.flush()
    print(paint("33", "Warning: " + msg), file=sys.stderr, flush=True)


def error(msg):
    sys.stdout.flush()
    print(paint("31", "FAILED: " + msg), file=sys.stderr, flush=True)


def q(path):
    """A path or argument quoted for copy-pasting into a shell."""
    return shlex.quote(str(path))


def home():
    return Path(os.path.expanduser("~"))


def cache_dir():
    base = os.environ.get("XDG_CACHE_HOME")
    if not base or not os.path.isabs(base):
        base = str(home() / ".cache")
    return Path(base) / "mimesis-patches"


def sha256_file(path):
    h = hashlib.sha256()
    with open(str(path), "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def atomic_write(path, data, mode_from=None):
    """Writes the file next to its final place, then renames it over the old one (never a half-written file)."""
    path = Path(path)
    fd, tmp = tempfile.mkstemp(prefix="." + path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        if mode_from is not None and os.path.exists(str(mode_from)):
            os.chmod(tmp, stat.S_IMODE(os.stat(str(mode_from)).st_mode))
        os.replace(tmp, str(path))
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def atomic_copy(src, dst):
    tmp = Path(str(dst) + ".mimesis-tmp")
    shutil.copy2(str(src), str(tmp))
    os.replace(str(tmp), str(dst))


def fmt_time(ts):
    return datetime.datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M")


def parse_version(text):
    """System.Version.TryParse: 2-4 dot-separated non-negative numbers."""
    parts = text.split(".")
    if not 2 <= len(parts) <= 4 or not all(p.isdigit() for p in parts):
        return None
    return tuple(int(p) for p in parts)


def vstr(v):
    return ".".join(str(x) for x in (tuple(v) + (0, 0))[:3]) if v else None


# ---------------------------------------------------------------- Valve KeyValues (.vdf / .acf)

def vdf_parse(text):
    """libraryfolders.vdf, appmanifest_*.acf, localconfig.vdf -> nested dicts with lowercased keys (Steam's keys are
    case-insensitive). Handles quoted strings with escapes (\\\\, \\"), // comments and unquoted tokens."""
    tokens = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c.isspace():
            i += 1
        elif c == "/" and text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j + 1
        elif c in "{}":
            tokens.append(c)
            i += 1
        elif c == '"':
            i += 1
            buf = []
            while i < n and text[i] != '"':
                if text[i] == "\\" and i + 1 < n:
                    buf.append({"n": "\n", "t": "\t"}.get(text[i + 1], text[i + 1]))
                    i += 2
                else:
                    buf.append(text[i])
                    i += 1
            i += 1
            tokens.append(("", "".join(buf)))
        else:
            j = i
            while j < n and not text[j].isspace() and text[j] not in '{}"':
                j += 1
            if c != "[":   # [$WIN32]-style conditionals don't matter here
                tokens.append(("", text[i:j]))
            i = j
    root = {}
    stack, key = [root], None
    for t in tokens:
        if t == "{":
            child = {}
            if key is not None:
                stack[-1][key.lower()] = child
            stack.append(child)
            key = None
        elif t == "}":
            if len(stack) > 1:
                stack.pop()
            key = None
        elif key is None:
            key = t[1]
        else:
            stack[-1][key.lower()] = t[1]
            key = None
    return root


def vdf_get(data, *keys):
    """data[k1][k2]... or None (also when the file has an unexpected shape)."""
    for k in keys:
        data = data.get(k) if isinstance(data, dict) else None
    return data


def vdf_file(path):
    try:
        return vdf_parse(Path(path).read_text(encoding="utf-8", errors="replace"))
    except (OSError, ValueError):
        return {}


# ---------------------------------------------------------------- finding Steam, the game and its Proton prefix

def steam_roots():
    """Steam installs on this machine: native, Flatpak and Snap (deduplicated by real path)."""
    h = home()
    candidates = []
    if os.environ.get("XDG_DATA_HOME"):
        candidates.append((Path(os.environ["XDG_DATA_HOME"]) / "Steam", "Steam"))
    candidates += [
        (h / ".local/share/Steam", "Steam"),
        (h / ".steam/steam", "Steam"),
        (h / ".steam/root", "Steam"),
        (h / ".var/app/com.valvesoftware.Steam/.local/share/Steam", "Flatpak Steam"),
        (h / ".var/app/com.valvesoftware.Steam/data/Steam", "Flatpak Steam"),
        (h / "snap/steam/common/.local/share/Steam", "Snap Steam"),
    ]
    roots, seen = [], set()
    for path, kind in candidates:
        try:
            if not path.is_dir():
                continue
            real = os.path.realpath(str(path))
        except OSError:
            continue
        if real not in seen:
            seen.add(real)
            roots.append((Path(real), kind))
    return roots


def library_paths(vdf):
    """The "path" of every library in a libraryfolders.vdf (also the old format: "1" "/path")."""
    folders = vdf_get(vdf_file(vdf), "libraryfolders")
    out = []
    if isinstance(folders, dict):
        for k, v in folders.items():
            if isinstance(v, dict) and isinstance(v.get("path"), str):
                out.append(v["path"])
            elif isinstance(v, str) and k.isdigit():
                out.append(v)
    return [os.path.expanduser(p) for p in out if p]


def steam_libraries(roots):
    """[(library, steam root, kind)] for every existing library of every Steam install."""
    libs, seen = [], set()
    for root, kind in roots:
        paths = [str(root)]
        for vdf in (root / "steamapps" / "libraryfolders.vdf", root / "config" / "libraryfolders.vdf"):
            paths += library_paths(vdf)
        for p in paths:
            try:
                real = os.path.realpath(p)
                if real in seen or not os.path.isdir(os.path.join(real, "steamapps")):
                    continue   # gone, unmounted (SD card) or already listed
            except OSError:
                continue
            seen.add(real)
            libs.append((Path(real), root, kind))
    return libs


def app_manifest(library):
    acf = library / "steamapps" / ("appmanifest_%s.acf" % APP_ID)
    manifest = vdf_get(vdf_file(acf), "appstate") if acf.is_file() else None
    return manifest if isinstance(manifest, dict) else None


def steam_game_dirs(libs):
    """[(game folder, library, kind, manifest)]: steamapps/common/<installdir> from appmanifest_2827200.acf, or the
    default folder name like the Windows installer."""
    found = []
    for lib, _root, kind in libs:
        manifest = app_manifest(lib)
        installdir = vdf_get(manifest, "installdir")
        if not isinstance(installdir, str) or installdir in ("", ".", "..") or "/" in installdir:
            installdir = "MIMESIS"
        game = lib / "steamapps" / "common" / installdir
        if game.is_dir():
            found.append((game, lib, kind, manifest))
    return found


def game_problem(path):
    """None when path is the Windows (Proton) build of MIMESIS, else why not."""
    if not path.is_dir():
        return "the folder doesn't exist"
    exe = path / "MIMESIS.exe"
    dll = path / "MIMESIS_Data" / "Managed" / "Assembly-CSharp.dll"
    if exe.is_file() and dll.is_file():
        return None
    natives = sorted(p.name for p in path.glob("*.x86_64"))
    if not exe.is_file() and natives:
        return ("this is a native Linux build (%s). These patches and this tool are for the Windows build that "
                "Steam runs with Proton: in Steam, MIMESIS > Properties > Compatibility > tick \"Force the use of a "
                "specific Steam Play compatibility tool\" and pick Proton, let Steam download the game again, then "
                "run this again." % ", ".join(natives))
    if not exe.is_file():
        return "no MIMESIS.exe in it"
    return "no MIMESIS_Data/Managed/Assembly-CSharp.dll in it"


def normalize_game_arg(text):
    """A game folder from the command line; MIMESIS.exe, MIMESIS_Data/Managed or Assembly-CSharp.dll also work."""
    p = Path(os.path.expanduser(text)).absolute()
    if p.is_file():
        p = p.parent.parent.parent if p.name.lower() == "assembly-csharp.dll" else p.parent
    elif p.name == "Managed" and p.parent.name.endswith("_Data"):
        p = p.parent.parent
    return Path(os.path.normpath(str(p)))


class Game:
    def __init__(self, path, how, libs, library=None, manifest=None):
        self.path, self.how, self.libs = path, how, libs
        # <library>/steamapps/common/<installdir>: the library usually holds the Proton prefix too
        if library is None and path.parent.name == "common" and path.parent.parent.name == "steamapps":
            library = path.parent.parent.parent
        self.library = library
        self.manifest = manifest if manifest is not None else (app_manifest(library) if library else None)
        self.prefix = None
        self.prefix_given = False   # --prefix

    @property
    def managed(self):
        return self.path / "MIMESIS_Data" / "Managed"

    @property
    def mods(self):
        return self.path / "Mods"


def resolve_game(args):
    """--game-dir > $MIMESIS_DIR > the folder this repo is cloned into > Steam libraries."""
    roots = steam_roots()
    libs = steam_libraries(roots)
    given, how = None, None
    if getattr(args, "game_dir", None):
        given, how = args.game_dir, "--game-dir"
    elif os.environ.get("MIMESIS_DIR"):
        given, how = os.environ["MIMESIS_DIR"], "MIMESIS_DIR"
    if given is not None:
        path = normalize_game_arg(given)
        problem = game_problem(path)
        if problem:
            raise Fail("%s (%s): %s" % (path, how, problem))
        game = Game(path, how, libs)
    else:
        game = None
        # Like Directory.Build.props: the repo may be cloned into the game folder as <game>/Patches.
        repo = getattr(args, "repo_dir", None)
        if repo is not None and game_problem(repo.parent) is None:
            game = Game(repo.parent, "the folder this repo is in", libs)
        if game is None:
            found = steam_game_dirs(libs)
            valid = [g for g in found if game_problem(g[0]) is None]
            if not valid:
                problems = ["  %s: %s" % (g[0], game_problem(g[0])) for g in found]
                searched = ["  %s (%s)" % (lib, kind) for lib, _r, kind in libs] or ["  (no Steam install found)"]
                raise Fail("MIMESIS not found.\n" + ("\n".join(problems) + "\n" if problems else "")
                           + "Steam libraries searched:\n" + "\n".join(searched)
                           + "\nInstall MIMESIS with Steam (it runs with Proton), or pass --game-dir \"/path/to/MIMESIS\".")
            path, lib, kind, manifest = valid[0]
            game = Game(path, "%s library %s" % (kind, lib), libs, lib, manifest)
            if len(valid) > 1:
                warn("found %d copies of MIMESIS; using %s (pick another with --game-dir):\n%s"
                     % (len(valid), path, "\n".join("  " + str(g[0]) for g in valid)))
    game.prefix = find_prefix(game, getattr(args, "prefix", None))
    game.prefix_given = bool(getattr(args, "prefix", None))
    return game


def prefix_last_used(pfx):
    # Proton locks compatdata/<appid>/pfx.lock on every start, which updates its time (protontricks uses the same).
    for f in (pfx.parent / "pfx.lock", pfx / "user.reg"):
        try:
            return f.stat().st_mtime
        except OSError:
            pass
    return 0


def find_prefix(game, override=None):
    """The game's Proton prefix (or --prefix): <library>/steamapps/compatdata/2827200/pfx. Usually that's the library
    holding the game, but it can be another one (e.g. after the game was moved), so when there are several, the one
    Proton used last."""
    if override:
        p = Path(os.path.expanduser(override)).absolute()
        if not (p / "user.reg").is_file() and (p / "pfx").is_dir():
            p = p / "pfx"   # the compatdata/2827200 folder was given
        return p
    candidates = []
    if game.library is not None:
        candidates.append(game.library / "steamapps" / "compatdata" / APP_ID / "pfx")
    for lib, _root, _kind in game.libs:
        c = lib / "steamapps" / "compatdata" / APP_ID / "pfx"
        if c not in candidates:
            candidates.append(c)
    found = [c for c in candidates if (c / "user.reg").is_file()]
    if len(found) > 1:
        found.sort(key=prefix_last_used, reverse=True)   # stable: the game's library first on a tie
        warn("found %d Proton prefixes for MIMESIS; using the one Proton used last (pick another with --prefix):\n%s"
             % (len(found), "\n".join("  %s (last used %s)" % (c, fmt_time(prefix_last_used(c))) for c in found)))
    if found:
        return found[0]
    return candidates[0] if candidates else None


def prefix_elsewhere(game):
    """True when the prefix was found in another Steam library than the one holding the game."""
    if game.library is None or game.prefix is None or game.prefix_given:
        return False
    return not os.path.realpath(str(game.prefix)).startswith(
        os.path.realpath(str(game.library / "steamapps")) + os.sep)


def prefix_ready(prefix):
    return prefix is not None and (prefix / "user.reg").is_file()


# ---------------------------------------------------------------- is the game (or Wine) running?

SHELLS = {"sh", "bash", "zsh", "dash", "fish", "ksh", "mksh", "tcsh", "csh"}


def _procs():
    try:
        pids = [d for d in os.listdir("/proc") if d.isdigit() and int(d) != os.getpid()]
    except OSError:
        return
    for pid in pids:
        try:
            with open("/proc/%s/cmdline" % pid, "rb") as f:
                raw = f.read()
            with open("/proc/%s/comm" % pid, "rb") as f:
                comm = f.read().decode("utf-8", "replace").strip()
        except OSError:
            continue   # exited, or not ours to read
        yield int(pid), comm, [a.decode("utf-8", "replace") for a in raw.split(b"\0") if a]


def _base(arg):
    return re.split(r"[\\/]", arg)[-1].lower()


def game_processes():
    """MIMESIS processes: Wine names the process after the exe (comm/argv[0] "MIMESIS.exe"); Proton's launcher and
    Steam's reaper have the exe path / AppId on their command line while the game runs."""
    found = []
    for pid, comm, args in _procs():
        hit = comm.lower() == "mimesis.exe" or (args and _base(args[0]) == "mimesis.exe")
        if not hit and args and comm not in SHELLS:
            hit = ("SteamLaunch" in args and "AppId=" + APP_ID in args) or any(
                _base(a) == "mimesis.exe" and ("/" in a or "\\" in a) for a in args[1:])
        if hit:
            found.append((pid, " ".join(args) or comm))
    return found


def wineserver_pids(prefix):
    """wineserver processes serving this prefix. While one runs it owns the registry and rewrites user.reg when it
    exits, so editing the file then would be lost. Under Flatpak Steam, WINEPREFIX is the path inside its sandbox,
    which doesn't exist out here: then MIMESIS's compatdata/2827200/pfx or Steam app id identifies it."""
    if prefix is None:
        return []
    target = os.path.realpath(str(prefix))
    tail = "/steamapps/compatdata/%s/pfx" % APP_ID
    pids = []
    for pid, comm, _args in _procs():
        if not comm.startswith("wineserver"):
            continue
        try:
            with open("/proc/%d/environ" % pid, "rb") as f:
                env = dict(e.decode("utf-8", "replace").partition("=")[::2] for e in f.read().split(b"\0") if e)
        except OSError:
            continue
        wp = env.get("WINEPREFIX", "")
        if not wp:
            continue
        if os.path.realpath(wp) == target or (not os.path.isdir(wp) and (
                wp.rstrip("/").endswith(tail) or APP_ID in (env.get("STEAM_COMPAT_APP_ID"), env.get("SteamAppId")))):
            pids.append(pid)
    return pids


def guard_running(args):
    procs = game_processes()
    if not procs:
        return
    listing = "\n".join("  PID %d: %s" % (pid, cmd[:150]) for pid, cmd in procs)
    if not args.force:
        raise Fail("MIMESIS is running:\n%s\nQuit the game and run this again (its files are in use, and Wine rewrites "
                   "the prefix registry when the game exits). Pass --force to go ahead anyway." % listing, EXIT_RUNNING)
    warn("MIMESIS is running (--force given, going ahead):\n" + listing)


def guard_root(path):
    """Refuses to work as root (sudo) in a folder that belongs to someone else: what it creates there would be root's,
    so MelonLoader (running as that user under Proton) and later runs of this tool couldn't write it."""
    try:
        owner = os.stat(str(path)).st_uid
    except OSError:
        return
    if os.geteuid() != 0 or owner == 0:
        return
    try:
        name = pwd.getpwuid(owner).pw_name
    except KeyError:
        name = "uid %d" % owner
    raise Fail("Don't run this as root (sudo): %s belongs to %s, and what this creates there would belong to root, so "
               "MelonLoader (running as %s) couldn't write its logs and settings. Run it again as %s, without sudo."
               % (path, name, name, name), EXIT_USAGE)


# ---------------------------------------------------------------- reading Windows DLLs

def pe_file_version(path):
    """FileVersion (a, b, c, d) from a PE's VS_FIXEDFILEINFO, or None. Patch DLLs carry their version here
    (dotnet build: <Version>.0), MelonLoader's DLLs theirs."""
    try:
        data = Path(path).read_bytes()
    except OSError:
        return None
    i = data.rfind("VS_VERSION_INFO".encode("utf-16-le"))
    j = data.find(struct.pack("<I", 0xFEEF04BD), i, i + 96) if i >= 0 else -1
    if j < 0:
        return None
    ms, ls = struct.unpack_from("<II", data, j + 8)
    return (ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF)


def dotnet_mvid(path):
    """The module version id of a .NET assembly, or None. It identifies a build of the game's code, and Mono.Cecil
    keeps it when it rewrites an assembly (as the old patch installers did)."""
    try:
        data = Path(path).read_bytes()
        u16 = lambda o: struct.unpack_from("<H", data, o)[0]   # noqa: E731
        u32 = lambda o: struct.unpack_from("<I", data, o)[0]   # noqa: E731
        pe = u32(0x3C)
        if data[pe:pe + 4] != b"PE\0\0":
            return None
        nsec, optsize, opt = u16(pe + 6), u16(pe + 20), pe + 24
        dirs = opt + (96 if u16(opt) == 0x10B else 112)
        sections = []
        for k in range(nsec):
            vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, opt + optsize + 40 * k + 8)
            sections.append((va, max(vsize, rawsize), rawptr))

        def off(rva):
            for va, size, raw in sections:
                if va <= rva < va + size:
                    return rva - va + raw
            raise ValueError("rva")

        cli = off(u32(dirs + 14 * 8))
        md = off(u32(cli + 8))
        if u32(md) != 0x424A5342:
            return None
        p = md + 16 + u32(md + 12)
        count, p = u16(p + 2), p + 4
        streams = {}
        for _ in range(count):
            end = data.index(b"\0", p + 8)
            streams[data[p + 8:end].decode("ascii")] = md + u32(p)
            p += 8 + ((end - (p + 8) + 1 + 3) & ~3)
        tables = streams.get("#~", streams.get("#-"))
        heaps = data[tables + 6]
        rows = tables + 24 + 4 * bin(struct.unpack_from("<Q", data, tables + 8)[0]).count("1")
        if heaps & 0x40:
            rows += 4
        at = rows + 2 + (4 if heaps & 1 else 2)   # Module row: Generation, Name, Mvid
        idx = u32(at) if heaps & 2 else u16(at)
        if idx == 0:
            return None
        g = streams["#GUID"] + (idx - 1) * 16
        return data[g:g + 16].hex()
    except (OSError, ValueError, KeyError, IndexError, TypeError, struct.error, UnicodeDecodeError):
        return None


def has_old_hook(dll):
    """True if Assembly-CSharp.dll still has the startup hook of installers 1-3 (it references <Patch>Runtime)."""
    try:
        data = Path(dll).read_bytes()
    except OSError:
        return False
    return any(b"\0" + (p.name + "Runtime").encode() + b"\0" in data for p in PATCHES)


def is_melonloader_proxy(path):
    try:
        data = Path(path).read_bytes()
    except OSError:
        return False
    return b"MelonLoader" in data or "MelonLoader".encode("utf-16-le") in data


# ---------------------------------------------------------------- Wine registry (pfx/user.reg)
# Wine's text registry: "WINE REGISTRY Version 2", then blank-line separated sections "[Key\\Path] <time>", an
# optional "#time=<hex>" line and "name"=value lines. Key and value names are case-insensitive. Edits are minimal:
# every other byte of the file stays as it was.

_VALUE_RE = re.compile(r'^"((?:[^"\\]|\\.)*)"=(.*?)\r?\n?$')


def _reg_header(line):
    """The (still escaped) key of a "[Key\\\\Path] 123" section header line, or None."""
    if not line.startswith("["):
        return None
    i = 1
    while i < len(line):
        if line[i] == "\\":
            i += 2
            continue
        if line[i] == "]":
            return line[1:i]
        i += 1
    return None


def _reg_order(key):
    # Wine keeps subkeys sorted case-insensitively (upper-case compare) and saves them depth-first.
    return tuple(part.upper() for part in key.split("\\\\"))


def _reg_sections(lines, key):
    """(header index, end index) of every section of key; the end is the next header (or the end of the file)."""
    heads = [i for i, line in enumerate(lines) if _reg_header(line) is not None]
    out = []
    for k, h in enumerate(heads):
        if _reg_header(lines[h]).lower() == key.lower():
            out.append((h, heads[k + 1] if k + 1 < len(heads) else len(lines)))
    return out


def _reg_read(path):
    text = Path(path).read_bytes().decode("latin-1")   # byte-exact round trip
    if not text.startswith("WINE REGISTRY Version 2"):
        raise Fail("%s isn't a Wine registry file." % path)
    lines = text.splitlines(True)
    return lines, ("\r\n" if lines[0].endswith("\r\n") else "\n")


def _reg_value(line):
    m = _VALUE_RE.match(line)
    if not m or m.group(1).lower() != REG_NAME:
        return None
    raw = m.group(2)
    return raw[1:-1] if len(raw) >= 2 and raw.startswith('"') and raw.endswith('"') else raw


def reg_get_override(path):
    """The current "version" override for MIMESIS.exe in user.reg, or None."""
    lines, _nl = _reg_read(path)
    for s, e in _reg_sections(lines, REG_KEY):
        for i in range(s + 1, e):
            v = _reg_value(lines[i])
            if v is not None:
                return v
    return None


def _reg_save(path, lines):
    path = Path(path)
    backup = path.with_name("%s.mimesis-patches-%s.bak" % (path.name, time.strftime("%Y%m%d-%H%M%S")))
    n = 1
    while backup.exists():
        n += 1
        backup = path.with_name("%s.mimesis-patches-%s-%d.bak" % (path.name, time.strftime("%Y%m%d-%H%M%S"), n))
    shutil.copy2(str(path), str(backup))
    atomic_write(path, "".join(lines).encode("latin-1"), mode_from=path)
    return backup


def reg_set_override(path):
    """Sets "version"="native,builtin" under [Software\\Wine\\AppDefaults\\MIMESIS.exe\\DllOverrides].
    Returns (what happened, backup path or None, the value it replaced or None)."""
    lines, nl = _reg_read(path)
    sections = _reg_sections(lines, REG_KEY)
    changed = found = False
    old = None
    for s, e in sections:
        for i in range(s + 1, e):
            m = _VALUE_RE.match(lines[i])
            if m and m.group(1).lower() == REG_NAME:
                found = True
                ending = lines[i][len(lines[i].rstrip("\r\n")):]
                new = '"%s"="%s"%s' % (m.group(1), REG_VALUE, ending)
                if lines[i] != new:
                    old = _reg_value(lines[i])
                    lines[i], changed = new, True
    if found:
        what = "updated"
    elif sections:
        s, e = sections[0]
        j = e
        while j > s + 1 and lines[j - 1].strip() == "":
            j -= 1
        if not lines[j - 1].endswith("\n"):
            lines[j - 1] += nl
        lines.insert(j, '"%s"="%s"%s' % (REG_NAME, REG_VALUE, nl))
        changed, what = True, "added"
    else:
        # A new section where Wine itself would save it, so the file stays in Wine's order.
        now = time.time()
        filetime = int(now * 10 ** 7) + 116444736000000000
        block = ["[%s] %d%s" % (REG_KEY, int(now), nl), "#time=%x%s" % (filetime, nl),
                 '"%s"="%s"%s' % (REG_NAME, REG_VALUE, nl)]
        ours = _reg_order(REG_KEY)
        pos = next((i for i, line in enumerate(lines)
                    if _reg_header(line) is not None and _reg_order(_reg_header(line)) > ours), None)
        if pos is not None:
            if pos > 0 and lines[pos - 1].strip() != "":
                block.insert(0, nl)
            lines[pos:pos] = block + [nl]
        else:
            if not lines[-1].endswith("\n"):
                lines[-1] += nl
            if lines[-1].strip() != "":
                lines.append(nl)
            lines.extend(block)
        changed, what = True, "added"
    if not changed:
        return "unchanged", None, None
    return what, _reg_save(path, lines), old


def reg_remove_override(path):
    """Removes the "version" value this tool adds (only if it is still native-first, i.e. ours or the same thing),
    and its section if nothing else is left in it. Returns (what happened, backup path or None, other value)."""
    lines, _nl = _reg_read(path)
    other = None
    removed = False
    for s, e in reversed(_reg_sections(lines, REG_KEY)):
        for i in range(e - 1, s, -1):
            v = _reg_value(lines[i])
            if v is None:
                continue
            if v.replace(" ", "").lower() in ("native,builtin", "n,b"):
                del lines[i]
                e -= 1
                removed = True
            else:
                other = v
        body = [line for line in lines[s + 1:e] if line.strip() and not line.startswith("#")]
        if removed and not body:
            # Only "#time=" left: drop the whole section (and the blank line before it at the end of the file).
            start = s - 1 if e == len(lines) and s > 0 and lines[s - 1].strip() == "" else s
            del lines[start:e]
    if not removed:
        return ("different" if other is not None else "absent"), None, other
    return "removed", _reg_save(path, lines), other


def launch_options(game):
    """MIMESIS's Steam launch options from every Steam user's localconfig.vdf: [(steam root, options)]."""
    out = []
    roots = {root for _lib, root, _kind in game.libs}
    for root in sorted(roots):
        for cfg in sorted((root / "userdata").glob("*/config/localconfig.vdf")):
            opts = vdf_get(vdf_file(cfg), "userlocalconfigstore", "software", "valve", "steam", "apps", APP_ID,
                           "launchoptions")
            if isinstance(opts, str) and opts.strip():
                out.append((cfg, opts))
    return out


def launch_option_override(opts):
    m = re.search(r'WINEDLLOVERRIDES\s*=\s*"?([^"\s]*)', opts)
    if not m:
        return False
    for part in m.group(1).split(";"):
        names, _, mode = part.partition("=")
        if "version" in [x.strip().lower() for x in names.split(",")] and mode.strip().lower().startswith("n"):
            return True
    return False


# ---------------------------------------------------------------- downloads

def http_open(url, api=False, timeout=60):
    headers = {"User-Agent": USER_AGENT}
    if api:
        headers["Accept"] = "application/vnd.github+json"
        token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
        if token:   # only for api.github.com, never sent along to download hosts
            headers["Authorization"] = "Bearer " + token
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=timeout)


def net_fail(e, url):
    if isinstance(e, urllib.error.HTTPError):
        if e.code in (403, 429) and "api.github.com" in url:
            return Fail("GitHub's API rate limit was reached (HTTP %d). Wait a while and try again, or set GITHUB_TOKEN."
                        % e.code)
        return Fail("Download failed: HTTP %d %s (%s)" % (e.code, e.reason, url))
    reason = getattr(e, "reason", None) or e
    return Fail("Couldn't reach GitHub (%s). Check your internet connection and try again." % reason)


def download(url, dest, sha256=None):
    """Downloads url to dest (via dest.part). With sha256, a mismatch deletes the download and aborts."""
    dest = Path(dest)
    dest.parent.mkdir(parents=True, exist_ok=True)
    part = dest.with_name(dest.name + ".part")
    try:
        with http_open(url, timeout=120) as r, open(str(part), "wb") as f:
            total = int(r.headers.get("Content-Length") or 0)
            got, shown = 0, -1
            while True:
                chunk = r.read(1 << 16)
                if not chunk:
                    break
                f.write(chunk)
                got += len(chunk)
                if total and sys.stdout.isatty() and got * 10 // total != shown:
                    shown = got * 10 // total
                    print("\r   %.1f / %.1f MB" % (got / 1048576.0, total / 1048576.0), end="", flush=True)
            if total and sys.stdout.isatty():
                print()
    except (OSError, http.client.HTTPException) as e:
        try:
            part.unlink()
        except OSError:
            pass
        raise net_fail(e, url)
    if sha256 is not None:
        actual = sha256_file(part)
        if actual != sha256:
            part.unlink()
            raise Fail("Checksum mismatch for %s:\n  expected sha256 %s\n  got      sha256 %s\nThe download was deleted "
                       "and nothing was changed. Try again later." % (url, sha256, actual))
    os.replace(str(part), str(dest))
    return dest


class Release:
    def __init__(self, name, version, assets, published):
        self.name, self.version, self.assets, self.published = name, version, assets, published


def latest_releases(repo):
    """Latest release per name, from tags "Name-vX.Y.Z" (same logic as Engine.LatestReleases; drafts skipped)."""
    url = "https://api.github.com/repos/%s/releases?per_page=100" % repo
    try:
        with http_open(url, api=True) as r:
            data = json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 404:
            raise Fail("GitHub repo %s wasn't found (check --repo OWNER/NAME)." % repo)
        raise net_fail(e, url)
    except (OSError, http.client.HTTPException) as e:
        raise net_fail(e, url)
    except ValueError:
        raise Fail("GitHub returned an unexpected answer for %s." % url)
    if not isinstance(data, list):
        raise Fail("GitHub returned an unexpected answer for %s." % url)
    result = {}
    for rel in data:
        if rel.get("draft"):
            continue
        tag = rel.get("tag_name") or ""
        dash = tag.rfind("-v")
        v = parse_version(tag[dash + 2:]) if dash > 0 else None
        if v is None:
            continue
        name = tag[:dash]
        if name in result and result[name].version >= v:
            continue
        assets = {a.get("name"): (a.get("browser_download_url"), a.get("digest")) for a in rel.get("assets") or []}
        published = None
        try:
            published = datetime.datetime.strptime(rel.get("published_at") or "", "%Y-%m-%dT%H:%M:%SZ") \
                .replace(tzinfo=datetime.timezone.utc)
        except ValueError:
            pass
        result[name] = Release(name, v, assets, published)
    return result


def release_file(repo, rel, filename):
    """A release asset, downloaded once into the cache (per repo and version) and checked against GitHub's digest."""
    if filename not in rel.assets or not rel.assets[filename][0]:
        raise Fail("%s v%s release has no %s." % (rel.name, vstr(rel.version), filename))
    url, digest = rel.assets[filename]
    sha = digest[7:].lower() if isinstance(digest, str) and digest.startswith("sha256:") else None
    dest = cache_dir() / "github" / repo.replace("/", "__") / ("%s-v%s" % (rel.name, vstr(rel.version))) / filename
    if dest.is_file():
        if sha is None or sha256_file(dest) == sha:
            return dest
        warn("cached %s is damaged; downloading it again" % dest)
        dest.unlink()
    say("   downloading %s..." % filename)
    download(url, dest, sha)
    with open(str(dest), "rb") as f:
        if f.read(2) != b"MZ":
            dest.unlink()
            raise Fail("%s from %s isn't a Windows DLL." % (filename, url))
    return dest


# ---------------------------------------------------------------- MelonLoader

def melonloader_installed(game):
    return (game.path / "version.dll").is_file() and (game.path / "MelonLoader").is_dir()


def melonloader_version(game):
    for p in (game.path / "MelonLoader" / "net35" / "MelonLoader.dll", game.path / "MelonLoader" / "net6" / "MelonLoader.dll",
              game.path / "version.dll"):
        v = pe_file_version(p)
        if v:
            return vstr(v)
    return None


def melonloader_zip(local=None):
    """The verified MelonLoader package: --melonloader-zip, the cached download, or a fresh download."""
    if local:
        p = Path(os.path.expanduser(local)).absolute()
        if not p.is_file():
            raise Fail("%s doesn't exist." % p)
        actual = sha256_file(p)
        if actual != MELONLOADER_SHA256:
            raise Fail("%s isn't the official MelonLoader %s x64 package:\n  expected sha256 %s\n  got      sha256 %s\n"
                       "Nothing was changed." % (p, MELONLOADER_VERSION, MELONLOADER_SHA256, actual))
        return p
    cached = cache_dir() / ("MelonLoader-%s.x64.zip" % MELONLOADER_VERSION)
    if cached.is_file():
        if sha256_file(cached) == MELONLOADER_SHA256:
            return cached
        warn("cached %s is damaged; downloading it again" % cached)
        cached.unlink()
    say("   downloading %s" % MELONLOADER_URL)
    return download(MELONLOADER_URL, cached, MELONLOADER_SHA256)


def safe_extract(zip_path, dest):
    """Extracts a zip into dest, refusing absolute paths, '..' and links. Returns the number of files written."""
    dest = os.path.abspath(str(dest))
    with zipfile.ZipFile(str(zip_path)) as z:
        members = []
        for m in z.infolist():
            name = m.filename.replace("\\", "/")
            if name.startswith("/") or re.match(r"^[A-Za-z]:", name) or ".." in name.split("/"):
                raise Fail("Refusing to extract %s: unsafe path %r" % (zip_path, m.filename))
            if stat.S_ISLNK(m.external_attr >> 16):
                raise Fail("Refusing to extract %s: %r is a link" % (zip_path, m.filename))
            target = os.path.normpath(os.path.join(dest, name))
            if os.path.commonpath([dest, target]) != dest:
                raise Fail("Refusing to extract %s: %r leaves the game folder" % (zip_path, m.filename))
            members.append((m, name, target))
        count = 0
        for m, name, target in members:
            if name.endswith("/"):
                os.makedirs(target, exist_ok=True)
                continue
            os.makedirs(os.path.dirname(target), exist_ok=True)
            tmp = target + ".mimesis-tmp"
            with z.open(m) as src, open(tmp, "wb") as out:
                shutil.copyfileobj(src, out)
            os.replace(tmp, target)
            count += 1
    return count


# ---------------------------------------------------------------- older installs (Engine.Migrate)

class OldInstalls:
    """What the older installers left: installer 4 (BepInEx plugins) and installers 1-3 (startup hook in
    Assembly-CSharp.dll, DLLs in Managed)."""

    def __init__(self, game):
        self.game = game
        self.bep = game.path / "BepInEx" / "plugins" / "MimesisPatches"
        self.dll = game.managed / "Assembly-CSharp.dll"
        self.bak = Path(str(self.dll) + ".bak")
        self.bep_patches = [p.name for p in PATCHES if (self.bep / p.dll).is_file()] if self.bep.is_dir() else []
        self.managed_patches = [p.name for p in PATCHES if (game.managed / p.dll).is_file()]

    @property
    def any(self):
        return self.bep.is_dir() or self.bak.is_file() or bool(self.managed_patches)

    @property
    def patches(self):
        return list(dict.fromkeys(self.bep_patches + self.managed_patches))

    def bak_is_stale(self):
        """True when the .bak is from another game build than Assembly-CSharp.dll (Steam updated the game since the
        old install), so restoring it would put old game code back."""
        a, b = dotnet_mvid(self.dll), dotnet_mvid(self.bak)
        return a is not None and b is not None and a != b


LATER = []   # warnings repeated at the end, where they can't be missed


def print_later():
    for msg in LATER:
        warn(msg)


def migrate(game):
    """Engine.Migrate: cleans up older installs and returns the patches that were installed that way."""
    old = OldInstalls(game)
    found = []
    if old.bep.is_dir():
        say("== Moving patches from BepInEx to MelonLoader")
        found += old.bep_patches
        shutil.rmtree(str(old.bep))
        plugins = old.bep.parent
        if not any(plugins.iterdir()):
            for f in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"):
                if (game.path / f).is_file():
                    (game.path / f).unlink()
            shutil.rmtree(str(game.path / "BepInEx"))
            say("   BepInEx removed (nothing else used it)")
        else:
            say("   BepInEx kept: other BepInEx mods are installed. Running two mod loaders may cause problems.")
        say()
    if old.bak.is_file() or old.managed_patches:
        say("== Cleaning up the original patch install")
        if old.bak.is_file():
            if old.bak_is_stale():
                # Not in the Windows installer: after a game update the .bak is older game code, so it must not
                # be restored (and builds would compile against it). Steam's copy is the right one.
                old.bak.unlink()
                say("   Assembly-CSharp.dll.bak was from an older game version: removed, not restored")
                if has_old_hook(old.dll):
                    LATER.append("Assembly-CSharp.dll still has the old patch hook, so MIMESIS may not start. In Steam: "
                                 "MIMESIS > Properties > Installed Files > Verify integrity of game files, to get a "
                                 "clean copy.")
                    warn(LATER[-1])
            else:
                shutil.copy2(str(old.bak), str(old.dll))
                old.bak.unlink()
                say("   original Assembly-CSharp.dll restored")
        for name in old.managed_patches:
            (game.managed / BY_NAME[name].dll).unlink()
        harmony = game.managed / "0Harmony.dll"   # the game doesn't ship Harmony; it came with the patches
        if harmony.is_file():
            harmony.unlink()
        icons = game.managed.parent / "PSIcons"
        if icons.is_dir():
            shutil.rmtree(str(icons))
        say()
        found += old.managed_patches
    return list(dict.fromkeys(found))


# ---------------------------------------------------------------- building from this checkout

def is_repo(p):
    return p is not None and (p / "Directory.Build.props").is_file() and (p / "Common" / "MelonEntry.cs").is_file()


def find_dotnet():
    """(dotnet path, newest SDK version) or (None, reason). Also finds a user install from dotnet-install.sh, also
    when the dotnet on PATH is only a runtime or an older SDK."""
    candidates = [shutil.which("dotnet")]
    if os.environ.get("DOTNET_ROOT"):
        candidates.append(os.path.join(os.environ["DOTNET_ROOT"], "dotnet"))
    candidates.append(str(home() / ".dotnet" / "dotnet"))
    first_reason = None
    for c in candidates:
        if not c or not os.path.isfile(c) or not os.access(c, os.X_OK):
            continue
        try:
            out = subprocess.run([c, "--list-sdks"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                 universal_newlines=True, timeout=60).stdout
        except (OSError, subprocess.SubprocessError):
            continue
        sdks = [parse_version(line.split()[0].split("-")[0]) for line in out.splitlines() if line.strip()]
        sdks = [v for v in sdks if v]
        if sdks and max(sdks)[0] >= 8:
            return c, vstr(max(sdks))
        if first_reason is None:
            first_reason = ("found dotnet at %s, but no .NET SDK 8 or newer (SDKs: %s)"
                            % (c, ", ".join(vstr(v) for v in sdks) or "none"))
    return None, first_reason or "the dotnet command wasn't found"


DOTNET_HELP = """Building the patches needs the .NET SDK 8 or newer (the runtime alone isn't enough):
  Fedora:          sudo dnf install dotnet-sdk-8.0
  Ubuntu:          sudo apt install dotnet-sdk-8.0
  Arch:            sudo pacman -S dotnet-sdk
  Any distro, also Steam Deck / SteamOS, Debian, openSUSE, Bazzite and other Fedora Atomic / immutable systems
  (no root needed, installs into ~/.dotnet, which this tool finds):
                   curl -sSLo /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
                   bash /tmp/dotnet-install.sh --channel 8.0
  More: https://learn.microsoft.com/dotnet/core/install/linux
The first build downloads its packages from nuget.org.
Or install the released DLLs from GitHub instead (built for an older game version): install --source github"""


def msbuild_escape(text):
    # ';' and ',' separate properties on the command line; %XX is MSBuild's escape.
    return text.replace("%", "%25").replace(";", "%3B").replace(",", "%2C")


def msbuild_game_dir(game):
    """The game folder for MSBuild. MSBuild unescapes %XX more than once on the way to its tasks, so a '%' in the path
    can't be escaped (a library named "x%20y" would become "x y"): such a game is built through a symlink to it."""
    path = str(game.path)
    if "%" not in path:
        return Path(path)
    link = cache_dir() / ("game-" + hashlib.sha256(path.encode("utf-8", "surrogateescape")).hexdigest()[:12])
    if "%" in str(link):
        raise Fail("The game folder's path has a '%%' in it (%s), which MSBuild can't handle, and so does the cache "
                   "folder. Make a symlink to the game folder without a '%%' and pass it with --game-dir." % path)
    if not (link.is_symlink() and os.readlink(str(link)) == path):
        link.parent.mkdir(parents=True, exist_ok=True)
        tmp = link.with_name("%s.%d.tmp" % (link.name, os.getpid()))
        os.symlink(path, str(tmp))
        os.replace(str(tmp), str(link))
    return link


def build_properties(game):
    gamedir = msbuild_game_dir(game)
    props = ["-p:GameDir=" + msbuild_escape(str(gamedir).rstrip("/") + "/")]
    old = OldInstalls(game)
    # Directory.Build.props prefers Assembly-CSharp.dll.bak (the original under an old hooked install); after a game
    # update that's older game code, so build against the current DLL then.
    if old.bak.is_file() and old.bak_is_stale():
        props.append("-p:GameDll=" + msbuild_escape(str(gamedir / old.dll.relative_to(game.path))))
    return props


def release_outputs(repo, name):
    # bin/Release/<target framework>/: net472 for most patches, netstandard2.1 for PSController
    return [p for p in (repo / name / "Runtime" / "bin" / "Release").glob("*/" + BY_NAME[name].dll) if p.is_file()]


def built_dll(repo, name):
    """The newest local build of a patch: <repo>/dist or the project's bin/Release output."""
    dist = repo / "dist" / BY_NAME[name].dll
    found = ([dist] if dist.is_file() else []) + release_outputs(repo, name)
    return max(found, key=lambda p: p.stat().st_mtime) if found else None


def stale_reason(repo, name, dll, game):
    """Why a local build is out of date (sources or the game changed since), or None."""
    built = dll.stat().st_mtime
    sources = [repo / "Directory.Build.props", repo / "Directory.Build.targets"] + list((repo / "Common").glob("*.cs"))
    for root, dirs, files in os.walk(str(repo / name / "Runtime")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        sources += [Path(root) / f for f in files if f.endswith((".cs", ".csproj"))]
    if any(s.is_file() and s.stat().st_mtime > built + 1 for s in sources):
        return "sources changed since it was built"
    gamedll = game.managed / "Assembly-CSharp.dll"
    if gamedll.is_file() and gamedll.stat().st_mtime > built + 1:
        return "the game was updated since it was built"
    return None


def build_patch(repo, game, name, dotnet, verbose=False):
    """dotnet build of one patch; the DLL is copied to <repo>/dist. Returns the dist path or None."""
    proj = repo / name / "Runtime"
    dll = BY_NAME[name].dll
    dist = repo / "dist"
    logs = dist / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    log = logs / (name + ".log")
    cmd = [dotnet, "build", "-c", "Release", "-nologo"] + build_properties(game)
    env = dict(os.environ)
    env.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    env.setdefault("DOTNET_NOLOGO", "1")
    env.setdefault("DOTNET_ROOT", os.path.dirname(os.path.realpath(dotnet)))
    start = time.time()
    lines = []
    try:
        with open(str(log), "w", encoding="utf-8") as f:
            f.write("$ cd %s && %s\n" % (q(proj), " ".join(q(c) for c in cmd)))
            p = subprocess.Popen(cmd, cwd=str(proj), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 stdin=subprocess.DEVNULL, env=env, universal_newlines=True, errors="replace")
            for line in p.stdout:
                f.write(line)
                lines.append(line.rstrip("\n"))
                if verbose:
                    say("      " + line.rstrip("\n"))
            rc = p.wait()
    except OSError as e:
        error("%s: couldn't run dotnet (%s)" % (name, e))
        return None
    # MSBuild reports "<Patch>Runtime -> <output path>"
    outs = [Path(m.group(1).strip()) for m in (re.match(r"^\s*\S+ -> (.+)$", line) for line in lines) if m]
    outs = [o for o in outs if o.name == dll and o.is_file()] or release_outputs(repo, name)
    out = max(outs, key=lambda p: p.stat().st_mtime) if outs else None
    if rc == 0 and out is not None:
        atomic_copy(out, dist / dll)
        good("   %-12s built %s (%.0fs)" % (name, vstr(pe_file_version(dist / dll)) or "", time.time() - start))
        return dist / dll
    errors = list(dict.fromkeys(line.strip() for line in lines if re.search(r": error [A-Z]+\d*:", line)))
    error("%s: build failed (exit code %d)" % (name, rc))
    for line in (errors[:12] if errors else [line for line in lines if line.strip()][-15:]):
        say("      " + line)
    say("      full log: %s" % log)
    return None


# ---------------------------------------------------------------- patch sources (local builds or GitHub releases)

def pick_patches(names):
    """Patch names from the command line (any case, also "A,B" and "<Name>Runtime.dll"); none or "all" = all."""
    wanted = [n for arg in (names or []) for n in arg.split(",") if n.strip()]
    if not wanted or [n.lower() for n in wanted] == ["all"]:
        return [p.name for p in PATCHES]
    lower = {p.name.lower(): p.name for p in PATCHES}
    out = []
    for n in wanted:
        key = re.sub(r"(runtime)?(\.dll)?$", "", n.strip(), flags=re.I).lower()
        if key not in lower:
            raise Fail("Unknown patch: %s\nPatches: %s (or all)" % (n, ", ".join(p.name for p in PATCHES)), EXIT_USAGE)
        if lower[key] not in out:
            out.append(lower[key])
    return out


def choose_source(args):
    if args.source == "local" and not is_repo(args.repo_dir):
        raise Fail("--source local builds the patches from a checkout of the repo, and this script isn't inside one. "
                   "Pass --repo-dir /path/to/mimesis-patches, or use --source github.", EXIT_USAGE)
    if args.source:
        return args.source
    if not is_repo(args.repo_dir):
        return "github"
    if any(built_dll(args.repo_dir, p.name) for p in PATCHES):
        return "local"
    dotnet, why = find_dotnet()
    if dotnet:
        return "local"
    # Not a silent switch to GitHub: its releases may be built for an older game version than this checkout.
    raise Fail("The patches in this checkout aren't built yet and can't be built here: %s.\n\n%s" % (why, DOTNET_HELP))


def prepare_local(args, game, names):
    """{name: (dll, version)} from local builds, building what's missing or out of date. Returns (ready, failed)."""
    repo = args.repo_dir
    say("== Local builds from %s" % repo)
    ready, failed = {}, []
    dotnet = reason_no_sdk = None
    checked = False
    for name in names:
        dll = built_dll(repo, name)
        why = "not built yet" if dll is None else stale_reason(repo, name, dll, game)
        if why and not args.no_build:
            if not checked:
                dotnet, reason_no_sdk = find_dotnet()
                checked = True
            if dotnet:
                say("   %-12s %s, building..." % (name, why))
                built = build_patch(repo, game, name, dotnet, args.verbose)
                if built is None:
                    failed.append(name)
                    continue
                dll = built
            elif dll is None:
                error("%s isn't built and can't be built here: %s." % (name, reason_no_sdk))
                failed.append(name)
                continue
            else:
                warn("%s: %s, but it can't be rebuilt here (%s); installing the existing build" % (name, why, reason_no_sdk))
        elif dll is None:
            error("%s isn't built (--no-build given). Run: %s build %s" % (name, q(SCRIPT), name))
            failed.append(name)
            continue
        ready[name] = (dll, vstr(pe_file_version(dll)))
        if not why or args.no_build or not dotnet:
            say("   %-12s %s  %s" % (name, ready[name][1] or "?", dll))
    if checked and not dotnet and failed:
        say()
        say(DOTNET_HELP)
    say()
    return ready, failed


def prepare_github(args, game, names):
    repo = args.repo
    say("== Checking GitHub for the latest patches (%s)..." % repo)
    releases = latest_releases(repo)
    ready, failed, skipped, published = {}, [], [], []
    for name in names:
        rel = releases.get(name)
        if rel is None:
            say("   %-12s not released yet, skipped" % name)
            skipped.append(name)
            continue
        try:
            dll = release_file(repo, rel, BY_NAME[name].dll)
        except Fail as e:
            error("%s: %s" % (name, e))
            failed.append(name)
            continue
        ready[name] = (dll, vstr(rel.version))
        if rel.published:
            published.append(rel.published)
        say("   %-12s %s  (released %s)" % (name, vstr(rel.version),
                                            rel.published.astimezone().strftime("%Y-%m-%d") if rel.published else "?"))
    say()
    msg = ("GitHub releases are built for the game version that was current when they were published (upstream's "
           "last ones target EA 0.3.1). If MIMESIS was updated since, some patches may not load or may misbehave. "
           "Building them from a checkout of this repo (--source local) compiles them against your game.")
    updated = str(vdf_get(game.manifest, "lastupdated") or "")
    if published and updated.isdigit():
        game_time = datetime.datetime.fromtimestamp(int(updated)).astimezone()
        if game_time > max(published):
            msg += "\nYour game was updated on %s, after the newest of these releases (%s)." % (
                game_time.strftime("%Y-%m-%d"), max(published).astimezone().strftime("%Y-%m-%d"))
    warn(msg)
    say()
    return ready, failed, skipped


def prepare(args, game, names, source):
    if not names:
        return {}, [], []
    if source == "local":
        ready, failed = prepare_local(args, game, names)
        return ready, failed, []
    return prepare_github(args, game, names)


# ---------------------------------------------------------------- the marker file read by the in-game window

def marker_path(game):
    return game.path / "UserData" / MARKER_NAME


def read_marker(game):
    try:
        data = json.loads(marker_path(game).read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except (OSError, ValueError):
        return None


def installed_patches(game):
    """{name: version or None} for the patch DLLs in <game>/Mods."""
    out = {}
    for p in PATCHES:
        dll = game.mods / p.dll
        if dll.is_file():
            out[p.name] = vstr(pe_file_version(dll))
    return out


def write_marker(game, source, repo, repo_dir):
    patches = installed_patches(game)
    path = marker_path(game)
    if not patches:
        if path.is_file():
            path.unlink()
        return
    data = {
        "script": str(SCRIPT),
        "repo_dir": str(repo_dir) if is_repo(repo_dir) else None,
        "source": source,
        "repo": repo,
        "patches": patches,
        "updated": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    atomic_write(path, (json.dumps(data, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


# ---------------------------------------------------------------- install steps

def remove_patch_dll(game, name):
    """Removes <game>/Mods/<Name>Runtime.dll, also in another letter case (Wine would load both)."""
    removed = False
    if game.mods.is_dir():
        for f in game.mods.iterdir():
            if f.name.lower() == BY_NAME[name].dll.lower() and f.is_file():
                f.unlink()
                removed = True
    return removed


def remove_retired(game):
    """Deletes the DLLs of retired patches (RETIRED) from <game>/Mods, in any letter case. Returns their names."""
    removed = []
    if game.mods.is_dir():
        for name in RETIRED:
            for f in game.mods.iterdir():
                if f.name.lower() == (name + "Runtime.dll").lower() and f.is_file():
                    f.unlink()
                    if name not in removed:
                        removed.append(name)
    for name in removed:
        say("== %s: removed (retired, replaced by %s)" % (name, RETIRED[name]))
    if removed:
        say()
    return removed


def ensure_override(game):
    """Sets the DLL override in the prefix registry. Returns True when it (or Steam's launch option) is in place;
    otherwise says why at the end too (LATER)."""
    say("== Wine DLL override (so Proton loads MelonLoader's version.dll)")
    reg = game.prefix / "user.reg" if game.prefix is not None else None
    if prefix_ready(game.prefix) and prefix_elsewhere(game):
        say("   the prefix is in another Steam library than the game (pick another with --prefix)")
    current = reg_get_override(reg) if prefix_ready(game.prefix) else None
    if current is not None and current.lower() == REG_VALUE:
        say("   already set in %s" % reg)
        say()
        return True
    problem = None
    if not prefix_ready(game.prefix):
        problem = ("the game's Proton prefix doesn't exist yet%s, so the DLL override can't be set. Start MIMESIS once "
                   "from Steam, quit it, then run this install again." % (" (%s)" % game.prefix if game.prefix else ""))
    else:
        pids = wineserver_pids(game.prefix)
        if pids and current is not None and current.replace(" ", "").lower().startswith("n"):
            say('   "%s"="%s" (native first, which works) in %s; left as is while Wine is running in the prefix'
                % (REG_NAME, current, reg))
            say()
            return True
        if pids:
            problem = ("Wine is still running in the game's prefix (wineserver PID %s) and would undo the change when "
                       "it exits, so the DLL override wasn't set. Wait until the game has fully closed and run this "
                       "install again." % ", ".join(map(str, pids)))
    if problem is not None:
        if any(launch_option_override(opts) for _cfg, opts in launch_options(game)):
            say("   not set in the prefix (%s), but MIMESIS's Steam launch options set it"
                % ("it doesn't exist yet" if not prefix_ready(game.prefix) else "Wine is running in it"))
            say()
            return True
        LATER.append(problem + " Or set it in Steam > MIMESIS > Properties > Launch Options: " + LAUNCH_OPTION)
        warn(LATER[-1])
        say()
        return False
    what, backup, old = reg_set_override(reg)
    if what == "unchanged":
        say("   already set in %s" % reg)
    else:
        good('   %s "%s"="%s" in %s' % (what, REG_NAME, REG_VALUE, reg))
        if old is not None:
            say('   it was "%s" (uninstall --melonloader removes the override rather than restoring that; the backup '
                'below has it)' % old)
        say("   backup: %s" % backup)
    say()
    return True


def install_ready(game, ready, ml_zip):
    """Puts MelonLoader and the prepared patch DLLs in place. Returns (the installed names, whether the DLL override
    is in place)."""
    if ml_zip is not None:
        say("== MelonLoader %s (mod loader, installed once)" % MELONLOADER_VERSION)
        good("   installed (%d files, sha256 verified)" % safe_extract(ml_zip, game.path))
        say()
    else:
        say("== MelonLoader %s already installed" % (melonloader_version(game) or ""))
        say()
    override = ensure_override(game)
    game.mods.mkdir(exist_ok=True)
    done = []
    for name, (dll, version) in ready.items():
        say("== %s v%s" % (name, version or "?"))
        remove_patch_dll(game, name)
        atomic_copy(dll, game.mods / BY_NAME[name].dll)
        good("   installed")
        done.append(name)
    if done:
        say()
    return done, override


def need_melonloader(game, args):
    return not melonloader_installed(game) or getattr(args, "reinstall_melonloader", False)


def print_header():
    say("MIMESIS Patches for Linux (Proton) v%s" % TOOL_VERSION)
    say("by Mohamed Darwesh (@medovanx) - github.com/medovanx/mimesis-patches")
    say()


def print_override_tip():
    say("If MelonLoader doesn't start with the game (`status` shows when it last started), set the override in Steam "
        "instead:")
    say("  Steam > MIMESIS > Properties > Launch Options:  %s" % LAUNCH_OPTION)


# ---------------------------------------------------------------- commands

def cmd_list(args):
    repo = args.repo_dir if is_repo(args.repo_dir) else None
    say("%-15s %-9s %-9s %s" % ("Patch", "Version", "Needs it", "What it does"))
    for p in PATCHES:
        version = "-"
        if repo is not None:
            try:
                m = re.search(r"<Version>([^<]+)</Version>",
                              next((repo / p.name / "Runtime").glob("*.csproj")).read_text(encoding="utf-8"))
                version = m.group(1) if m else "-"
            except (StopIteration, OSError):
                pass
        say("%-15s %-9s %-9s %s" % (p.name, version, p.who, p.about))
    say()
    say("Needs it: Host = only the host needs it; Everyone = host and every player; Anyone = personal feature.")
    if repo is not None:
        say("Version: the version in this checkout (%s)." % repo)
    return 0


def cmd_status(args):
    say("MIMESIS Patches for Linux (Proton) v%s" % TOOL_VERSION)
    say()
    try:
        game = resolve_game(args)
    except Fail as e:
        error(str(e))
        return EXIT_FAILED
    rows = [("Game", "%s\n%15s(found via %s)" % (game.path, "", game.how))]
    if game.manifest:
        updated = str(vdf_get(game.manifest, "lastupdated") or "")
        rows.append(("Steam build", "%s%s" % (vdf_get(game.manifest, "buildid") or "?",
                                              ", updated %s" % fmt_time(int(updated)) if updated.isdigit() else "")))
    if prefix_ready(game.prefix):
        pids = wineserver_pids(game.prefix)
        rows.append(("Proton prefix", str(game.prefix) + (paint("33", " (Wine is running in it: wineserver PID %s)"
                                                                % ", ".join(map(str, pids))) if pids else "")
                     + (" (in another Steam library than the game)" if prefix_elsewhere(game) else "")))
    else:
        rows.append(("Proton prefix", paint("33", "not created yet") + " - start MIMESIS once from Steam"
                     + (" (expected at %s)" % game.prefix if game.prefix else "")))
    if melonloader_installed(game):
        log = game.path / "MelonLoader" / "Latest.log"
        ran = ("last started %s" % fmt_time(log.stat().st_mtime)) if log.is_file() else "hasn't run yet"
        rows.append(("MelonLoader", "%s installed (%s)" % (melonloader_version(game) or "?", ran)))
    else:
        rows.append(("MelonLoader", paint("33", "not installed")))
    override = []
    if prefix_ready(game.prefix):
        try:
            v = reg_get_override(game.prefix / "user.reg")
        except (Fail, OSError) as e:
            v = None
            override.append(paint("33", "couldn't read user.reg (%s)" % e))
        if v is not None:
            native = v.replace(" ", "").lower().startswith("n")
            override.append(('version="%s" in the prefix registry' % v) if native
                            else paint("33", 'version="%s" in the prefix registry (not native-first!)' % v))
    for cfg, opts in launch_options(game):
        if launch_option_override(opts):
            override.append("Steam launch options: %s" % opts)
    if not override:
        override.append(paint("33", "not set") + " - run install, or set the Steam launch option " + LAUNCH_OPTION)
    rows.append(("DLL override", ("\n%15s" % "").join(override)))
    for label, value in rows:
        say("%-14s %s" % (label + ":", value))
    say()

    installed = installed_patches(game)
    repo = args.repo_dir if is_repo(args.repo_dir) else None
    old = OldInstalls(game)
    say("Patches in %s:" % game.mods)
    for p in PATCHES:
        if p.name in installed:
            extra = ""
            if repo is not None:
                dll = built_dll(repo, p.name)
                local = vstr(pe_file_version(dll)) if dll else None
                if local and installed[p.name] and parse_version(local) > parse_version(installed[p.name]):
                    extra = paint("33", "  (local build %s is newer)" % local)
            say("  %-15s %s%s" % (p.name, paint("32", installed[p.name] or "installed"), extra))
        else:
            where = " (old %s install: moved to MelonLoader by the next install)" % (
                "BepInEx" if p.name in old.bep_patches else "Managed") if p.name in old.patches else ""
            say("  %-15s -%s" % (p.name, where))
    others = sorted(f.name for f in game.mods.glob("*.dll")
                    if f.name.lower() not in {p.dll.lower() for p in PATCHES}) if game.mods.is_dir() else []
    if others:
        say("  other mods:   %s" % ", ".join(others))
    if old.bak.is_file():
        say("  Assembly-CSharp.dll.bak from an old install is there (cleaned up by the next install)")
    marker = read_marker(game)
    if marker:
        say("Last install: %s from %s%s" % (marker.get("updated", "?"), marker.get("source", "?"),
                                            " (%s)" % marker.get("repo") if marker.get("source") == "github" else ""))
    say()

    procs = game_processes()
    say("MIMESIS running: %s" % (paint("33", "yes (PID %s)" % ", ".join(str(pid) for pid, _ in procs)) if procs else "no"))
    if repo is not None:
        dotnet, why = find_dotnet()
        say("Repo checkout:  %s (%s)" % (repo, ".NET SDK %s" % why if dotnet else "can't build: " + why))
    else:
        say("Repo checkout:  none (install --source github, or pass --repo-dir)")
    return 0


def cmd_build(args):
    print_header()
    if not is_repo(args.repo_dir):
        raise Fail("build needs a checkout of the repo (this script isn't inside one). Pass --repo-dir.", EXIT_USAGE)
    guard_root(args.repo_dir)
    names = pick_patches(args.patches)
    game = resolve_game(args)
    dotnet, why = find_dotnet()
    if not dotnet:
        raise Fail("Can't build: %s.\n\n%s" % (why, DOTNET_HELP))
    say("Building %d patch%s against %s (.NET SDK %s)" % (len(names), "es" if len(names) > 1 else "", game.path, why))
    if len(build_properties(game)) > 1:
        warn("Assembly-CSharp.dll.bak is from an older game version; building against the current Assembly-CSharp.dll")
    say()
    built, failed = [], []
    for i, name in enumerate(names, 1):
        say("[%d/%d] %s" % (i, len(names), name))
        (built if build_patch(args.repo_dir, game, name, dotnet, args.verbose) else failed).append(name)
    say()
    if built:
        good("Built %d of %d into %s: %s" % (len(built), len(names), args.repo_dir / "dist", ", ".join(built)))
    if failed:
        error("%d failed: %s (logs in %s)" % (len(failed), ", ".join(failed), args.repo_dir / "dist" / "logs"))
        return EXIT_FAILED
    say("Install them with: %s install%s" % (q(SCRIPT), "" if len(names) == len(PATCHES) else " " + " ".join(names)))
    return 0


def cmd_install(args):
    print_header()
    game = resolve_game(args)
    say("Game: %s" % game.path)
    guard_root(game.path)
    guard_running(args)
    selected = pick_patches(args.patches)
    # Patches from an older install move to MelonLoader too, not just the selected ones.
    old = OldInstalls(game)
    names = selected + [n for n in old.patches if n not in selected]
    source = choose_source(args)
    say("Source: %s" % ("local builds from %s" % args.repo_dir if source == "local" else "GitHub releases of " + args.repo))
    say()
    ready, failed, skipped = prepare(args, game, names, source)
    if not ready:
        raise Fail("Nothing to install%s. The game folder was not changed." % (
            " (failed: %s)" % ", ".join(failed) if failed else ""))
    # Everything is downloaded/built and MelonLoader verified before the game folder is touched.
    ml_zip = None
    if need_melonloader(game, args):
        say("== Getting MelonLoader %s" % MELONLOADER_VERSION)
        ml_zip = melonloader_zip(args.melonloader_zip)
        say("   sha256 verified")
        say()
    migrate(game)
    remove_retired(game)
    done, override = install_ready(game, ready, ml_zip)
    write_marker(game, source, args.repo, args.repo_dir)
    if done:
        good("Installed: %s" % ", ".join(done))
    if skipped:
        say("Not released yet: %s" % ", ".join(skipped))
    if override:
        say("Start MIMESIS from Steam as usual. The first start with MelonLoader takes a little longer.")
        print_override_tip()
    print_later()
    if failed:
        error("not installed: %s" % ", ".join(failed))
    if not override:
        error("the DLL override isn't set, so MelonLoader won't start with the game yet (see the warning above).")
    return EXIT_FAILED if failed or not override else 0


def cmd_uninstall(args):
    print_header()
    game = resolve_game(args)
    say("Game: %s" % game.path)
    say()
    guard_root(game.path)
    guard_running(args)
    removing = pick_patches(args.patches)
    old = OldInstalls(game)
    # Patches from an older install that stay are reinstalled as mods (Engine.Uninstall), unless MelonLoader goes too.
    kept = [] if args.melonloader else [n for n in old.patches if n not in removing]
    ready, failed = {}, []
    ml_zip = None
    if kept:
        source = choose_source(args)
        say("Patches from an older install that you're keeping are moved to MelonLoader: %s" % ", ".join(kept))
        ready, failed, _skipped = prepare(args, game, kept, source)
        if ready and need_melonloader(game, args):
            say("== Getting MelonLoader %s" % MELONLOADER_VERSION)
            ml_zip = melonloader_zip(args.melonloader_zip)
            say("   sha256 verified")
            say()
    # Removed from Mods now, or from an older install by migrate() below.
    removed = [name for name in removing if remove_patch_dll(game, name) or name in old.patches]
    for name in removed:
        say("== %s: removed" % name)
    if not removed:
        say("== %s" % ("%s isn't installed" % removing[0] if len(removing) == 1 else "None of these patches are installed"))
    say()
    migrate(game)
    remove_retired(game)
    if ready:
        install_ready(game, ready, ml_zip)
    marker = read_marker(game)
    if args.melonloader:
        remove_melonloader(game)
        good("Done. Patches and MelonLoader removed.")
    else:
        if marker is not None or ready:
            write_marker(game, (marker or {}).get("source") or choose_source(args), (marker or {}).get("repo") or args.repo,
                         args.repo_dir)
        good("Done.%s MelonLoader stays installed for any other mods (remove it with --melonloader)."
             % (" Removed: %s." % ", ".join(removed) if removed else ""))
    print_later()
    if failed:
        error("couldn't reinstall: %s" % ", ".join(failed))
        return EXIT_FAILED
    return 0


def remove_melonloader(game):
    say("== Removing MelonLoader")
    version_dll = game.path / "version.dll"
    if version_dll.is_file():
        if is_melonloader_proxy(version_dll):
            version_dll.unlink()
            say("   removed version.dll")
        else:
            warn("%s isn't MelonLoader's; left in place" % version_dll)
    if (game.path / "MelonLoader").is_dir():
        shutil.rmtree(str(game.path / "MelonLoader"))
        say("   removed MelonLoader/")
    # Folders MelonLoader creates on its first start: removed only when nothing else is in them.
    for d in ("Mods", "Plugins", "UserLibs"):
        p = game.path / d
        if p.is_dir():
            try:
                p.rmdir()
                say("   removed empty %s/" % d)
            except OSError:
                say("   kept %s/ (%s)" % (d, ", ".join(sorted(f.name for f in p.iterdir())[:8])))
    others = [f.name for f in game.mods.glob("*.dll")] if game.mods.is_dir() else []
    if others:
        warn("these mods in %s won't load without MelonLoader: %s" % (game.mods, ", ".join(sorted(others))))
    userdata = game.path / "UserData"
    if marker_path(game).is_file():
        marker_path(game).unlink()
    if userdata.is_dir():
        try:
            userdata.rmdir()
            say("   removed empty UserData/")
        except OSError:
            say("   kept UserData/ (MelonLoader and mod settings: %s)"
                % ", ".join(sorted(f.name for f in userdata.iterdir())[:8]))
    if not prefix_ready(game.prefix):
        say("   no Proton prefix, so no DLL override to remove")
    elif wineserver_pids(game.prefix):
        warn("Wine is still running in the game's prefix; the DLL override was left in place. Run this again once "
             "the game has fully closed.")
    else:
        what, backup, other = reg_remove_override(game.prefix / "user.reg")
        if what == "removed":
            say('   removed the "%s" DLL override from %s (backup: %s)' % (REG_NAME, game.prefix / "user.reg", backup))
        elif what == "different":
            say('   the "%s" DLL override is "%s", which this tool doesn\'t set; left in place' % (REG_NAME, other))
        else:
            say("   no DLL override in the prefix registry")
    for cfg, opts in launch_options(game):
        if launch_option_override(opts):
            say("   Steam launch options still have %s; remove them in Steam > MIMESIS > Properties if you like" % opts)
    say()


# ---------------------------------------------------------------- command line

HELP = """\
Install, update, build or remove the MIMESIS patches on Linux (Steam with Proton).

MIMESIS is a Windows game; on Linux Steam runs it with Proton. The patches are MelonLoader mods: install sets up
MelonLoader (the Windows x64 package, sha256-checked), tells Proton to load it (a per-game Wine DLL override in the
game's Proton prefix registry, no launch options needed) and copies <Patch>Runtime.dll into <game>/Mods. It replaces
the Windows MimesisPatchesInstaller.exe and also moves installs made by older installers over to MelonLoader."""

EPILOG = """\
examples:
  %(prog)s status                      what's installed, and whether everything is set up
  %(prog)s list                        the patches and who needs them
  %(prog)s build                       build all patches from this checkout into dist/ (.NET SDK 8+)
  %(prog)s install                     install/update all patches (local builds, building them if needed)
  %(prog)s install Fov Minimap         install/update only these
  %(prog)s install --source github     use the latest GitHub releases instead (may be built for an older game)
  %(prog)s uninstall Fov               remove one patch
  %(prog)s uninstall --melonloader     remove all patches, MelonLoader and the DLL override

the game is found by: --game-dir, then $MIMESIS_DIR, then the folder this repo is cloned into, then every library
of Steam, Flatpak Steam (com.valvesoftware.Steam) and Snap Steam. The Proton prefix is
<library>/steamapps/compatdata/2827200/pfx, normally in the game's library; if several libraries have one, the one
Proton used last (or --prefix).

install/uninstall refuse to run while MIMESIS is running (--force overrides). The game's registry is only edited
while Wine isn't running in its prefix, and a backup (user.reg.mimesis-patches-<time>.bak) is kept next to it.
If the override doesn't work for you, use the Steam launch option instead:
  WINEDLLOVERRIDES="version=n,b" %%command%%

environment:
  MIMESIS_DIR       game folder (same as --game-dir)
  XDG_CACHE_HOME    downloads are cached in $XDG_CACHE_HOME/mimesis-patches (default ~/.cache/mimesis-patches)
  GITHUB_TOKEN      optional, for a higher GitHub API rate limit
  NO_COLOR          no colors

exit codes: 0 done, 1 failed (install: also when the DLL override couldn't be set yet), 2 bad command line or run
as root, 3 MIMESIS is running"""


def build_parser():
    common = argparse.ArgumentParser(add_help=False)
    g = common.add_argument_group("game")
    g.add_argument("--game-dir", metavar="PATH", help="the MIMESIS folder (the one with MIMESIS.exe)")
    g.add_argument("--prefix", metavar="PATH", help="the game's Proton prefix (default: found next to the game)")
    g.add_argument("--repo-dir", metavar="PATH", type=lambda s: Path(os.path.expanduser(s)).absolute(),
                   help="checkout of the patches repo to build from (default: the one this script is in)")
    common.add_argument("--no-color", action="store_true", help="no colored output")

    source = argparse.ArgumentParser(add_help=False)
    s = source.add_argument_group("where the patches come from")
    s.add_argument("--source", choices=("local", "github"),
                   help="local: builds from this checkout (<repo>/dist or <Patch>/Runtime/bin), built first if "
                        "missing or out of date (needs the .NET SDK 8+); github: latest release of each patch (may be "
                        "built for an older game version). Default: local in a checkout of the repo, github for a "
                        "copy of this script on its own")
    s.add_argument("--repo", default=DEFAULT_REPO, metavar="OWNER/NAME",
                   help="GitHub repo for --source github (default: %(default)s)")
    s.add_argument("--no-build", action="store_true", help="local source: never build, use the DLLs that are there")
    s.add_argument("--melonloader-zip", metavar="FILE",
                   help="use this MelonLoader.x64.zip (%s) instead of downloading it; it must match the official "
                        "sha256" % MELONLOADER_VERSION)
    s.add_argument("--force", action="store_true", help="go ahead even though MIMESIS is running")
    s.add_argument("-v", "--verbose", action="store_true", help="show the full dotnet build output")

    parser = argparse.ArgumentParser(prog=os.path.basename(sys.argv[0]) or "mimesis-patches.py", description=HELP,
                                     epilog=EPILOG, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version="%(prog)s " + TOOL_VERSION)
    sub = parser.add_subparsers(dest="cmd", metavar="COMMAND")
    sub.add_parser("status", parents=[common], help="show what's installed and whether everything is set up",
                   description="Game folder, Proton prefix, MelonLoader, the DLL override, installed patches, and "
                               "whether MIMESIS is running. Changes nothing.")
    sub.add_parser("list", parents=[common], help="list the patches and who needs them")
    b = sub.add_parser("build", parents=[common], help="build patches from this checkout into <repo>/dist",
                       description="Runs `dotnet build -c Release -p:GameDir=<game>/` in <Patch>/Runtime for each "
                                   "patch (all by default) and copies <Patch>Runtime.dll into <repo>/dist. Needs the "
                                   ".NET SDK 8 or newer. Keeps going when one fails; logs in <repo>/dist/logs.")
    b.add_argument("patches", nargs="*", metavar="PATCH", help="patches to build (default: all)")
    b.add_argument("-v", "--verbose", action="store_true", help="show the full dotnet build output")
    i = sub.add_parser("install", parents=[common, source], help="install or update patches (default: all)",
                       description="Installs MelonLoader if needed, sets the Wine DLL override in the game's Proton "
                                   "prefix, and copies the patches into <game>/Mods. Also updates installed patches. "
                                   "Patches installed by older installers are moved over too.")
    i.add_argument("patches", nargs="*", metavar="PATCH", help="patch names, or all (default)")
    i.add_argument("--reinstall-melonloader", action="store_true", help="extract MelonLoader again even if it's there")
    u = sub.add_parser("uninstall", parents=[common, source], help="remove patches (default: all)",
                       description="Removes the patch DLLs from <game>/Mods. MelonLoader stays unless --melonloader.")
    u.add_argument("patches", nargs="*", metavar="PATCH", help="patch names, or all (default)")
    u.add_argument("--melonloader", action="store_true",
                   help="also remove MelonLoader (version.dll, MelonLoader/; Mods/, Plugins/, UserLibs/ only if "
                        "empty) and the DLL override this tool added")
    return parser


def main(argv=None):
    global COLOR
    parser = build_parser()
    argv = sys.argv[1:] if argv is None else argv
    args = parser.parse_args(argv)
    if args.cmd is None:
        args = parser.parse_args(["status"] + argv)
    COLOR = sys.stdout.isatty() and not args.no_color and "NO_COLOR" not in os.environ \
        and os.environ.get("TERM") != "dumb"
    if args.repo_dir is None:
        args.repo_dir = SCRIPT.parent.parent if is_repo(SCRIPT.parent.parent) else None
    elif not is_repo(args.repo_dir):
        error("%s isn't a checkout of the patches repo (no Directory.Build.props / Common/MelonEntry.cs)." % args.repo_dir)
        return EXIT_USAGE
    if getattr(args, "repo", None) and not re.match(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", args.repo):
        error("--repo must look like OWNER/NAME, not %r" % args.repo)
        return EXIT_USAGE
    for attr, default in (("no_build", False), ("verbose", False), ("force", False), ("source", None),
                          ("repo", DEFAULT_REPO), ("melonloader_zip", None)):
        if not hasattr(args, attr):
            setattr(args, attr, default)
    if hasattr(os, "geteuid") and os.geteuid() == 0 and not getattr(args, "game_dir", None) \
            and not os.environ.get("MIMESIS_DIR"):
        warn("running as root: Steam is looked for in root's home folder. Run this as your normal user, without sudo.")
    commands = {"status": cmd_status, "list": cmd_list, "build": cmd_build, "install": cmd_install,
                "uninstall": cmd_uninstall}
    try:
        return commands[args.cmd](args)
    except Fail as e:
        error(str(e))
        return e.code
    except BrokenPipeError:
        # Output piped into e.g. head, which exited: stop quietly.
        os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())
        return 0
    except PermissionError as e:
        error("Permission denied: %s. Make sure you own the game folder and MIMESIS is closed." % e.filename)
        return EXIT_FAILED
    except OSError as e:
        error("Couldn't write the files (%s). Make sure MIMESIS is closed and try again." % e)
        return EXIT_FAILED
    except KeyboardInterrupt:
        say()
        error("interrupted")
        return 130


if __name__ == "__main__":
    sys.exit(main())
