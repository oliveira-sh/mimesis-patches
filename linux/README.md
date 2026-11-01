# MIMESIS Patches on Linux

Install, update, build or remove the patches on **Linux and Steam Deck** (Steam with Proton). `mimesis-patches.py` does what [`MimesisPatchesInstaller.exe`](../Installer/) does on Windows, adapted to Proton. It's one Python 3.8+ file; it builds the patches against your copy of the game, which needs the [.NET SDK 8 or newer](#net-sdk).

MIMESIS only ships for Windows, so Steam runs it with Proton. The patches are MelonLoader mods, which load under Proton once Wine is told to use MelonLoader's `version.dll`. This tool sets that up for you; no launch options needed.

## Quick start
1. Install the [.NET SDK 8 or newer](#net-sdk) (once), and git if you don't have it.
2. Start MIMESIS once from Steam and quit it (this creates the game's Proton prefix).
3. In a terminal (Steam Deck: Desktop Mode > Konsole):
   ```sh
   git clone -b linux https://github.com/oliveira-sh/mimesis-patches
   cd mimesis-patches
   python3 linux/mimesis-patches.py install
   ```
   No git? On the GitHub page, **Code > Download ZIP**, unzip it, and run the last line in the unzipped folder.
4. Start MIMESIS from Steam as usual. The **Patches** chip in the bottom-left of the main menu lists your patches.

`install` installs or updates all patches. To pick some: `install Fov Minimap`. Run `status` any time to check that everything is set up.

Don't use `sudo`: Steam and the game belong to your user, so the tool refuses to change them as root.

**After every MIMESIS update, run `install` again.** It rebuilds the patches against the new game code. The in-game Patches window can't tell you about this, as it only checks for new GitHub releases.

## Commands

| Command | What it does |
|---|---|
| `status` | Game folder, Proton prefix, MelonLoader, the DLL override, installed patches, whether MIMESIS is running. Changes nothing. |
| `list` | The patches, who needs them and what they do. |
| `build [PATCH ...]` | Builds patches from this checkout into `dist/` (all by default). Needs the .NET SDK 8+. |
| `install [PATCH ...\|all]` | Installs or updates patches (all by default). |
| `uninstall [PATCH ...\|all]` | Removes patches (all by default). MelonLoader stays; `--melonloader` removes it too, with the DLL override. |

Useful options (`--help` on any command lists them all):
- `--source local` (the default in a checkout): builds from this checkout, built first if missing or older than the sources or the game. When nothing is built yet and the .NET SDK isn't installed, `install` stops and says how to install it. `--no-build` uses what's there.
- `--source github [--repo OWNER/NAME]`: the latest GitHub release of each patch (the default only for a copy of the script outside a checkout). **These may be built for an older game version**: the upstream releases target EA 0.3.1, and the tool tells you when your game was updated after them. Building locally compiles the patches against your game.
- `--game-dir PATH` (or `MIMESIS_DIR`), `--prefix PATH`: when the game or its prefix isn't found on its own.
- `--force`: go ahead although MIMESIS is running.

Exit codes: 0 done, 1 failed (for `install`, also when the DLL override couldn't be set yet: the patches are copied, but MelonLoader won't start until it is), 2 bad command line or run as root, 3 MIMESIS is running.

## What install does
1. **Finds the game** in every library of Steam, Flatpak Steam and Snap Steam (`libraryfolders.vdf`, `appmanifest_2827200.acf`), or uses `--game-dir`. It must be the Windows build (`MIMESIS.exe`); if Steam installed a native Linux build, force Proton in MIMESIS > Properties > Compatibility.
2. **Gets the patches**: local builds (`dist/` or `<Patch>/Runtime/bin/Release`) or GitHub releases (cached in `~/.cache/mimesis-patches`, checked against GitHub's sha256). Everything is ready before the game folder is touched.
3. **MelonLoader 0.7.3** (the Windows x64 package, sha256-checked) goes into the game folder, once.
4. **The DLL override**: `"version"="native,builtin"` under `[Software\\Wine\\AppDefaults\\MIMESIS.exe\\DllOverrides]` in the prefix's `user.reg` (`<library>/steamapps/compatdata/2827200/pfx`, normally in the game's library; if several libraries have one, the one Proton used last, or pick it with `--prefix`). It only affects MIMESIS.exe, the rest of the file stays byte-for-byte the same, and a backup `user.reg.mimesis-patches-<time>.bak` is kept next to it. Wine rewrites this file when it exits, so it's only edited while the game (and Wine) isn't running. If the prefix doesn't exist yet or Wine is still running in it, `install` says so at the end and exits with 1; run it again once that's fixed (not needed when the Steam launch option below is set).
5. **Copies** `<Patch>Runtime.dll` into `<game>/Mods`.
6. **Writes** `<game>/UserData/mimesis-patches-linux.json` (see below).

Older installs are moved over like the Windows installer does: BepInEx plugins (installer 4) and the original Assembly-CSharp.dll installs (installers 1-3). One difference: if Steam updated the game since such an install, its `Assembly-CSharp.dll.bak` is older game code, so it's deleted instead of restored.

## Uninstall
`python3 linux/mimesis-patches.py uninstall` removes all patches; `uninstall Fov` removes one. MelonLoader stays installed for any other mods.

`uninstall --melonloader` also removes `version.dll`, the `MelonLoader` folder and the DLL override this tool added. `Mods`, `Plugins` and `UserLibs` are removed only when empty, and `UserData` (your settings) only when nothing is left in it.

## Building
`python3 linux/mimesis-patches.py build` runs `dotnet build -c Release -p:GameDir=<game>/` in each `<Patch>/Runtime` and collects the DLLs in `dist/`, with a log per patch in `dist/logs/`. A patch that fails to build is reported and the others still build. `install` builds by itself when needed.

You can also run `dotnet build -c Release` in a `<Patch>/Runtime` folder yourself: it finds the game in the default Steam, Flatpak or Snap Steam library, or set `MIMESIS_DIR` (or pass `-p:GameDir=/path/to/MIMESIS`) when it's in another library.

### .NET SDK
Building needs the .NET SDK 8 or newer (the .NET runtime alone isn't enough). The first build downloads its NuGet packages (MelonLoader's, among others) from nuget.org, so it needs internet access.
- Fedora: `sudo dnf install dotnet-sdk-8.0`
- Ubuntu: `sudo apt install dotnet-sdk-8.0`
- Arch: `sudo pacman -S dotnet-sdk`
- Any distro, including Steam Deck, Debian, openSUSE, Bazzite and other immutable systems (no root needed; installs into `~/.dotnet`, which the tool finds):
  ```sh
  curl -sSLo /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0
  ```
  To run `dotnet build` yourself after that, add it to your PATH first: `export PATH="$HOME/.dotnet:$PATH"`.

## Troubleshooting
- **Does MelonLoader start?** Start the game, then run `status`: the MelonLoader line says when it last started, which should be just now. `MelonLoader/Latest.log` in the game folder has the details: its startup ends with `11 Mods loaded.` (or however many you installed). It stays from earlier starts, so check its time, not just that it's there. If MelonLoader doesn't start, set Steam's launch option, which does the same as the DLL override:
  ```
  WINEDLLOVERRIDES="version=n,b" %command%
  ```
  (Steam > MIMESIS > Properties > Launch Options.)
- **Did a patch work?** Each patch writes to the game's own log, in the Proton prefix: `<library>/steamapps/compatdata/2827200/pfx/drive_c/users/steamuser/AppData/LocalLow/ReLUGames/MIMESIS/Player.log`. Its lines start with `[<Patch>]`, e.g. `[BiggerLobby] v1.5.7: patches applied (10 players)`. `Failed to ...` or `not applied` there means the patch doesn't work with this game version.
- **After a game update a patch fails to build, or logs `Failed to ...`**: the game changed something the patch relies on, and the patch needs updating. Remove it with `uninstall <Patch>` until it's fixed (then `git pull` and `install` again), and report it with the build log from `dist/logs/` or the `Player.log` lines.

## Good to know
- **Flatpak / Snap Steam**: found automatically. Run the tool in a normal terminal, not inside the Flatpak.
- **Deleted the Proton prefix**, or switched Proton versions and the override is gone? Start the game once, quit, and run `install` again (`status` shows it).
- **MIMESIS is running** (exit code 3): quit the game and wait a few seconds for Wine to exit.
- **An override you set yourself** for `version` under MIMESIS.exe (e.g. with winecfg or protontricks) is replaced by `native,builtin`; `install` shows the old value, and the `user.reg` backup keeps it. `uninstall --melonloader` removes the override rather than restoring it.
- Besides what you ask `uninstall` to remove and the older installs it moves over (above), the tool deletes nothing. Other mods in `Mods` are left alone.

## For the in-game Patches window
After every install, `<game>/UserData/mimesis-patches-linux.json` says how the patches were installed:

```json
{
  "script": "/home/me/mimesis-patches/linux/mimesis-patches.py",
  "repo_dir": "/home/me/mimesis-patches",
  "source": "local",
  "repo": "medovanx/mimesis-patches",
  "patches": {
    "Fov": "1.5.4",
    "Minimap": "1.6.5"
  },
  "updated": "2026-11-01T14:51:47-03:00"
}
```

`repo_dir` is `null` when the script isn't in a checkout, `source` is `local` or `github`, and `patches` maps each patch DLL in `Mods` to its version (`null` if unknown). The command that updates patches with the same source is:

```sh
python3 '<script>' install <Name> [<Name> ...]                              # source "local"
python3 '<script>' install <Name> [<Name> ...] --source github --repo <repo>   # source "github"
```

`uninstall` updates the file, and removes it when no patch is left (or with `--melonloader`).

Under Proton the Patches window never runs the Windows installer. When it finds an update, its **Update** buttons show this command for the patches to update, with a **Copy command** button (for `local`, also a reminder to update the checkout first, e.g. `git pull`). Without the file it shows the generic `python3 linux/mimesis-patches.py install <Name>`.
