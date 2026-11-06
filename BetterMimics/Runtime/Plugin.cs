// MIMESIS BetterMimics - mimics that are harder to tell from your friends (host only)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// How the game's mimics work (reverse-engineered from EA 0.4.1), and what this patch builds on:
//   - Each mimic runs a behavior tree on the host (StreamingAssets/aidata/bt/npc_mimic_alpha_base_*.xml and
//     npc_mimic_beta_base_*.xml: the two kinds levels spawn): "passive" wanders between random points, "search" walks
//     to the nearest player, "checkfriendly" and "bewarefriendly" hand the body to a neural movement model trained on
//     real players (DLMovementAgent, Unity Sentis) and a decision agent (DLDecisionAgent: when to emote back, attack,
//     follow, drop scrap as bait...), "combat"/"run"/... attack and flee.
//   - The game learns each player as it plays (DLPlayerPersonaManager): how often they talk, how long they look at a
//     mimic before swinging at it. Mimics bound to a persona copy those numbers.
//   - Voices: every player's game records what they say with the situation (SpeechEvent + its game data: players
//     around, area, game time, monsters, scrap...). The host picks a mimic's line by matching situations
//     (MimicVoiceSpawner, SpeechEventAdditionalGameData.PickBestMatch) and plays it from the mimic on every game.
// This patch adds to that, never replacing the behavior tree or the model (Brain.cs, Voice.cs, Patches.cs):
//   Brain.cs    where a standing mimic looks, the scrap it picks up, the dances it joins, its stops and its walk
//   Voice.cs    one voice per mimic, quiet next to the player it copies, turn-taking, reply-like answers, timing
//   Patches.cs  the game hooks (B1-B5 body, V1-V7 voice), one class each
// Everything runs on the host: only the host needs it, and every player (modded or not) sees and hears the result.

using System;
using System.Collections.Generic;
using HarmonyLib;
using MimesisPatches;
using TMPro;
using UnityEngine;

namespace BetterMimics
{
    public static class Plugin
    {
        static bool _initialized;
        /// <summary>Features whose hooks all went in (each hook is applied on its own: a game update that changes one
        /// target turns off only what needs it). Body: gaze, emotes, stops. Loot: also both scrap guards (never pick up
        /// scrap the game could then delete). Walk. Voice: the one-voice lock with the hooks that keep it right.</summary>
        internal static bool BodyReady, LootReady, WalkReady, VoiceReady;

        // Called once at the start of Hub.Awake (see Common/MelonEntry.cs).
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            var version = typeof(Plugin).Assembly.GetName().Version;
            try
            {
                Settings.Load();
                var harmony = new HarmonyLib.Harmony("medovanx.bettermimics");
                bool tick = Apply(harmony, typeof(TickHook)), reset = Apply(harmony, typeof(LevelResetHook));
                bool copyGuard = Apply(harmony, typeof(CopyGuardHook)), fakeGuard = Apply(harmony, typeof(FakeItemGuardHook));
                WalkReady = Apply(harmony, typeof(WalkHook));
                bool archives = Apply(harmony, typeof(ArchivesHook)), radio = Apply(harmony, typeof(RadioPickHook)), persona = Apply(harmony, typeof(PersonaHook));
                // Independent voice touches: each works (or not) on its own.
                bool style = Apply(harmony, typeof(LineTagHook)) & Apply(harmony, typeof(PickHook)) &
                             Apply(harmony, typeof(PrepareHook)) & Apply(harmony, typeof(ReplyDelayHook));
                BodyReady = tick && reset;
                LootReady = BodyReady && copyGuard && fakeGuard;
                VoiceReady = archives && radio && persona && reset;
                PatchMenu.Register("BetterMimics", version, status: Status, build: BuildPage);
                Log.Info($"v{version.ToString(3)}: body {Ok(BodyReady)}, scrap {Ok(LootReady)}, walk {Ok(WalkReady)}, voice {Ok(VoiceReady)}" +
                         $"{(style ? "" : " (some voice touches FAILED)")}; {(Settings.Enabled ? "on" : "off")}");
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

        static string Ok(bool ready) => ready ? "ready" : "FAILED";

        static string Status()
        {
            if (!BodyReady && !VoiceReady && !WalkReady) return "Not working with this game version (see the log)";
            if (!Settings.Enabled) return "Off: the game's own mimics";
            var parts = new List<string>();
            if (Settings.Voice && VoiceReady) parts.Add("voice");
            if (Settings.Gaze && BodyReady) parts.Add("gaze");
            if (Settings.Loot && LootReady) parts.Add("scrap");
            if (Settings.Emotes && BodyReady) parts.Add("emotes");
            if (Settings.Walk && WalkReady) parts.Add("walk");
            return "On in your games: " + (parts.Count == 0 ? "nothing selected" : string.Join(", ", parts));
        }

        static void BuildPage(RectTransform page, TMP_FontAsset font)
        {
            PatchUi.OnOff(page, font, "Better mimics", () => Settings.Enabled, v => Settings.Enabled = v);
            PatchUi.Label(page, font,
                "Mimics in your games act more like the people they copy. Only the host needs this patch: everyone sees and " +
                "hears the difference, modded or not. Mimics stay exactly as dangerous: nothing about attacks changes.",
                17f, PatchUi.Dim);

            PatchUi.Section(page, font, "Voice");
            PatchUi.OnOff(page, font, "Smarter voice", () => Settings.Voice, v => Settings.Voice = v);
            PatchUi.Label(page, font,
                "Each mimic keeps one person's voice for the whole level (the game mixed everyone's), stays quiet while that " +
                "person is near it, waits for others to finish talking, answers after a human pause, and answers with lines " +
                "people said as answers.",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Body");
            PatchUi.OnOff(page, font, "Natural gaze", () => Settings.Gaze, v => Settings.Gaze = v);
            PatchUi.OnOff(page, font, "Picks up scrap", () => Settings.Loot, v => Settings.Loot = v);
            PatchUi.OnOff(page, font, "Dances and emotes", () => Settings.Emotes, v => Settings.Emotes = v);
            PatchUi.OnOff(page, font, "Walks like a player", () => Settings.Walk, v => Settings.Walk = v);
            PatchUi.Label(page, font,
                "Gaze: a standing mimic looks at whoever talks, back at whoever stares at it, at the nearest player, glances " +
                "away and looks around, instead of staring at nothing. Scrap: a wandering mimic picks up scrap lying around " +
                "(never from the tram) and puts it down a minute or two later; it drops it if killed. Emotes: it joins a " +
                "player's dance and emotes now and then. Walk: it walks around instead of switching to a run every few " +
                "seconds; it still runs to chase and on long trips.",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Testing");
            PatchUi.OnOff(page, font, "Detailed log", () => Settings.VerboseLog, v => Settings.VerboseLog = v);
        }
    }

    static class Log
    {
        const string Tag = "[BetterMimics] ";
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
