// MIMESIS Patches - one "Patches" chip on the main menu that opens a window listing every installed patch
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Compiled into every runtime, but each patch is its own DLL, so they share state through the AppDomain
// using only BCL/Unity types: every patch adds an entry (a Dictionary) to one list, and the first patch to
// start becomes the "owner" that draws the chip and the window. Each entry's page is built by its own patch.
//
// The window: left sidebar = installed patches (orange dot = update available); right = the selected
// patch's version, status, update link and options. Updates are checked once per launch on GitHub releases.
// On Linux (Proton) the update buttons show the linux/mimesis-patches.py command instead of running the Windows installer.
// Thunderstore builds (-p:Thunderstore=1, define THUNDERSTORE) contain none of the update code: Thunderstore doesn't
// allow mods that update themselves, and the mod manager serves updates there.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
using System.Text.RegularExpressions;
#endif
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
using UnityEngine.Networking;
#endif
using UnityEngine.UI;

namespace MimesisPatches
{
    sealed class PatchMenu : MonoBehaviour
    {
        public const string Repo = "oliveira-sh/mimesis-patches";   // fork: releases built for the current game
        public const string RepoUrl = "https://github.com/" + Repo;
        const string SizeKey = "medovanx.patches.window";
        static readonly Vector2 MinSize = new Vector2(900f, 560f);
        const string RegistryKey = "medovanx.patches.registry", OwnerKey = "medovanx.patches.owner";

        static readonly Color ChipColor = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color UpdateColor = new Color(0.85f, 0.45f, 0.05f, 0.85f);

        /// <param name="build">Fills the patch's options page (parent has a vertical layout). Optional.</param>
        public static void Register(string name, Version version, Func<string> status = null, Action<RectTransform, TMP_FontAsset> build = null)
        {
            var list = AppDomain.CurrentDomain.GetData(RegistryKey) as List<Dictionary<string, object>>;
            if (list == null) AppDomain.CurrentDomain.SetData(RegistryKey, list = new List<Dictionary<string, object>>());
            list.RemoveAll(e => (string)e["name"] == name);
            list.Add(new Dictionary<string, object>
            {
                ["name"] = name,
                ["version"] = new Version(version.Major, version.Minor, Math.Max(version.Build, 0)),
                ["status"] = status,
                // Clear this assembly's refreshers before building its page, so only the visible page refreshes.
                ["build"] = build == null ? null : (Action<RectTransform, TMP_FontAsset>)((rt, font) => { PatchUi.Refreshers.Clear(); build(rt, font); PatchUi.RefreshAll(); }),
            });
            list.Sort((a, b) => string.CompareOrdinal((string)a["name"], (string)b["name"]));

            if (AppDomain.CurrentDomain.GetData(OwnerKey) == null)
            {
                AppDomain.CurrentDomain.SetData(OwnerKey, name);
                var go = new GameObject("MedovanxPatchMenu");
                DontDestroyOnLoad(go);
                go.AddComponent<PatchMenu>();
            }
        }

        static List<Dictionary<string, object>> Entries =>
            AppDomain.CurrentDomain.GetData(RegistryKey) as List<Dictionary<string, object>> ?? new List<Dictionary<string, object>>();

        static Version Latest(Dictionary<string, object> e) => e.TryGetValue("latest", out var v) ? v as Version : null;
        static bool HasUpdate(Dictionary<string, object> e) => Latest(e) != null && Latest(e) > (Version)e["version"];

        RectTransform _row;
        TMP_Text _chipText;
        Image _chipBg;
        TMP_FontAsset _font;
        bool _noVersionText;

#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
        /// <summary>True when the mods were installed by MimesisPatchesInstaller (directly in the game's Mods folder).
        /// Installs from a mod manager (r2modman, Gale: Mods\Team-Package\ or a profile folder) get their updates there,
        /// so the window doesn't check GitHub or offer to update itself (Thunderstore rule).</summary>
        static bool InstallerManaged
        {
            get
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(typeof(PatchMenu).Assembly.Location);
                    var mods = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "Mods");
                    return string.Equals(System.IO.Path.GetFullPath(dir).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(mods).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }
#endif

