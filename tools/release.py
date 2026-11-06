"""Releases one or more patches everywhere players get them, in one go.

    python tools/release.py Fov HostOptions --note "Fixed the slider" --token tss_... [--minor] [--no-thunderstore] [--dry-run]

For each patch: bump the version (patch number, or minor with --minor), add the note to its CHANGELOG (followed by
what's under "## Unreleased" there), update the version in its README and the root table, build it twice (the normal DLL with the in-game updater, for GitHub;
and -p:Thunderstore=1 without any updater code, for the Thunderstore package) and package it. Then commit and push, create the
GitHub release (installer users and the in-game Update button get it from there) and upload the package to
Thunderstore (Gale / r2modman users get it from there).

Thunderstore uploads need a service account token (thunderstore.io > Teams > medovanx > Service accounts), passed
with --token. Only patches listed in THUNDERSTORE are uploaded there.
"""
import argparse, json, os, re, subprocess, sys, tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from package_thunderstore import package, DESCRIPTIONS   # noqa: E402

REPO = "oliveira-sh/mimesis-patches"   # where GitHub releases go (the installer and in-game updater read them)
TEAM = "medovanx"
COMMUNITY = "mimesis"
CATEGORIES = ["mods"]
# Patches published on Thunderstore (tested in multiplayer). Others are GitHub only for now.
THUNDERSTORE = {"BiggerLobby", "Fov", "FpsCounter", "HostOptions", "HudPercent", "Inventory", "Minimap", "PSController", "SpectatorCam"}


def run(*cmd, cwd=ROOT, dry=False):
    print("  $", " ".join(str(c) for c in cmd))
    if not dry:
        subprocess.run(cmd, cwd=cwd, check=True)


def bump(patch, minor):
    csproj = next((ROOT / patch / "Runtime").glob("*.csproj"))
    text = csproj.read_text(encoding="utf-8")
    old = re.search(r"<Version>([^<]+)</Version>", text).group(1)
    major, mid, low = (int(x) for x in old.split("."))
    new = f"{major}.{mid + 1}.0" if minor else f"{major}.{mid}.{low + 1}"
    csproj.write_text(text.replace(f"<Version>{old}</Version>", f"<Version>{new}</Version>"), encoding="utf-8")
    readme = ROOT / patch / "README.md"
    readme.write_text(re.sub(r"\*\*Version [0-9.]+\*\*", f"**Version {new}**", readme.read_text(encoding="utf-8")), encoding="utf-8")
    root = ROOT / "README.md"
    root.write_text(re.sub(rf"(\| \[{patch}\]\({patch}/\) \| )[0-9.]+", rf"\g<1>{new}", root.read_text(encoding="utf-8")), encoding="utf-8")
    return old, new


def changelog(patch, version, note):
    path = ROOT / patch / "CHANGELOG.md"
    text = path.read_text(encoding="utf-8")
    # What's already listed under "## Unreleased" ships in this version too, after the note.
    unreleased = re.search(r"^## Unreleased\n(.*?)(?=^## |\Z)", text, re.M | re.S)
    pending = ""
    if unreleased:
        pending = "".join(line + "\n" for line in unreleased.group(1).split("\n") if line.strip())
        text = text[:unreleased.start()] + text[unreleased.end():]
    entry = f"## {version}\n" + "".join(f"- {line.strip()}\n" for line in note.split("\n") if line.strip()) + pending + "\n"
    head = f"# {patch} changelog\n\n"
    path.write_text(text.replace(head, head + entry, 1) if text.startswith(head) else head + entry + text, encoding="utf-8")
    return entry


def build(patch, dry):
    run("dotnet", "build", "-c", "Release", cwd=ROOT / patch / "Runtime", dry=dry)                       # GitHub / installer
    run("dotnet", "build", "-c", "Release", "-p:Thunderstore=1", cwd=ROOT / patch / "Runtime", dry=dry)   # Thunderstore, no updater
    if dry:
        return None
    return next((ROOT / patch / "Runtime" / "bin" / "Release").rglob(f"{patch}Runtime.dll"))


