// MIMESIS Deathmatch - a deathmatch destination on the tram (host and every player)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Now and then one of the tram's two destinations is a deathmatch (marked on the tram's screen). Take it or not, as any
// destination. There: everyone arrives somewhere else inside the building; no monsters, only mimics; everyone is lent a
// loaded double-barrel shotgun (taken back at the end); every death pays the team gold, and the dead lose everything
// they carried (what's in the tram is safe).
//   Plugin.cs    startup and the Patches window page
//   Settings.cs  the host's settings
//   Game.cs      the deathmatch on the host (spawns, shotguns, deaths, money)
//   Client.cs    the rules in the corner
//   Patches.cs   the game hooks (D0-D12 host, C1-C3 every player)
//   Common/TramMode.cs     the special destination on the tram's screen, messages and banners (shared with Traitor)
//   Common/TramScatter.cs  where everyone starts in the level (shared with Traitor)

using System;
using System.Collections.Generic;
using MimesisPatches;
using TMPro;
using UnityEngine;

namespace Deathmatch
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
                TramMode.Name = "Deathmatch";
                TramMode.Magic = "MDXDTM|";
                TramMode.ColorHex = "#ff8a1f";
                TramMode.ChancePct = () => HostReady ? Settings.ChancePct : 0;
                TramMode.MinPlayers = 2;
                TramMode.CountFakePlayers = () => Settings.FakePlayers;
                TramMode.LogInfo = Log.Info;
                TramMode.LevelStarted = room =>
                {
                    try { Game.OnLevelStarted(room); }
                    catch (Exception e) { Log.Error("Starting the deathmatch failed: " + e); }
                };
                TramMode.ClientMessage = Client.OnMessage;

                var harmony = new HarmonyLib.Harmony("medovanx.deathmatch");
                bool level = Apply(harmony, typeof(LevelPatches));
                bool rules = Apply(harmony, typeof(RulePatches));
                HostReady = level && rules;
                // Without it, everyone is moved to their spot as the level starts (TramScatter.MoveStragglers).
                if (!Apply(harmony, typeof(StartPatches))) Log.Warn("Players will start in the tram and be moved to their spot as the level starts");
                if (!Apply(harmony, typeof(PlayerPatches))) Log.Warn("A returning player's shotgun comes, and a lent one that got out goes, a moment after they load");
                TramScatter.Verbose = () => Settings.VerboseLog;
                ClientReady = Apply(harmony, typeof(ClientPatches));
                if (!Apply(harmony, typeof(IndoorPatches))) Log.Warn("Your game will check whether you start indoors a moment after you arrive");

                var go = new GameObject("MedovanxDeathmatch");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<RulesHud>();

                PatchMenu.Register("Deathmatch", version, status: Status, build: BuildPage);
                Log.Info($"v{version.ToString(3)}: host {(HostReady ? "ready" : "FAILED")}, player {(ClientReady ? "ready" : "FAILED")}; " +
                         $"deathmatch destinations {ChanceName(Settings.ChancePct).ToLowerInvariant()}");
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
            if (!HostReady) return "Your games: off (game update?). Others' deathmatches: on";
            return Settings.ChancePct == 0 ? "Your games: no deathmatch destinations" : $"Your games: deathmatch destinations {ChanceName(Settings.ChancePct).ToLowerInvariant()}";
        }

        static string ChanceName(int pct) => pct == 0 ? "Off" : pct <= 15 ? "Rare" : pct >= 100 ? "Always" : "Often";

        static void BuildPage(RectTransform page, TMP_FontAsset font)
        {
            PatchUi.Label(page, font,
                "Now and then one of the tram's two destinations is a deathmatch (marked DEATHMATCH on the tram's screen). Take " +
                "it or not, like any destination. There: everyone arrives alone somewhere inside the building; no monsters, only " +
                "mimics; everyone is lent a loaded double-barrel shotgun (it goes back at the end) " +
                "(2 shells); every death pays the team gold, but the dead lose everything they carried. What's in the tram is " +
                "safe. Everyone needs this patch to see the mark and the rules: the tram only offers a deathmatch, and only goes " +
                "there, when everyone in the game has it, in a version that can play with the host's. The host's settings count.",
                17f, PatchUi.Dim);

            PatchUi.Section(page, font, "Your games (host)");
            PatchUi.Options(page, font, "Deathmatch destinations", 120f,
                ("Off", () => Settings.ChancePct == 0, () => Settings.ChancePct = 0),
                ("Rare", () => Settings.ChancePct == 15, () => Settings.ChancePct = 15),
                ("Often", () => Settings.ChancePct == 35, () => Settings.ChancePct = 35),
                ("Always", () => Settings.ChancePct == 100, () => Settings.ChancePct = 100));
            PatchUi.Options(page, font, "Gold per death", 120f,
                ("Small", () => Settings.RewardPct == 10, () => Settings.RewardPct = 10),
                ("Normal", () => Settings.RewardPct == 15, () => Settings.RewardPct = 15),
                ("Big", () => Settings.RewardPct == 25, () => Settings.RewardPct = 25));
            PatchUi.Label(page, font,
                "Destinations: how often one of the two is a deathmatch (needs 2 players in the tram). Gold per death: a share " +
                "of the stage's repair cost (10, 15 or 25 %), paid to the team when a player dies in the deathmatch, once per " +
                "player (not again after a revive or a reconnect).",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Testing");
            PatchUi.OnOff(page, font, "Fake players take part", () => Settings.FakePlayers, v => Settings.FakePlayers = v);
            PatchUi.OnOff(page, font, "Detailed log", () => Settings.VerboseLog, v => Settings.VerboseLog = v);
        }
    }

    static class Log
    {
        const string Tag = "[Deathmatch] ";
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
