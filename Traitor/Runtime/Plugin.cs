// MIMESIS Traitor - a traitor destination on the tram (host and every player)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Now and then one of the tram's two destinations is a traitor job (marked on the tram's screen). Take it or not, as
// any destination. If you go: everyone arrives somewhere else inside the building, one of you is secretly the traitor,
// and monsters ignore them. Catch the traitor for gold; the traitor tries to get away with it.
//   Plugin.cs    startup and the Patches window page
//   Settings.cs  the host's settings
//   Game.cs      the job on the host (the traitor, monsters ignoring them, the lever lock, the end)
//   Client.cs    your role on screen
//   Patches.cs   the game hooks (T0-T14 host, C1-C3 every player)
//   Common/TramMode.cs     the special destination on the tram's screen, messages and banners (shared with Deathmatch)
//   Common/TramScatter.cs  where everyone starts in the level (shared with Deathmatch)

using System;
using System.Collections.Generic;
using MimesisPatches;
using TMPro;
using UnityEngine;

namespace Traitor
{
    public static class Plugin
    {
        static bool _initialized;
        internal static bool HostReady, ClientReady;

        // Called once at the start of Hub.Awake (see Common/MelonEntry.cs).
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            var version = typeof(Plugin).Assembly.GetName().Version;
            try
            {
                Settings.Load();
                TramMode.Name = "Traitor";
                TramMode.Magic = "MDXTRT|";
                TramMode.ColorHex = "#ff3b3b";
                TramMode.ChancePct = () => HostReady ? Settings.ChancePct : 0;
                TramMode.MinPlayers = 3;
                TramMode.CountFakePlayers = () => Settings.FakePlayers;
                TramMode.LogInfo = Log.Info;
                TramMode.LevelStarted = room =>
                {
                    try { Game.OnLevelStarted(room); }
                    catch (Exception e) { Log.Error("Starting the traitor job failed: " + e); }
                };
                TramMode.ClientMessage = Client.OnMessage;

                var harmony = new HarmonyLib.Harmony("medovanx.traitor");
                bool level = Apply(harmony, typeof(LevelPatches));
                bool ignore = Apply(harmony, typeof(IgnorePatches));
                bool job = Apply(harmony, typeof(JobPatches));
                // Without the monster part a traitor would be no different from anyone: no traitor jobs at all then.
                HostReady = level && ignore && job;
                // Without it, everyone is moved to their spot as the level starts (TramScatter.MoveStragglers).
                if (!Apply(harmony, typeof(StartPatches))) Log.Warn("Players will start in the tram and be moved to their spot as the level starts");
                TramScatter.Verbose = () => Settings.VerboseLog;
                TramScatter.MinTravellers = TramMode.MinPlayers;   // with fewer, no traitor job: a normal start in the tram
                ClientReady = Apply(harmony, typeof(ClientPatches));
                if (!Apply(harmony, typeof(IndoorPatches))) Log.Warn("Your game will check whether you start indoors a moment after you arrive");

                var go = new GameObject("MedovanxTraitor");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<RoleHud>();

                PatchMenu.Register("Traitor", version, status: Status, build: BuildPage);
                Log.Info($"v{version.ToString(3)}: host {(HostReady ? "ready" : "FAILED")}, player {(ClientReady ? "ready" : "FAILED")}; " +
                         $"traitor destinations {ChanceName(Settings.ChancePct).ToLowerInvariant()}");
            }
            catch (Exception e)
            {
                Log.Error("Failed to start: " + e);
            }
        }

        static bool Apply(HarmonyLib.Harmony harmony, Type type)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"{type.Name} not applied: {e}");
                return false;
            }
        }

        static string Status()
        {
            if (!HostReady && !ClientReady) return "Not working with this game version (see the log)";
            if (!HostReady) return "Your games: off (game update?). Others' traitor jobs: on";
            return Settings.ChancePct == 0 ? "Your games: no traitor destinations" : $"Your games: traitor destinations {ChanceName(Settings.ChancePct).ToLowerInvariant()}";
        }

        static string ChanceName(int pct) => pct == 0 ? "Off" : pct <= 15 ? "Rare" : pct >= 100 ? "Always" : "Often";

        static void BuildPage(RectTransform page, TMP_FontAsset font)
        {
            PatchUi.Label(page, font,
                "Now and then one of the tram's two destinations is a traitor job (marked TRAITOR on the tram's screen). Take " +
                "it or not, like any destination. There, everyone arrives alone somewhere inside the building, one player is secretly " +
                "the traitor and monsters ignore them. Kill the traitor for gold; the traitor wins if the crew dies. Everyone " +
                "needs this patch (to see the mark and their role): the tram only offers a traitor job, and only goes there, " +
                "when everyone in the game has it, in a version that can play with the host's. The host's settings count.",
                17f, PatchUi.Dim);

            PatchUi.Section(page, font, "Your games (host)");
            PatchUi.Options(page, font, "Traitor destinations", 120f,
                ("Off", () => Settings.ChancePct == 0, () => Settings.ChancePct = 0),
                ("Rare", () => Settings.ChancePct == 15, () => Settings.ChancePct = 15),
                ("Often", () => Settings.ChancePct == 35, () => Settings.ChancePct = 35),
                ("Always", () => Settings.ChancePct == 100, () => Settings.ChancePct = 100));
            PatchUi.Options(page, font, "Tram locked at the start", 120f,
                ("No", () => Settings.LockSec == 0, () => Settings.LockSec = 0),
                ("1:30", () => Settings.LockSec == 90, () => Settings.LockSec = 90),
                ("2:30", () => Settings.LockSec == 150, () => Settings.LockSec = 150),
                ("4:00", () => Settings.LockSec == 240, () => Settings.LockSec = 240));
            PatchUi.Options(page, font, "Reward for the traitor", 120f,
                ("Small", () => Settings.RewardPct == 15, () => Settings.RewardPct = 15),
                ("Normal", () => Settings.RewardPct == 25, () => Settings.RewardPct = 25),
                ("Big", () => Settings.RewardPct == 40, () => Settings.RewardPct = 40));
            PatchUi.Label(page, font,
                "Destinations: how often one of the two is a traitor job (needs 3 players in the tram). Lock: the tram can't " +
                "leave this long after everyone is dropped off, so the traitor can't just leave everyone behind. Reward: the " +
                "team's gold for catching the traitor, as a share of the stage's repair cost (15, 25 or 40 %).",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Testing");
            PatchUi.OnOff(page, font, "Fake players take part", () => Settings.FakePlayers, v => Settings.FakePlayers = v);
            PatchUi.OnOff(page, font, "Detailed log", () => Settings.VerboseLog, v => Settings.VerboseLog = v);
        }
    }

    static class Log
    {
        const string Tag = "[Traitor] ";
        static readonly HashSet<string> Once = new HashSet<string>();

        public static void Info(string message) => Debug.Log(Tag + message);
        public static void Warn(string message) => Debug.LogWarning(Tag + message);
        public static void Error(string message) => Debug.LogError(Tag + message);

        public static void ErrorOnce(string key, string message)
        {
            if (Once.Add(key)) Error(message);
        }
    }
}