def upload_thunderstore(zip_path, token):
    """Uploads a package the way Thunderstore's CLI (tcli) does: start an upload, PUT the file in parts to the
    URLs it returns, finish the upload, then submit the package to the community. Returns (status, message)."""
    import urllib.request, urllib.error
    api = "https://thunderstore.io/api/experimental"
    # Cloudflare blocks Python's default user agent (error 1010).
    ua = "mimesis-patches-release/1.0 (+https://github.com/medovanx/mimesis-patches)"

    def call(method, url, payload=None, data=None, headers=None, auth=True):
        h = {"User-Agent": ua}
        if auth:
            h["Authorization"] = f"Bearer {token}"
        if payload is not None:
            data = json.dumps(payload).encode()
            h["Content-Type"] = "application/json"
        h.update(headers or {})
        req = urllib.request.Request(url, data=data, method=method, headers=h)
        with urllib.request.urlopen(req, timeout=600) as r:
            body = r.read()
            return r.headers, (json.loads(body) if body[:1] in (b"{", b"[") else body)

    data = zip_path.read_bytes()
    try:
        _, start = call("POST", f"{api}/usermedia/initiate-upload/", {"filename": zip_path.name, "file_size_bytes": len(data)})
        uuid = start["user_media"]["uuid"]
        parts = []
        for part in start["upload_urls"]:
            chunk = data[part["offset"]:part["offset"] + part["length"]]
            for attempt in range(3):   # storage uploads sometimes time out on slow connections: retry
                try:
                    headers, _ = call("PUT", part["url"], data=chunk, auth=False)   # presigned storage URL: no token
                    break
                except urllib.error.URLError:
                    if attempt == 2:
                        raise
            parts.append({"ETag": headers["ETag"], "PartNumber": part["part_number"]})
        call("POST", f"{api}/usermedia/{uuid}/finish-upload/", {"parts": parts})
        _, result = call("POST", f"{api}/submission/submit/", {
            "upload_uuid": uuid, "author_name": TEAM, "communities": [COMMUNITY], "categories": [],
            "community_categories": {COMMUNITY: CATEGORIES}, "has_nsfw_content": False})
        return 200, str(result)[:200]
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")[:500]
    except urllib.error.URLError as e:
        return 0, f"connection failed: {e.reason}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("patches", nargs="+", help="patch folder names, e.g. Fov HostOptions")
    ap.add_argument("--note", required=True, help="what changed (one CHANGELOG bullet per line)")
    ap.add_argument("--minor", action="store_true", help="bump the minor version instead of the patch number")
    ap.add_argument("--token", help="Thunderstore service account token (tss_...)")
    ap.add_argument("--no-thunderstore", action="store_true", help="GitHub release only")
    ap.add_argument("--dry-run", action="store_true", help="show what would happen, change nothing")
    args = ap.parse_args()

    for p in args.patches:
        if p not in DESCRIPTIONS:
            sys.exit(f"Unknown patch: {p}")
    token = args.token
    ts = not args.no_thunderstore
    if ts and not token and any(p in THUNDERSTORE for p in args.patches):
        sys.exit("Pass --token tss_... (Thunderstore service account token), or --no-thunderstore.")

    out = Path(tempfile.mkdtemp(prefix="mimesis-release-"))
    done = []
    for p in args.patches:
        print(f"== {p}")
        if args.dry_run:
            print(f"  would bump, add to CHANGELOG, build and package {p}")
            continue
        old, new = bump(p, args.minor)
        entry = changelog(p, new, args.note)
        dll = build(p, False)
        done.append((p, new, entry, dll, package(p, out)))
        print(f"  {old} -> {new}")

    if args.dry_run:
        return
    names = ", ".join(f"{p} {v}" for p, v, *_ in done)
    run("git", "add", "-A")
    run("git", "commit", "-q", "-m", f"Release {names}")
    run("git", "push", "-q")

    for p, v, entry, dll, zip_path in done:
        notes = entry.split("\n", 1)[1] + f"\nInstall: **MimesisPatchesInstaller.exe** or the in-game Patches window, or from Thunderstore with Gale / r2modman. Full history: [CHANGELOG](https://github.com/{REPO}/blob/main/{p}/CHANGELOG.md)"
        # -R: never let gh pick a remote on its own (a fork has the upstream as a second remote).
        run("gh", "release", "create", "-R", REPO, f"{p}-v{v}", str(dll), str(zip_path), "-t", f"{p} {v}", "--latest=false", "-n", notes)
        if ts and p in THUNDERSTORE:
            status, reply = upload_thunderstore(zip_path, token)
            print(f"  Thunderstore: {'uploaded' if status < 300 else f'FAILED ({status}) {reply}'}")
    print(f"\nReleased {names}. Packages are in {out}")


if __name__ == "__main__":
    main()