        IEnumerator Start()
        {
#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
            if (InstallerManaged) StartCoroutine(CheckForUpdates());
#endif
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                // Spawned UI prefabs are usually named "<prefab>(Clone)".
                var menu = GameObject.Find("UIPrefab_MainMenu(Clone)") ?? GameObject.Find("UIPrefab_MainMenu");
                // The version text gives the chip its font: UI_Text_Version since EA 0.4.0, versionText before.
                var version = menu != null ? (menu.transform.Find("VersionTextRect/UI_Text_Version") ?? menu.transform.Find("VersionTextRect/versionText"))?.GetComponent<TMP_Text>() : null;
                // EA 0.4.0 always hides VersionTextRect, and a hidden text has no canvas: take the menu's.
                var canvas = version != null ? menu.GetComponentInParent<Canvas>(true) : null;
                if (version == null)
                {
                    if (_row != null) _row.gameObject.SetActive(false);   // menu gone: hide
                    _hovered = 0;
                    if (menu != null && !_noVersionText)
                    {
                        _noVersionText = true;
                        Debug.LogWarning("[Patches] The main menu has no version text (VersionTextRect/UI_Text_Version), so the Patches chip can't be added. A game update may have changed the menu.");
                    }
                }
                else if (canvas != null)
                {
                    _font = version.font;
                    if (_row == null) BuildChip(canvas.rootCanvas.transform, version);
                    var entries = Entries;
                    int updates = entries.Count(HasUpdate);
                    _chipText.text = $"Patches ({entries.Count})" + (updates > 0 ? $", {updates} update{(updates > 1 ? "s" : "")}" : "");
                    _chipBg.color = updates > 0 ? UpdateColor : ChipColor;
                    // The main menu object stays active behind game scenes, so also require that no game scene is loaded.
                    bool onMenu = menu.activeInHierarchy && (Hub.s == null || Hub.s.pdata == null || Hub.s.pdata.main == null);
                    _row.gameObject.SetActive(onMenu);
                    if (!onMenu) { _hovered = 0; Close(); }
                }
                yield return wait;
            }
        }

#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
        IEnumerator CheckForUpdates()
        {
            using (var req = UnityWebRequest.Get($"https://api.github.com/repos/{UpdateRepo()}/releases?per_page=100"))
            {
                req.SetRequestHeader("User-Agent", "mimesis-patches");
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) yield break;   // offline etc.: no update hints
                foreach (Match m in Regex.Matches(req.downloadHandler.text, "\"tag_name\"\\s*:\\s*\"([A-Za-z]+)-v(\\d+\\.\\d+\\.\\d+)\""))
                {
                    var entry = Entries.FirstOrDefault(e => (string)e["name"] == m.Groups[1].Value);
                    if (entry == null) continue;
                    var v = new Version(m.Groups[2].Value);
                    if (Latest(entry) == null || v > Latest(entry)) entry["latest"] = v;
                }
                foreach (var e in Entries.Where(HasUpdate))
                    Debug.Log($"[{e["name"]}] Update available: {((Version)e["version"]).ToString(3)} -> {Latest(e).ToString(3)}");
            }
        }

        // ---------------- In-game update ----------------
        // Downloads the latest installer and runs it: it closes the game, installs the updates and starts the game again.

        const string InstallerUrl = RepoUrl + "/releases/latest/download/MimesisPatchesInstaller.exe";
        bool _updating;

        RectTransform _confirm;

        static string Eta(double seconds) =>
            seconds >= 3600 ? $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m" :
            seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{(int)Math.Ceiling(seconds)}s";

        // A popup over the whole window: dims everything behind it and blocks clicks until closed.
        RectTransform Dialog()
        {
            if (_confirm != null) Destroy(_confirm.gameObject);
            _confirm = PatchUi.Box(_window.transform, "UpdateDialog", new Color(0f, 0f, 0f, 0.6f));
            PatchUi.Stretch(_confirm);
            var box = PatchUi.Box(_confirm, "Box", new Color(0.09f, 0.09f, 0.09f, 0.98f));
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
            var v = box.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(32, 32, 28, 28);
            v.spacing = 18f;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandHeight = false;
            var fit = box.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            box.gameObject.AddComponent<LayoutElement>();
            box.sizeDelta = new Vector2(760f, 0f);
            return box;
        }

        void CloseDialog() { if (_confirm != null) Destroy(_confirm.gameObject); _confirm = null; }

        // Asks first: updating closes the game.
        void StartUpdate(string[] names)
        {
            if (_updating) return;
            if (LinuxTool) { LinuxUpdate(names); return; }   // never the Windows installer under Proton
            var box = Dialog();
            PatchUi.Label(box, _font, names.Length == 1 ? $"Update {names[0]}?" : $"Update {names.Length} patches?", 28f, null, 36f);
            PatchUi.Label(box, _font, "The game will close, install the update and start again.", 20f, PatchUi.Dim);
            var row = PatchUi.Row(box, 44f);
            PatchUi.Button(row, _font, "Update now", () =>
            {
                _updating = true;
                StartCoroutine(RunUpdate(names));
            }, 200f).GetComponent<Image>().color = UpdateColor;
            PatchUi.Button(row, _font, "Not now", CloseDialog, 160f);
        }

        // ---------------- Linux (Steam with Proton) ----------------
        // MIMESIS only ships for Windows, so on Linux Steam runs it with Proton (Wine). The installer .exe would run inside
        // the game's Wine prefix there and put the GitHub release DLLs over what linux/mimesis-patches.py installed (maybe
        // built for this very game version), so under Proton the update buttons show the Linux command instead.
        // Other Wine setups (CrossOver or Whisky on macOS) keep the installer: the Linux tool doesn't support them.

        static bool? _linuxTool;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>True when updates come from linux/mimesis-patches.py: under Wine (its ntdll exports wine_get_version,
        /// Windows' doesn't; WINEPREFIX in case a Wine build hides that export), started by Steam's Proton
        /// (STEAM_COMPAT_DATA_PATH) or installed with that tool (its marker file). Never throws.</summary>
        static bool LinuxTool
        {
            get
            {
                if (_linuxTool == null)
                {
                    bool wine = false, linux = false;
                    try
                    {
                        var ntdll = GetModuleHandle("ntdll.dll");
                        wine = ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
                    }
                    catch { }
                    try { wine |= !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEPREFIX")); } catch { }
                    try { linux = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")) || System.IO.File.Exists(MarkerFile); } catch { }
                    _linuxTool = wine && linux;
                    if (wine) Debug.Log(linux ? "[Patches] Running under Proton: updates are installed with linux/mimesis-patches.py"
                                              : "[Patches] Running under Wine, not Proton: updates use the Windows installer");
                }
                return _linuxTool.Value;
            }
        }

        // Written by mimesis-patches.py on every install: how the patches were installed (see linux/README.md).
        static string MarkerFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "UserData", "mimesis-patches-linux.json");

        /// <summary>The marker file's top-level string values (null for null or missing), or null without the file.</summary>
        static Func<string, string> ReadMarker()
        {
            try
            {
                if (!System.IO.File.Exists(MarkerFile)) return null;
                var json = System.IO.File.ReadAllText(MarkerFile, System.Text.Encoding.UTF8);
                return key =>
                {
                    var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    try { return m.Success ? Regex.Unescape(m.Groups[1].Value) : null; } catch { return null; }
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Patches] Couldn't read mimesis-patches-linux.json: " + ex.Message);
                return null;
            }
        }

        static bool IsRepoName(string s) => s != null && Regex.IsMatch(s, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");

        // Where updates are looked for: the repo the Linux update command installs from (--source github --repo), else ours.
        static string UpdateRepo()
        {
            var marker = LinuxTool ? ReadMarker() : null;
            var repo = marker?.Invoke("repo");
            return marker != null && marker("source") == "github" && IsRepoName(repo) ? repo : Repo;
        }

        /// <summary>The shell command that updates these patches, from the marker file, or null without it.
        /// repoDir: the checkout it builds from.</summary>
        static string LinuxCommand(string[] names, out string repoDir)
        {
            repoDir = null;
            var marker = ReadMarker();
            var script = marker?.Invoke("script");
            if (string.IsNullOrEmpty(script)) return null;
            var cmd = $"python3 {Quote(script)} install {string.Join(" ", names)}";
            if (marker("source") == "github")
            {
                var repo = marker("repo") ?? Repo;
                cmd += " --source github --repo " + (IsRepoName(repo) ? repo : Quote(repo));
            }
            else
            {
                // Named, so the tool builds from that checkout or says why it can't, instead of taking the GitHub releases.
                repoDir = marker("repo_dir");
                cmd += " --source local" + (string.IsNullOrEmpty(repoDir) ? "" : " --repo-dir " + Quote(repoDir));
            }
            return cmd;
        }

        // For a POSIX shell: in single quotes, a ' inside becomes '\''.
        static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

        // Under Proton: how to update with the Linux tool. Downloads and starts nothing.
        void LinuxUpdate(string[] names)
        {
            var cmd = LinuxCommand(names, out var repoDir);
            var how = "MIMESIS runs with Proton here, so updates come from the Linux tool (linux/mimesis-patches.py), not the Windows installer. ";
            if (cmd == null)   // not installed with it (or an older version of it): the generic way
            {
                cmd = $"python3 linux/mimesis-patches.py install {string.Join(" ", names)}";
                how += "Quit the game, then run this in a terminal, in your copy of the mimesis-patches repo (see linux/README.md there):";
            }
            else if (!string.IsNullOrEmpty(repoDir))
                how += $"It builds the patches from your copy of the repo ({repoDir}), so get the new version there first, for example with git pull. Then quit the game and run this in a terminal:";
            else
                how += "Quit the game, then run this in a terminal:";
            Debug.Log("[Patches] To update on Linux, run: " + cmd);

            var box = Dialog();
            PatchUi.Label(box, _font, names.Length == 1 ? $"Update {names[0]}" : $"Update {names.Length} patches", 28f, null, 36f);
            PatchUi.Label(box, _font, how, 20f, PatchUi.Dim).richText = false;   // paths may contain '<'
            var code = PatchUi.Box(box, "Command", new Color(1f, 1f, 1f, 0.06f));
            var pad = code.gameObject.AddComponent<VerticalLayoutGroup>();
            pad.padding = new RectOffset(16, 16, 12, 12);
            pad.childControlWidth = pad.childControlHeight = true;
            pad.childForceExpandHeight = false;
            PatchUi.Label(code, _font, cmd, 20f).richText = false;
            var row = PatchUi.Row(box, 44f);
            TMP_Text copied = null;
            copied = PatchUi.Button(row, _font, "Copy command", () =>
            {
                GUIUtility.systemCopyBuffer = cmd;
                copied.text = "Copied";
            }, 220f).GetComponentInChildren<TMP_Text>();
            PatchUi.Button(row, _font, "OK", CloseDialog, 160f);
        }

        void Failed(string message)
        {
            var box = Dialog();
            PatchUi.Label(box, _font, "Update failed", 28f, null, 36f);
            PatchUi.Label(box, _font, message, 20f, PatchUi.Dim);
            PatchUi.Button(PatchUi.Row(box, 44f), _font, "OK", CloseDialog, 160f);
            _updating = false;
        }

        static string ExistingInstaller(string game)
        {
            try
            {
                return System.IO.Directory.GetFiles(game, "MimesisPatchesInstaller*.exe")
                    .Concat(new[] { System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MimesisPatchesInstaller.exe") })
                    .Where(System.IO.File.Exists)
                    .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        IEnumerator RunUpdate(string[] names)
        {
            var box = Dialog();
            PatchUi.Label(box, _font, names.Length == 1 ? $"Updating {names[0]}" : $"Updating {names.Length} patches", 28f, null, 36f);
            var status = PatchUi.Label(box, _font, "Starting the download...", 20f, PatchUi.Dim);
            status.textWrappingMode = TextWrappingModes.NoWrap;   // one line: size | speed | time left
            var track = PatchUi.Box(box, "Progress", PatchUi.Off);
            track.gameObject.AddComponent<LayoutElement>().preferredHeight = 8f;
            var fill = PatchUi.Box(track, "Fill", UpdateColor);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.offsetMin = fill.offsetMax = Vector2.zero;

            var game = System.IO.Path.GetDirectoryName(Application.dataPath);   // the folder with MIMESIS.exe
            // Reuse an installer that's already here (game folder or a previous update): it updates itself when a newer
            // one exists, so only the small patch files get downloaded. Download the full installer (~50 MB) only if none.
            var exe = ExistingInstaller(game);
            if (exe != null) { status.text = "Starting the installer..."; fill.anchorMax = Vector2.one; }
            else exe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MimesisPatchesInstaller.exe");
            if (!System.IO.File.Exists(exe))
            using (var req = new UnityWebRequest(InstallerUrl, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(exe), null))
            {
                req.timeout = 1800;
                req.SendWebRequest();
                float started = Time.realtimeSinceStartup, lastTime = started;
                ulong lastBytes = 0;
                double speed = 0;   // bytes per second, smoothed
                while (!req.isDone)
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                    float now = Time.realtimeSinceStartup;
                    ulong got = req.downloadedBytes;
                    float p = req.downloadProgress;
                    double instant = (got - lastBytes) / Math.Max(0.001, now - lastTime);
                    speed = speed <= 0 ? instant : speed * 0.8 + instant * 0.2;
                    lastBytes = got; lastTime = now;
                    double total = p > 0.001f ? got / p : 0;
                    fill.anchorMax = new Vector2(Mathf.Clamp01(p), 1f);
                    string text = $"Downloading the update: {got / 1048576.0:0.0}" + (total > 0 ? $" / {total / 1048576.0:0.0} MB" : " MB");
                    if (speed > 0) text += $"  |  {speed / 1048576.0:0.00} MB/s";
                    if (speed > 0 && total > got) text += $"  |  {Eta((total - got) / speed)} left";
                    status.text = text;
                }
                status.text = "Downloaded. Closing the game to install...";
                fill.anchorMax = Vector2.one;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Failed("Couldn't download the update. Check your connection, or run MimesisPatchesInstaller.exe.");
                    yield break;
                }
            }
            try
            {
                System.Diagnostics.Process.Start(exe, $"install:{string.Join(",", names)} \"{game}\" --relaunch");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Patches] Couldn't start the installer: " + ex);
                Failed("Couldn't start the installer. Run MimesisPatchesInstaller.exe yourself.");
            }
        }
#endif

        // ---------------- Chip (bottom-left of the main menu) ----------------

        void BuildChip(Transform canvas, TMP_Text version)
        {
            _row = new GameObject("PatchChips", typeof(RectTransform)).GetComponent<RectTransform>();
            _row.SetParent(canvas, false);
            _row.SetAsLastSibling();
            _row.anchorMin = _row.anchorMax = _row.pivot = Vector2.zero;
            _row.anchoredPosition = new Vector2(24f, 20f);
            _row.sizeDelta = new Vector2(600f, 30f);
            var layout = _row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.LowerLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            var chip = new GameObject("Chip_Patches", typeof(RectTransform), typeof(Image), typeof(Button));
            chip.transform.SetParent(_row, false);
            _chipBg = chip.GetComponent<Image>();
            var button = chip.GetComponent<Button>();
            button.targetGraphic = _chipBg;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            button.colors = colors;
            button.onClick.AddListener(Open);
            Hover(chip);
            var pad = chip.AddComponent<HorizontalLayoutGroup>();
            pad.padding = new RectOffset(12, 12, 4, 4);
            pad.childControlWidth = pad.childControlHeight = true;
            pad.childForceExpandWidth = pad.childForceExpandHeight = false;

            _chipText = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            _chipText.transform.SetParent(chip.transform, false);
            _chipText.font = version.font;
            _chipText.fontSize = version.fontSize * 0.85f;
            _chipText.color = PatchUi.Text;
            _chipText.raycastTarget = false;
            Debug.Log("[Patches] Main menu chip added");
        }

        // ---------------- Window ----------------

        GameObject _window;
        RectTransform _sidebar, _content;
#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
        Button _updateAll;
#endif
        string _selected;

        void Open()
        {
            if (_window == null) BuildWindow();
            _window.SetActive(true);
            var entries = Entries;
            if (_selected == null || entries.All(e => (string)e["name"] != _selected)) _selected = entries.FirstOrDefault()?["name"] as string;
            BuildSidebar();
            Select(_selected);
        }

        void Close() { if (_window != null) _window.SetActive(false); }

        void BuildWindow()
        {
            var canvas = new GameObject("PatchesWindow", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)).GetComponent<Canvas>();
            canvas.transform.SetParent(transform, false);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            _window = canvas.gameObject;

            // Dimmed backdrop; clicking it closes the window.
            var dim = PatchUi.Box(canvas.transform, "Dim", new Color(0f, 0f, 0f, 0.55f));
            PatchUi.Stretch(dim);
            var dimButton = dim.gameObject.AddComponent<Button>();
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(Close);

            var panel = PatchUi.Box(canvas.transform, "Panel", new Color(0.06f, 0.06f, 0.06f, 0.96f));
            panel.sizeDelta = new Vector2(
                Mathf.Clamp(PlayerPrefs.GetFloat(SizeKey + ".w", 1100f), MinSize.x, 1900f),
                Mathf.Clamp(PlayerPrefs.GetFloat(SizeKey + ".h", 760f), MinSize.y, 1060f));
            panel.anchoredPosition = new Vector2(PlayerPrefs.GetFloat(SizeKey + ".x", 0f), PlayerPrefs.GetFloat(SizeKey + ".y", 0f));
            WindowFrame.AddMove(panel);   // first, so the title bar buttons sit on top of it

            // Title bar with close button.
            var title = PatchUi.Label(panel, _font, "Patches", 30f);
            Destroy(title.GetComponent<LayoutElement>());
            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.pivot = new Vector2(0f, 1f);
            title.rectTransform.offsetMin = new Vector2(28f, -64f);
            title.rectTransform.offsetMax = new Vector2(-80f, -16f);
            // Drawn as two crossed bars (the game font has no ✕ glyph) instead of a plain "X" letter.
            var close = PatchUi.Box(panel, "Close", PatchUi.Off);
            close.anchorMin = close.anchorMax = close.pivot = new Vector2(1f, 1f);
            close.anchoredPosition = new Vector2(-16f, -16f);
            close.sizeDelta = new Vector2(44f, 44f);
            close.GetComponent<Image>().color = PatchUi.Off;
            var closeButton = close.gameObject.AddComponent<Button>();
            closeButton.targetGraphic = close.GetComponent<Image>();
            closeButton.onClick.AddListener(Close);
            Hover(close.gameObject);
            foreach (var angle in new[] { 45f, -45f })
            {
                var bar = PatchUi.Box(close, "Bar", PatchUi.Text);
                bar.sizeDelta = new Vector2(22f, 3f);
                bar.anchorMin = bar.anchorMax = bar.pivot = new Vector2(0.5f, 0.5f);
                bar.anchoredPosition = Vector2.zero;
                bar.localRotation = Quaternion.Euler(0f, 0f, angle);
                bar.GetComponent<Image>().raycastTarget = false;
            }

#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
            // "Update all" in the title bar, left of the close button; shown when any patch has an update.
            _updateAll = PatchUi.Button(panel, _font, "Update all", () =>
                StartUpdate(Entries.Where(HasUpdate).Select(x => (string)x["name"]).ToArray()), 200f);
            _updateAll.GetComponent<Image>().color = UpdateColor;
            var ua = (RectTransform)_updateAll.transform;
            Destroy(ua.GetComponent<LayoutElement>());
            ua.anchorMin = ua.anchorMax = ua.pivot = new Vector2(1f, 1f);
            ua.anchoredPosition = new Vector2(-72f, -16f);
            ua.sizeDelta = new Vector2(200f, 44f);
#endif

            // Author footer along the bottom; clicking it opens the repo.
            var footer = PatchUi.Label(panel, _font,
                $"Made by <b>Mohamed Darwesh</b> (@medovanx)   |   <u>github.com/{Repo}</u>", 18f, PatchUi.Dim);
            Destroy(footer.GetComponent<LayoutElement>());
            footer.alignment = TextAlignmentOptions.Center;
            footer.raycastTarget = true;
            var frt = footer.rectTransform;
            frt.anchorMin = new Vector2(0f, 0f);
            frt.anchorMax = new Vector2(1f, 0f);
            frt.pivot = new Vector2(0.5f, 0f);
            frt.offsetMin = new Vector2(16f, 12f);
            frt.offsetMax = new Vector2(-16f, 44f);
            footer.gameObject.AddComponent<Button>().onClick.AddListener(() => Application.OpenURL(RepoUrl));
            Hover(footer.gameObject);

            // Left sidebar: one button per patch, scrolling when they don't all fit (many patches, or a small window)
            // instead of squeezing the buttons.
            var sideBox = PatchUi.Box(panel, "Sidebar", new Color(1f, 1f, 1f, 0.04f));
            sideBox.anchorMin = new Vector2(0f, 0f);
            sideBox.anchorMax = new Vector2(0f, 1f);
            sideBox.pivot = new Vector2(0f, 0.5f);
            sideBox.offsetMin = new Vector2(16f, 56f);
            sideBox.offsetMax = new Vector2(296f, -80f);
            _sidebar = ScrollArea(sideBox);
            PatchUi.Stretch((RectTransform)_sidebar.parent.parent);   // the scroll area fills the box
            var sideLayout = _sidebar.gameObject.AddComponent<VerticalLayoutGroup>();
            sideLayout.padding = new RectOffset(8, 12, 8, 8);
            sideLayout.spacing = 6f;
            sideLayout.childControlWidth = sideLayout.childControlHeight = true;
            sideLayout.childForceExpandHeight = false;
            _sidebar.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Right content.
            _content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            _content.SetParent(panel, false);
            _content.anchorMin = Vector2.zero;
            _content.anchorMax = Vector2.one;
            _content.offsetMin = new Vector2(320f, 56f);
            _content.offsetMax = new Vector2(-24f, -80f);
            var contentLayout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 14f;
            contentLayout.childControlWidth = contentLayout.childControlHeight = true;
            contentLayout.childForceExpandHeight = false;

            WindowFrame.AddResize(panel);   // last, so the edges are on top
        }

        void BuildSidebar()
        {
            // Hidden at once (Destroy only happens at the end of the frame), so the layout below sees the new list only.
            foreach (Transform c in _sidebar) { c.gameObject.SetActive(false); Destroy(c.gameObject); }
            int index = 0, selectedIndex = -1;
            foreach (var e in Entries)
            {
                var name = (string)e["name"];
                var label = $"{name}  <size=70%><color=#A6AAA0>v{((Version)e["version"]).ToString(3)}</color></size>" + (HasUpdate(e) ? "  <color=#E08A1E>●</color>" : "");
                var b = PatchUi.Button(_sidebar, _font, label, () => { _selected = name; Select(name); }, 260f, 48f);
                var text = b.GetComponentInChildren<TMP_Text>();
                text.alignment = TextAlignmentOptions.MidlineLeft;
                text.margin = new Vector4(14f, 0f, 8f, 0f);
                b.GetComponent<Image>().color = name == _selected ? PatchUi.On : PatchUi.Off;
                if (name == _selected) selectedIndex = index;
                index++;
            }
            ScrollSidebarTo(selectedIndex);
        }

        // Scrolls the sidebar just enough to show the button at this index (48 px buttons, 6 px apart, 8 px padding).
        void ScrollSidebarTo(int index)
        {
            var scroll = _sidebar != null ? _sidebar.GetComponentInParent<ScrollRect>() : null;
            if (scroll == null || index < 0) return;
            Canvas.ForceUpdateCanvases();
            float list = _sidebar.rect.height, view = scroll.viewport.rect.height, room = list - view;
            if (room <= 0f) return;
            float top = 8f + index * 54f, bottom = top + 48f;
            float offset = (1f - scroll.verticalNormalizedPosition) * room;   // how far down the list the view starts
            if (top < offset) offset = top - 8f;
            else if (bottom > offset + view) offset = bottom + 8f - view;
            else return;
            scroll.verticalNormalizedPosition = 1f - Mathf.Clamp(offset, 0f, room) / room;
        }

        void Select(string name)
        {
            BuildSidebar();
#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
            int updates = Entries.Count(HasUpdate);
            _updateAll.gameObject.SetActive(updates > 0);
            _updateAll.GetComponentInChildren<TMP_Text>().text = updates > 1 ? $"Update all ({updates})" : "Update";
#endif
            foreach (Transform c in _content) Destroy(c.gameObject);
            var e = Entries.FirstOrDefault(x => (string)x["name"] == name);
            if (e == null) return;

            PatchUi.Label(_content, _font, $"{name}  <size=70%><color=#A6AAA0>v{((Version)e["version"]).ToString(3)}</color></size>", 30f, null, 40f);
            if (e["status"] is Func<string> status)
            {
                string s = null;
                try { s = status(); } catch { }
                if (!string.IsNullOrEmpty(s)) PatchUi.Label(_content, _font, s, 18f, PatchUi.Dim, 26f);
            }
#if !THUNDERSTORE   // the updater is not in the Thunderstore build (mod managers handle updates)
            if (HasUpdate(e))
            {
                var row = PatchUi.Row(_content, 44f);
                PatchUi.Button(row, _font, $"Update to {Latest(e).ToString(3)}", () => StartUpdate(new[] { name }), 220f)
                    .GetComponent<Image>().color = UpdateColor;
                PatchUi.Button(row, _font, "What's new", () => Application.OpenURL($"{RepoUrl}/blob/main/{name}/CHANGELOG.md"), 160f);
            }
#endif

            if (e["build"] is Action<RectTransform, TMP_FontAsset> build)
            {
                var page = ScrollArea(_content);
                var layout = page.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 14f;
                layout.padding = new RectOffset(0, 24, 8, 16);
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                page.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                try { build(page, _font); }
                catch (Exception ex) { Debug.LogError($"[{name}] Options page failed: {ex}"); }
            }
        }

        // Fills the rest of the content column and scrolls (mouse wheel or the bar on the right) when the
        // options don't fit. Returns the scrolled content, which grows to fit its children.
        RectTransform ScrollArea(Transform parent)
        {
            var root = new GameObject("Scroll", typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(parent, false);
            root.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            viewport.SetParent(root, false);
            PatchUi.Stretch(viewport);
            // A transparent image so the wheel scrolls anywhere over the page, not just over controls.
            viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

            var content = new GameObject("Options", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;

            var bar = PatchUi.Box(root, "Scrollbar", new Color(1f, 1f, 1f, 0.05f));
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = Vector2.one;
            bar.pivot = new Vector2(1f, 0.5f);
            bar.sizeDelta = new Vector2(8f, 0f);
            var handleArea = new GameObject("Handle Area", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(bar, false);
            PatchUi.Stretch(handleArea);
            var handle = PatchUi.Box(handleArea, "Handle", new Color(1f, 1f, 1f, 0.3f));
            handle.offsetMin = handle.offsetMax = Vector2.zero;
            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handle.GetComponent<Image>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;   // never scroll past the last option
            scroll.scrollSensitivity = 30f;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return content;
        }

        // ---------------- Hand cursor while hovering the chip ----------------

        static int _hovered;
        static IntPtr _hand;
        const int IDC_HAND = 32649;
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, int name);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);

        static void Hover(GameObject target)
        {
            var hover = target.AddComponent<EventTrigger>();
            void On(EventTriggerType type, int delta)
            {
                var entry = new EventTrigger.Entry { eventID = type };
                entry.callback.AddListener(_ => _hovered = Mathf.Max(0, _hovered + delta));
                hover.triggers.Add(entry);
            }
            On(EventTriggerType.PointerEnter, 1);
            On(EventTriggerType.PointerExit, -1);
        }

        void LateUpdate()
        {
            if (_hovered <= 0) return;
            if (_hand == IntPtr.Zero) _hand = LoadCursor(IntPtr.Zero, IDC_HAND);
            SetCursor(_hand);   // every frame, since Windows resets it on mouse move
        }
    }

    /// <summary>Moving (drag the title bar) and resizing (drag any edge or corner) the Patches window.
    /// Size and position are remembered; the cursor changes to a move/resize arrow over those areas.</summary>
    sealed class WindowFrame : MonoBehaviour, IDragHandler, IEndDragHandler, IBeginDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        const float MinW = 900f, MinH = 560f, Edge = 8f, Corner = 22f, TitleBar = 72f;
        const int IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643, IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646;
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, int name);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);

        RectTransform _panel;
        Vector2Int _dir;          // which edges move: x -1 left / 1 right, y -1 top / 1 bottom; (0,0) = move
        int _cursor;
        bool _hover, _dragging;
        IntPtr _handle;

        static WindowFrame Make(RectTransform panel, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Vector2Int dir, int cursor)
        {
            var rt = PatchUi.Box(panel, name, new Color(1f, 1f, 1f, 0.001f));
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
            var f = rt.gameObject.AddComponent<WindowFrame>();
            f._panel = panel; f._dir = dir; f._cursor = cursor;
            return f;
        }

        public static void AddMove(RectTransform panel) =>
            Make(panel, "TitleBar", new Vector2(0f, 1f), Vector2.one, new Vector2(Edge, -TitleBar), new Vector2(-Edge, -Edge), Vector2Int.zero, IDC_SIZEALL);

        public static void AddResize(RectTransform panel)
        {
            Make(panel, "Left", Vector2.zero, new Vector2(0f, 1f), new Vector2(0f, Corner), new Vector2(Edge, -Corner), new Vector2Int(-1, 0), IDC_SIZEWE);
            Make(panel, "Right", new Vector2(1f, 0f), Vector2.one, new Vector2(-Edge, Corner), new Vector2(0f, -Corner), new Vector2Int(1, 0), IDC_SIZEWE);
            Make(panel, "Top", new Vector2(0f, 1f), Vector2.one, new Vector2(Corner, -Edge), new Vector2(-Corner, 0f), new Vector2Int(0, -1), IDC_SIZENS);
            Make(panel, "Bottom", Vector2.zero, new Vector2(1f, 0f), new Vector2(Corner, 0f), new Vector2(-Corner, Edge), new Vector2Int(0, 1), IDC_SIZENS);
            Make(panel, "TopLeft", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -Corner), new Vector2(Corner, 0f), new Vector2Int(-1, -1), IDC_SIZENWSE);
            Make(panel, "TopRight", Vector2.one, Vector2.one, new Vector2(-Corner, -Corner), Vector2.zero, new Vector2Int(1, -1), IDC_SIZENESW);
            Make(panel, "BottomLeft", Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(Corner, Corner), new Vector2Int(-1, 1), IDC_SIZENESW);
            var grip = Make(panel, "BottomRight", new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-Corner, 0f), new Vector2(0f, Corner), new Vector2Int(1, 1), IDC_SIZENWSE);
            // Three short diagonal lines in the bottom-right corner, like a window grip.
            for (int i = 0; i < 3; i++)
            {
                var line = PatchUi.Box(grip.transform, "Line", new Color(1f, 1f, 1f, 0.35f));
                float len = 5f + 5f * i;
                line.anchorMin = line.anchorMax = new Vector2(1f, 0f);
                line.sizeDelta = new Vector2(len * 1.414f, 2f);
                line.anchoredPosition = new Vector2(-4f - len / 2f, 4f + len / 2f);
                line.localRotation = Quaternion.Euler(0f, 0f, 45f);
                line.GetComponent<Image>().raycastTarget = false;
            }
        }

        public void OnPointerEnter(PointerEventData e) => _hover = true;
        public void OnPointerExit(PointerEventData e) => _hover = false;
        public void OnBeginDrag(PointerEventData e) => _dragging = true;

        void LateUpdate()
        {
            if (!_hover && !_dragging) return;
            if (_handle == IntPtr.Zero) _handle = LoadCursor(IntPtr.Zero, _cursor);
            SetCursor(_handle);   // every frame, since Windows resets it on mouse move
        }

        public void OnDrag(PointerEventData e)
        {
            var canvas = _panel.GetComponentInParent<Canvas>();
            var d = e.delta / (canvas != null ? canvas.scaleFactor : 1f);
            if (_dir == Vector2Int.zero)
            {
                _panel.anchoredPosition += d;
            }
            else
            {
                var old = _panel.sizeDelta;
                var size = new Vector2(
                    _dir.x == 0 ? old.x : Mathf.Clamp(old.x + _dir.x * d.x, MinW, 1900f),
                    _dir.y == 0 ? old.y : Mathf.Clamp(old.y - _dir.y * d.y, MinH, 1060f));
                var change = size - old;
                _panel.sizeDelta = size;
                // The panel is centred, so shift it by half the change to keep the opposite edges in place.
                _panel.anchoredPosition += new Vector2(_dir.x * change.x / 2f, -_dir.y * change.y / 2f);
            }
            // Keep at least part of the title bar on screen.
            var area = ((RectTransform)canvas.transform).rect.size / 2f;
            var p = _panel.anchoredPosition;
            _panel.anchoredPosition = new Vector2(Mathf.Clamp(p.x, -area.x, area.x), Mathf.Clamp(p.y, -area.y - _panel.sizeDelta.y / 2f + 80f, area.y - _panel.sizeDelta.y / 2f));
        }

        public void OnEndDrag(PointerEventData e)
        {
            _dragging = false;
            PlayerPrefs.SetFloat("medovanx.patches.window.w", _panel.sizeDelta.x);
            PlayerPrefs.SetFloat("medovanx.patches.window.h", _panel.sizeDelta.y);
            PlayerPrefs.SetFloat("medovanx.patches.window.x", _panel.anchoredPosition.x);
            PlayerPrefs.SetFloat("medovanx.patches.window.y", _panel.anchoredPosition.y);
            PlayerPrefs.Save();
        }
    }
}
