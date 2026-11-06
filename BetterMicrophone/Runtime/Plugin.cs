// MIMESIS BetterMicrophone - cleaner, steadier voice for everyone who hears you (and for the mimics that copy you)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The game's voice chat (Dissonance) cleans your microphone with WebRTC only; it ships RNNoise, a neural noise remover,
// but leaves it off. This patch turns RNNoise on and wraps the whole thing in a voice chain of its own: low cut and
// optional hum filter before, then silence between words, even volume and a limiter after; and it sends your voice at
// a higher Opus bitrate. Only your outgoing voice changes, so only you need it; the mimics record what the others
// hear, so they copy the cleaner voice too.
//   Plugin.cs           startup and the Patches window page (settings, live meter, mic test)
//   Settings.cs         your settings
//   DissonanceSetup.cs  the game's own voice settings it changes (RNNoise, WebRTC level, quality)
//   Hooks.cs            the hooks into Dissonance's capture pipeline and encoder
//   Dsp.cs              the voice chain (filters, gate, auto gain, limiter)
//   MicTest.cs          record 5 s and hear it as captured and as cleaned

using System;
using System.Collections.Generic;
using HarmonyLib;
using MimesisPatches;
using TMPro;
using UnityEngine;

namespace BetterMicrophone
{
    public static class Plugin
    {
        static bool _initialized;
        internal static bool HooksReady;

        // Called once at the start of Hub.Awake (see Common/MelonEntry.cs).
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            var version = typeof(Plugin).Assembly.GetName().Version;
            try
            {
                Settings.Load();
                var harmony = new HarmonyLib.Harmony("medovanx.bettermicrophone");
                int failed = 0;
                foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
                {
                    try { harmony.CreateClassProcessor(type).Patch(); }
                    catch (Exception e)
                    {
                        failed++;
                        Log.Error($"{type.Name} not applied: {e}");
                    }
                }
                HooksReady = failed == 0;
                DissonanceSetup.Apply();

                var go = new GameObject("MedovanxBetterMicrophone");
                UnityEngine.Object.DontDestroyOnLoad(go);
                MicTest.Install(go);

                PatchMenu.Register("BetterMicrophone", version, status: Status, build: BuildPage);
                Log.Info($"v{version.ToString(3)}: {(failed == 0 ? "ready" : failed + " hook(s) failed, see above")}; " +
                         (Settings.Enabled ? $"on: noise removal {Settings.Strength}, quality {Settings.Quality}" : "off"));
            }
            catch (Exception e)
            {
                Log.Error("Failed to start: " + e);
            }
        }

        static string Status()
        {
            if (!HooksReady) return "Not working with this game version (see the log)";
            if (!Settings.Enabled) return "Off: your voice is as the game makes it";
            return $"On: noise removal {Settings.Strength}, {QualityText(Settings.Quality)}" + (Live.Broken ? " (chain failed, see the log)" : "");
        }

