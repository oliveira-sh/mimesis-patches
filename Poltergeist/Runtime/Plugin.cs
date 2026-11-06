// MIMESIS Poltergeist - the dungeons are haunted (each player's own; no host needed)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Random hauntings while you explore: teammates' voices whispering behind you, eerie sounds, lights flickering, a
// figure staring at you from the end of a corridor, something running past the edge of your view, and now and then
// something right behind you when you turn around. Harmless: nothing touches your health, radiation, items or the
// server, and nobody else sees your hauntings (they get their own).
//   Plugin.cs      startup and the Patches window page
//   Settings.cs    this player's settings
//   Haunt.cs       when something happens, and what
//   Flicker.cs     the lights (and P2, the shoulder light)
//   Apparition.cs  the figures (and P1, their animation events)
//   TestWindow.cs  the debug window (F8, with Debug mode on): any haunting at once

using System;
using System.Collections.Generic;
using MimesisPatches;
using TMPro;
using UnityEngine;

namespace Poltergeist
{
    public static class Plugin
    {
        static bool _initialized;
        internal static bool Ready;

        // Called once at the start of Hub.Awake (see Common/MelonEntry.cs).
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            var version = typeof(Plugin).Assembly.GetName().Version;
            try
            {
                Settings.Load();
                var harmony = new HarmonyLib.Harmony("medovanx.poltergeist");
                try
                {
                    harmony.CreateClassProcessor(typeof(ApparitionEventPatch)).Patch();
                    Ready = true;
                }
                catch (Exception e) { Log.Error("ApparitionEventPatch not applied (figures may log animation errors): " + e); }
                try { harmony.CreateClassProcessor(typeof(MountedLightPatch)).Patch(); }
                catch (Exception e) { Log.Error("MountedLightPatch not applied (a flashlight toggled during a flicker may stay dim): " + e); }

                var go = new GameObject("MedovanxPoltergeist");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<HauntDriver>();
                go.AddComponent<TestWindow>();

                PatchMenu.Register("Poltergeist", version, status: Status, build: BuildPage);
                Log.Info($"v{version.ToString(3)}: {(Settings.Enabled ? "on, " + IntensityName(Settings.Intensity).ToLowerInvariant() : "off")}");
            }
            catch (Exception e)
            {
                Log.Error("Failed to start: " + e);
            }
        }

        static string Status()
        {
            if (!Settings.Enabled) return "Off: the dungeons are quiet";
            return $"On: {IntensityName(Settings.Intensity).ToLowerInvariant()}, about every {Settings.MeanIntervalSec:0} s";
        }

        static string IntensityName(int i) => i == 0 ? "Calm" : i == 2 ? "Terror" : "Spooky";

        static void BuildPage(RectTransform page, TMP_FontAsset font)
        {
            PatchUi.OnOff(page, font, "Poltergeist", () => Settings.Enabled, v => Settings.Enabled = v);
            PatchUi.Label(page, font,
                "Random hauntings while you explore: whispers in your teammates' voices, eerie sounds, flickering lights, " +
                "figures in the distance and, now and then, something right behind you. Only you see and hear your own " +
                "hauntings, and none of them can hurt you or touch your items. Works in any game: no host needed.",
                17f, PatchUi.Dim);

            PatchUi.Section(page, font, "How often");
            PatchUi.Options(page, font, "Intensity", 140f,
                ("Calm", () => Settings.Intensity == 0, () => Settings.Intensity = 0),
                ("Spooky", () => Settings.Intensity == 1, () => Settings.Intensity = 1),
                ("Terror", () => Settings.Intensity == 2, () => Settings.Intensity = 2));
            PatchUi.Label(page, font, "About every 2.5 minutes, every 1.5 minutes, or every 50 seconds. Never while a monster is close.", 15f, PatchUi.Dim);

            PatchUi.Section(page, font, "What can happen");
            PatchUi.OnOff(page, font, "Whispers and sounds", () => Settings.Sounds, v => Settings.Sounds = v);
            PatchUi.OnOff(page, font, "Flickering lights", () => Settings.Flicker, v => Settings.Flicker = v);
            PatchUi.OnOff(page, font, "Figures in the distance", () => Settings.Apparitions, v => Settings.Apparitions = v);
            PatchUi.OnOff(page, font, "Jumpscares", () => Settings.Jumpscares, v => Settings.Jumpscares = v);
            PatchUi.Label(page, font,
                "Figures: one stares at you from far away and vanishes when you look straight at it, one runs past the edge " +
                "of your view. Jumpscares: something waits right behind you and lunges when you turn around.",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Testing");
            PatchUi.OnOff(page, font, "Debug mode", () => Settings.Debug, v => Settings.Debug = v);
            PatchUi.OnOff(page, font, "Detailed log", () => Settings.VerboseLog, v => Settings.VerboseLog = v);
            PatchUi.Label(page, font,
                "Debug mode: F8 in a level opens a window to start any haunting at once (whispers, sounds, lights, figures, the " +
                "jumpscare), to see and hear them without waiting.", 15f, PatchUi.Dim);
        }
    }

    static class Log
    {
        const string Tag = "[Poltergeist] ";
        static readonly HashSet<string> Once = new HashSet<string>();

        public static void Info(string message) => Debug.Log(Tag + message);
        public static void Warn(string message) => Debug.LogWarning(Tag + message);
        public static void Error(string message) => Debug.LogError(Tag + message);

        public static void WarnOnce(string key, string message)
        {
            if (Once.Add(key)) Warn(message);
        }

        public static void ErrorOnce(string key, string message)
        {
            if (Once.Add(key)) Error(message);
        }
    }
}
