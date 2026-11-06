// MIMESIS Patches Installer - install logic shared by the window and the command line
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The patches are MelonLoader mods. The installer sets up MelonLoader in the game folder if needed, then downloads each
// patch's mod DLL from its latest GitHub release into <game>\Mods. It also cleans up the two older install types.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace MimesisInstaller
{
    record Release(Version Version, Dictionary<string, string> Assets);

    /// <summary>A patch: its mod DLL comes from its latest GitHub release.</summary>
    record PatchInfo(string Name, string About, string Who)
    {
        public string Dll => Name + "Runtime.dll";
    }

    static class Engine
    {
        public const string Repo = "oliveira-sh/mimesis-patches";   // fork: releases built for the current game
        public static readonly string Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        // MelonLoader, the mod loader the patches run in (the one MIMESIS mods on Thunderstore use).
        // Installed once into the game folder if it isn't there.
        const string MelonLoaderVersion = "0.7.3";
        const string MelonLoaderUrl = "https://github.com/LavaGang/MelonLoader/releases/download/v" + MelonLoaderVersion + "/MelonLoader.x64.zip";

        public static readonly List<PatchInfo> Patches = new List<PatchInfo>
        {
            new("BiggerLobby", "10-player lobbies + difficulty scaling", "Host"),
            new("PSController", "PlayStation button icons", "Anyone"),
            new("SpectatorCam", "Free spectator camera", "Anyone"),
            new("Minimap", "Minimap of the level", "Anyone"),
            new("HostOptions", "Stamina, money, difficulty", "Host"),
            new("HudPercent", "Health / radiation % on the HUD", "Anyone"),
            new("Inventory", "Host sets 1-8 inventory slots", "Everyone"),
            new("LateJoin", "Join a game in progress", "Everyone"),
            new("Fov", "Field of view slider", "Anyone"),
            new("Revive", "Revive dead teammates", "Host"),
            new("FpsCounter", "FPS in the bottom-right corner", "Anyone"),
            new("Hallucinations", "Fake mimics at high radiation", "Everyone"),
            new("BetterMicrophone", "Noise removal for your voice", "Anyone"),
            new("FakePlayers", "Fake players for testing (off by default)", "Host"),
            new("BetterMimics", "Mimics that act like your friends", "Host"),
            new("Poltergeist", "Random hauntings and jumpscares", "Anyone"),
            new("Traitor", "Traitor destination on the tram", "Everyone"),
            new("Deathmatch", "Deathmatch destination on the tram", "Everyone"),
        };

        // Long timeout: patch files and the installer itself can be slow to download on some connections.
        public static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mimesis-patches-installer");
            return http;
        }

        /// <summary>Assembly-CSharp.dll next to the exe or under MIMESIS_Data\Managed in the given folder.</summary>
        public static string FindDll(string folder) => new[] {
            Path.Combine(folder, "Assembly-CSharp.dll"),
            Path.Combine(folder, "MIMESIS_Data", "Managed", "Assembly-CSharp.dll"),
        }.FirstOrDefault(File.Exists);

        /// <summary>Finds the game without asking: next to this exe, then the path the game writes at the top of its
        /// log on every launch (any copy that has been started once), then Steam's library folders.</summary>
        public static string DetectDll()
        {
            var here = FindDll(AppContext.BaseDirectory);
            if (here != null) return here;
            foreach (var managed in FromGameLog().Concat(FromSteam()))
            {
                try
                {
                    var dll = Path.Combine(managed, "Assembly-CSharp.dll");
                    if (File.Exists(dll)) return Path.GetFullPath(dll);
                }
                catch { }
            }
            return null;
        }

        // %USERPROFILE%\AppData\LocalLow\ReLUGames\MIMESIS\Player.log starts with "Mono path[0] = '<game>/MIMESIS_Data/Managed'".
        static IEnumerable<string> FromGameLog()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "ReLUGames", "MIMESIS");
            foreach (var name in new[] { "Player.log", "Player-prev.log" })
            {
                string line = null;
                try
                {
                    using var fs = new FileStream(Path.Combine(dir, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    for (int i = 0; i < 20 && (line = reader.ReadLine()) != null; i++)
                        if (line.StartsWith("Mono path[0]")) break;
                }
                catch { continue; }
                if (line == null || !line.StartsWith("Mono path[0]")) continue;
                int a = line.IndexOf('\''), b = line.LastIndexOf('\'');
                if (a >= 0 && b > a) yield return line.Substring(a + 1, b - a - 1).Replace('/', Path.DirectorySeparatorChar);
            }
        }

        // Steam: registry SteamPath -> steamapps\libraryfolders.vdf "path" entries -> steamapps\common\MIMESIS.
        static IEnumerable<string> FromSteam()
        {
            string steam = null;
            try { steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
            if (string.IsNullOrEmpty(steam)) yield break;
            var libraries = new List<string> { steam };
            try
            {
                var vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(lib, "steamapps", "common", "MIMESIS", "MIMESIS_Data", "Managed");
        }

        // Layout: <game>\MIMESIS_Data\Managed\Assembly-CSharp.dll (how the game is found) and <game>\Mods\<Patch>Runtime.dll.
        static string GameDir(string dll) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dll), "..", ".."));
        static string ModsDir(string dll) => Path.Combine(GameDir(dll), "Mods");

        // Older installs, moved to MelonLoader by the next install: BepInEx plugins (installer 4) and DLLs in Managed (installers 1-3).
        static string BepInExDir(string dll) => Path.Combine(GameDir(dll), "BepInEx", "plugins", "MimesisPatches");

        public static bool IsInstalled(string dll, PatchInfo p) =>
            File.Exists(Path.Combine(ModsDir(dll), p.Dll)) || File.Exists(Path.Combine(BepInExDir(dll), p.Dll))
            || File.Exists(Path.Combine(Path.GetDirectoryName(dll), p.Dll));

        /// <summary>Latest release per name, from tags "Name-vX.Y.Z" (includes "Installer").</summary>
        public static Dictionary<string, Release> LatestReleases()
        {
            var result = new Dictionary<string, Release>();
            using var doc = JsonDocument.Parse(Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100").Result);
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.GetProperty("draft").GetBoolean()) continue;
                var tag = rel.GetProperty("tag_name").GetString() ?? "";
                int dash = tag.LastIndexOf("-v", StringComparison.Ordinal);
                if (dash <= 0 || !System.Version.TryParse(tag.Substring(dash + 2), out var v)) continue;
                var name = tag.Substring(0, dash);
                if (result.TryGetValue(name, out var have) && have.Version >= v) continue;
                var assets = rel.GetProperty("assets").EnumerateArray()
                    .ToDictionary(a => a.GetProperty("name").GetString(), a => a.GetProperty("browser_download_url").GetString());
                result[name] = new Release(v, assets);
            }
            return result;
        }

        /// <summary>Newer installer release, if any.</summary>
        public static Release NewerInstaller(Dictionary<string, Release> releases) =>
            releases.TryGetValue("Installer", out var r) && r.Version > new Version(Version) && r.Assets.ContainsKey("MimesisPatchesInstaller.exe") ? r : null;

        /// <summary>Downloads the newer installer next to this one and starts it with the given arguments.</summary>
        public static void LaunchNewer(Release r, IEnumerable<string> args)
        {
            Console.WriteLine($"Downloading installer v{r.Version.ToString(3)}...");
            var target = Path.Combine(AppContext.BaseDirectory, $"MimesisPatchesInstaller-v{r.Version.ToString(3)}.exe");
            File.WriteAllBytes(target, Http.GetByteArrayAsync(r.Assets["MimesisPatchesInstaller.exe"]).Result);
            var start = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var a in args.Append("--no-update")) start.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(start);
        }

        public static void Install(string dll, IEnumerable<PatchInfo> selected, Dictionary<string, Release> releases, Action<int, int> progress = null)
        {
            Log.CloseGame();
            // Patches from an older install move to MelonLoader too, not just the selected ones.
            var list = selected.Union(Migrate(dll)).ToList();
            EnsureMelonLoader(dll);
            var mods = ModsDir(dll);
            Directory.CreateDirectory(mods);
            RemoveRetired(dll);
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                progress?.Invoke(i, list.Count);
                if (!releases.TryGetValue(p.Name, out var r)) { Console.WriteLine($"== {p.Name}: not released yet, skipped\n"); continue; }
                Console.WriteLine($"== {p.Name} v{r.Version.ToString(3)}");
                File.Copy(Download(r, p.Dll, p.Name), Path.Combine(mods, p.Dll), true);
                Log.Success("   installed");
                Console.WriteLine();
            }
            progress?.Invoke(list.Count, list.Count);
        }

        // Patches that were replaced: their DLL is deleted on install and uninstall so the old code stops loading.
        static readonly Dictionary<string, string> Retired = new Dictionary<string, string>
        {
            ["MimicControl"] = "replaced by Hallucinations",
        };

        static void RemoveRetired(string dll)
        {
            foreach (var r in Retired)
            {
                var path = Path.Combine(ModsDir(dll), r.Key + "Runtime.dll");
                if (!File.Exists(path)) continue;
                File.Delete(path);
                Console.WriteLine($"== {r.Key}: removed ({r.Value})\n");
            }
        }

        /// <summary>Removes the given patches. MelonLoader stays (other mods may use it).</summary>
        public static void Uninstall(string dll, IEnumerable<PatchInfo> remove, Dictionary<string, Release> releases, Action<int, int> progress = null)
        {
            Log.CloseGame();
            RemoveRetired(dll);
            var removing = remove.ToList();
            var kept = Migrate(dll).Except(removing).ToList();   // older installs that stay: reinstall them as mods
            foreach (var p in removing)
            {
                var path = Path.Combine(ModsDir(dll), p.Dll);
                if (File.Exists(path)) File.Delete(path);
                Console.WriteLine($"== {p.Name}: removed");
            }
            Console.WriteLine();
            if (kept.Count > 0) Install(dll, kept, releases, progress);
            else progress?.Invoke(1, 1);
        }

        // Downloads MelonLoader and unpacks it into the game folder, unless it's already there.
        static void EnsureMelonLoader(string dll)
        {
            var game = GameDir(dll);
            if (File.Exists(Path.Combine(game, "version.dll")) && Directory.Exists(Path.Combine(game, "MelonLoader"))) return;
            Console.WriteLine($"== MelonLoader {MelonLoaderVersion} (mod loader, installed once)");
            var zip = Path.Combine(Path.GetTempPath(), "mimesis-patches", $"MelonLoader_{MelonLoaderVersion}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip));
            if (!File.Exists(zip))
            {
                Console.WriteLine("   downloading...");
                File.WriteAllBytes(zip, Http.GetByteArrayAsync(MelonLoaderUrl).Result);
            }
            ZipFile.ExtractToDirectory(zip, game, true);
            Log.Success("   installed");
            Console.WriteLine();
        }

        /// <summary>Cleans up older installs and returns the patches that were installed that way: installer 4 (BepInEx
        /// plugins; BepInEx itself goes too if nothing else uses it, since its loader would also start with the game)
        /// and installers 1-3 (startup hook in Assembly-CSharp.dll, DLLs in Managed).</summary>
        static List<PatchInfo> Migrate(string dll)
        {
            var found = new List<PatchInfo>();
            var game = GameDir(dll);

            var bep = BepInExDir(dll);
            if (Directory.Exists(bep))
            {
                Console.WriteLine("== Moving patches from BepInEx to MelonLoader");
                found.AddRange(Patches.Where(p => File.Exists(Path.Combine(bep, p.Dll))));
                Directory.Delete(bep, true);
                var plugins = Path.GetDirectoryName(bep);
                if (!Directory.EnumerateFileSystemEntries(plugins).Any())
                {
                    foreach (var f in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" })
                        if (File.Exists(Path.Combine(game, f))) File.Delete(Path.Combine(game, f));
                    Directory.Delete(Path.Combine(game, "BepInEx"), true);
                    Console.WriteLine("   BepInEx removed (nothing else used it)");
                }
                else Console.WriteLine("   BepInEx kept: other BepInEx mods are installed. Running two mod loaders may cause problems.");
                Console.WriteLine();
            }

            var managed = Path.GetDirectoryName(dll);
            var old = Patches.Where(p => File.Exists(Path.Combine(managed, p.Dll))).ToList();
            var bak = dll + ".bak";
            if (File.Exists(bak) || old.Count > 0)
            {
                Console.WriteLine("== Cleaning up the original patch install");
                if (File.Exists(bak))
                {
                    File.Copy(bak, dll, true);
                    File.Delete(bak);
                    Console.WriteLine("   original Assembly-CSharp.dll restored");
                }
                foreach (var p in old) File.Delete(Path.Combine(managed, p.Dll));
                var harmony = Path.Combine(managed, "0Harmony.dll");   // the game doesn't ship Harmony; it came with the patches
                if (File.Exists(harmony)) File.Delete(harmony);
                var icons = Path.Combine(Path.GetDirectoryName(managed), "PSIcons");
                if (Directory.Exists(icons)) Directory.Delete(icons, true);
                Console.WriteLine();
                found.AddRange(old);
            }
            return found.Distinct().ToList();
        }

        // Downloads a release file into a temp folder (cached per version).
        static string Download(Release r, string file, string name)
        {
            if (!r.Assets.TryGetValue(file, out var url)) throw new InvalidOperationException($"{name} v{r.Version.ToString(3)} release has no {file}.");
            var dir = Path.Combine(Path.GetTempPath(), "mimesis-patches", $"{name}-v{r.Version.ToString(3)}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"   downloading {file}...");
                File.WriteAllBytes(path, Http.GetByteArrayAsync(url).Result);
            }
            return path;
        }
    }

    /// <summary>Console output helpers and closing the game.</summary>
    static class Log
    {
        /// <summary>Set when running from the window or the in-game updater: never wait for input.</summary>
        public static bool Unattended;

        public static void CloseGame()
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("MIMESIS"))
            {
                try
                {
                    Console.WriteLine("MIMESIS is running; closing it so the patches can be installed...");
                    p.Kill();
                    p.WaitForExit(15000);
                }
                catch (Exception e) { Console.WriteLine($"Couldn't close MIMESIS ({e.Message}). Close it yourself and try again."); }
                finally { p.Dispose(); }
            }
        }

        public static void Success(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        public static void Fail(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("FAILED: " + msg);
            Console.ResetColor();
        }
    }
}