        static void BuildPage(RectTransform page, TMP_FontAsset font)
        {
            PatchUi.OnOff(page, font, "Better microphone", () => Settings.Enabled, v => Settings.Enabled = v);
            PatchUi.Label(page, font,
                "Cleans your voice before anyone hears it: the game's neural noise remover (RNNoise, off in the game) on top of " +
                "its own, rumble and hum filters, silence between your words, even volume, no clipping, and a higher voice " +
                "quality. Only you need this patch: it changes what you send. The mimics copy the cleaner voice too.",
                17f, PatchUi.Dim);

            PatchUi.Section(page, font, "Noise");
            PatchUi.Options(page, font, "Noise removal", 120f,
                ("Light", () => Settings.Strength == Strength.Light, () => Settings.Strength = Strength.Light),
                ("Medium", () => Settings.Strength == Strength.Medium, () => Settings.Strength = Strength.Medium),
                ("Strong", () => Settings.Strength == Strength.Strong, () => Settings.Strength = Strength.Strong),
                ("Max", () => Settings.Strength == Strength.Max, () => Settings.Strength = Strength.Max));
            PatchUi.Options(page, font, "Silence between words", 120f,
                ("Off", () => Settings.Gate == 0, () => Settings.Gate = 0),
                ("Light", () => Settings.Gate == 1, () => Settings.Gate = 1),
                ("Normal", () => Settings.Gate == 2, () => Settings.Gate = 2),
                ("Strong", () => Settings.Gate == 3, () => Settings.Gate = 3));
            PatchUi.Options(page, font, "Low cut", 120f,
                ("Off", () => Settings.LowCutHz == 0, () => Settings.LowCutHz = 0),
                ("60 Hz", () => Settings.LowCutHz == 60, () => Settings.LowCutHz = 60),
                ("90 Hz", () => Settings.LowCutHz == 90, () => Settings.LowCutHz = 90),
                ("120 Hz", () => Settings.LowCutHz == 120, () => Settings.LowCutHz = 120));
            PatchUi.Options(page, font, "Hum filter", 120f,
                ("Off", () => Settings.HumHz == 0, () => Settings.HumHz = 0),
                ("50 Hz", () => Settings.HumHz == 50, () => Settings.HumHz = 50),
                ("60 Hz", () => Settings.HumHz == 60, () => Settings.HumHz = 60));
            PatchUi.Label(page, font,
                "How deep the neural noise remover goes (the game's own suppression stays on top of it). Strong suits most " +
                "rooms; Max for loud fans or keyboards (it can dull a quiet voice). Silence between words lowers what's left " +
                "of the noise while you don't speak. Low cut removes rumble and desk knocks; the hum filter " +
                "electrical hum (50 Hz in Europe, Asia, Africa, most of South America; 60 Hz in North America, Brazil, Japan's west).",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Voice");
            PatchUi.OnOff(page, font, "Even out volume", () => Settings.AutoGain, v => Settings.AutoGain = v);
            PatchUi.OnOff(page, font, "Voice clarity", () => Settings.Clarity, v => Settings.Clarity = v);
            PatchUi.Slider(page, font, "Mic boost", Settings.BoostMin, Settings.BoostMax, () => Settings.BoostDb, v => Settings.BoostDb = v,
                " dB", "Before everything else.");
            PatchUi.Options(page, font, "Send quality", 170f,
                ("Game's own", () => Settings.Quality == SendQuality.Game, () => Settings.Quality = SendQuality.Game),
                ("High (24 kbps)", () => Settings.Quality == SendQuality.High, () => Settings.Quality = SendQuality.High),
                ("Very high (32)", () => Settings.Quality == SendQuality.VeryHigh, () => Settings.Quality = SendQuality.VeryHigh));
            PatchUi.Label(page, font,
                "Even out volume brings a quiet or loud mic to a steady level while you speak. Voice clarity is a light " +
                "presence boost. Send quality takes effect from your next game (it's read when voice starts).",
                15f, PatchUi.Dim);

            PatchUi.Section(page, font, "Test your microphone");
            var row = PatchUi.Row(page);
            PatchUi.Button(row, font, "Record 5 s", MicTest.Record, 170f);
            PatchUi.Button(row, font, "Play original", () => MicTest.Play(false), 170f);
            PatchUi.Button(row, font, "Play cleaned", () => MicTest.Play(true), 170f);
            PatchUi.Button(row, font, "Stop", MicTest.StopPlaying, 100f);
            var status = PatchUi.Label(page, font, "", 17f, PatchUi.Text, 48f);
            var meter = PatchUi.Label(page, font, "", 15f, PatchUi.Dim, 24f);
            var live = status.gameObject.AddComponent<LiveText>();
            live.Lines = new List<(TMP_Text, Func<string>)>
            {
                (status, () => string.IsNullOrEmpty(MicTest.Status) ? "Record a few seconds, then compare the two." : MicTest.Status),
                (meter, MeterText),
            };
        }

        static string MeterText()
        {
            if (!Live.Running) return "Live meter: your voice isn't running here (it does in a lobby or a game).";
            var m = Live.Meter;
            return $"Live: in {m.InDb:0} dB, out {m.OutDb:0} dB, {(m.Open ? "speaking" : "silent")}, auto gain {m.GainDb:+0;-0;0} dB, " +
                   $"limiter {m.LimitDb:0.0} dB, noise floor {m.FloorDb:0} dB, {m.CostMs:0.00} ms per 10 ms";
        }

        static string QualityText(SendQuality q) => q == SendQuality.VeryHigh ? "32 kbps" : q == SendQuality.High ? "24 kbps" : "the game's own quality";
    }

    /// <summary>Rewrites some labels of the page a few times a second (the page itself is built once).</summary>
    sealed class LiveText : MonoBehaviour
    {
        internal List<(TMP_Text label, Func<string> text)> Lines;
        float _next;

        void Update()
        {
            if (Lines == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.2f;
            foreach (var (label, text) in Lines)
            {
                try
                {
                    string s = text();
                    if (label != null && label.text != s) label.text = s;
                }
                catch (Exception) { /* display only */ }
            }
        }
    }

    static class Log
    {
        const string Tag = "[BetterMicrophone] ";
        static readonly HashSet<string> Once = new HashSet<string>();

        public static void Info(string message) => Debug.Log(Tag + message);
        public static void Warn(string message) => Debug.LogWarning(Tag + message);
        public static void Error(string message) => Debug.LogError(Tag + message);

        public static void WarnOnce(string key, string message)
        {
            lock (Once) if (!Once.Add(key)) return;
            Warn(message);
        }

        public static void ErrorOnce(string key, string message)
        {
            lock (Once) if (!Once.Add(key)) return;
            Error(message);
        }
    }
}
