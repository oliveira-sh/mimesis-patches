// MIMESIS FakePlayers - fake players for testing, on your own (off by default)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// While you host, F7 opens a panel that adds the game's own dummy players to your level and changes their state:
// alive or dead, health, radiation, where they are (next to you, or following you), talking. Everyone in your game
// sees them as players, so other patches can be tried without a second person: Revive (revive a dead one), the
// spectator camera and its lists, BiggerLobby's 10-player UI, HudPercent, Hallucinations (with its Debug keys on, the
// host's game plays a fake player's side, so a dead host can steer a hallucination at one).
//   Plugin.cs  startup, the Patches window page, and the one game hook (F1)
//   Panel.cs   the in-game panel (F7)
//   Bots.cs    the host's server side: spawning and changing fake players
// Everything else goes through the game's own server calls, and nothing runs while it's off.

using System;
using HarmonyLib;
using MimesisPatches;
using UnityEngine;

namespace FakePlayers
{
    /// <summary>F1: a player who joins a level after it started is killed 3 s later (the game's "barge-in" rule,
    /// IVroom.ScheduleBargeInKill, from DungeonRoom.OnLevelLoadComplete). A fake player always joins that way: without
    /// this it would die 3 s after being added to a level. Real players are untouched.</summary>
    [HarmonyPatch(typeof(IVroom), nameof(IVroom.ScheduleBargeInKill))]
    static class BargeInPatch
    {
        static bool Prefix(VPlayer player) => player == null || !player.IsDummy;
    }

    public static class Plugin
    {
        const string EnabledKey = "medovanx.FakePlayers.Enabled";
        static bool _initialized;

        /// <summary>Off by default: a testing tool, not for normal games.</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
                if (!value) Panel.CloseIfOpen();
            }
        }

        // Called once at the start of Hub.Awake (see Common/MelonEntry.cs).
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            var version = typeof(Plugin).Assembly.GetName().Version;
            try
            {
                try { new HarmonyLib.Harmony("medovanx.fakeplayers").CreateClassProcessor(typeof(BargeInPatch)).Patch(); }
                catch (Exception e) { Debug.LogError("[FakePlayers] BargeInPatch not applied (fake players added in a level die after 3 s): " + e); }
                Panel.Install();
                PatchMenu.Register("FakePlayers", version,
                    status: () => Enabled ? "On: F7 opens the panel while you host" : "Off",
                    build: (page, font) =>
                    {
                        PatchUi.OnOff(page, font, "Fake players", () => Enabled, v => Enabled = v);
                        PatchUi.Label(page, font,
                            "A testing tool: while you host, F7 opens a panel that adds fake players to your level (the game's own " +
                            "dummy players) and changes them: kill or revive, health, radiation, bring them over or have them " +
                            "follow you, make them talk. Also: kill or revive yourself, set your own radiation.",
                            18f, PatchUi.Dim);
                        PatchUi.Label(page, font,
                            "Everyone in your game sees them as players. They stay behind when the team changes level. Only the " +
                            "host needs this patch. Leave it off for normal games.",
                            16f, PatchUi.Dim);
                    });
                Debug.Log($"[FakePlayers] v{version.ToString(3)}: loaded ({(Enabled ? "on: F7 while hosting" : "off")})");
            }
            catch (Exception e)
            {
                Debug.LogError("[FakePlayers] Failed to start: " + e);
            }
        }
    }
}
