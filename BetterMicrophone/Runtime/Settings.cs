// MIMESIS BetterMicrophone - settings (this player's own, saved in PlayerPrefs)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every change makes a new Config snapshot for the audio thread and re-applies the Dissonance settings (DissonanceSetup).

using UnityEngine;

namespace BetterMicrophone
{
    /// <summary>How hard noise is removed: RNNoise's suppression depth and the WebRTC noise suppression level with it.</summary>
    enum Strength { Light = 0, Medium = 1, Strong = 2, Max = 3 }

    /// <summary>The Opus bitrate of your voice: the game's own (it ships Low, 10 kbps), High (24) or Very high (32).</summary>
    enum SendQuality { Game = 0, High = 1, VeryHigh = 2 }

    static class Settings
    {
        const string Prefix = "medovanx.BetterMicrophone.";

        static bool _loaded;
        static bool _enabled, _autoGain, _clarity;
        static int _strength, _gate, _lowCut, _hum, _boost, _quality;

        /// <summary>The audio thread's snapshot (replaced whole on every change).</summary>
        internal static volatile Config Current = new Config();

        internal static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _enabled = PlayerPrefs.GetInt(Prefix + "Enabled", 1) == 1;
            _strength = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Strength", (int)Strength.Strong), 0, 3);
            _gate = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Gate", 2), 0, 3);
            _autoGain = PlayerPrefs.GetInt(Prefix + "AutoGain", 1) == 1;
            _lowCut = PlayerPrefs.GetInt(Prefix + "LowCut", 90);
            if (_lowCut != 0 && _lowCut != 60 && _lowCut != 90 && _lowCut != 120) _lowCut = 90;
            _hum = PlayerPrefs.GetInt(Prefix + "Hum", 0);
            if (_hum != 0 && _hum != 50 && _hum != 60) _hum = 0;
            _clarity = PlayerPrefs.GetInt(Prefix + "Clarity", 0) == 1;
            _boost = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Boost", 0), BoostMin, BoostMax);
            _quality = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Quality", (int)SendQuality.High), 0, 2);
            Rebuild();
        }

        public const int BoostMin = -12, BoostMax = 24;

        public static bool Enabled { get => _enabled; set => SetInt("Enabled", ref _enabled, value); }
        public static Strength Strength { get => (Strength)_strength; set => SetInt("Strength", ref _strength, (int)value); }
        /// <summary>Silence between words: 0 off, 1 light (-12 dB), 2 normal (-24 dB), 3 strong (-40 dB).</summary>
        public static int Gate { get => _gate; set => SetInt("Gate", ref _gate, Mathf.Clamp(value, 0, 3)); }
        public static bool AutoGain { get => _autoGain; set => SetInt("AutoGain", ref _autoGain, value); }
        public static int LowCutHz { get => _lowCut; set => SetInt("LowCut", ref _lowCut, value); }
        public static int HumHz { get => _hum; set => SetInt("Hum", ref _hum, value); }
        public static bool Clarity { get => _clarity; set => SetInt("Clarity", ref _clarity, value); }
        public static int BoostDb { get => _boost; set => SetInt("Boost", ref _boost, Mathf.Clamp(value, BoostMin, BoostMax)); }
        public static SendQuality Quality { get => (SendQuality)_quality; set => SetInt("Quality", ref _quality, (int)value); }

        /// <summary>RNNoise's removal amount (Dissonance: 0 = none, 1 = up to -80 dB per band).</summary>
        internal static float RnnoiseAmount
        {
            get
            {
                switch (Strength)
                {
                    case Strength.Light: return 0.5f;
                    case Strength.Medium: return 0.75f;
                    case Strength.Strong: return 0.9f;
                    default: return 1f;
                }
            }
        }

        /// <summary>The bitrate to send at (bits/s); 0 = leave the game's.</summary>
        internal static int Bitrate => Quality == SendQuality.High ? 24000 : Quality == SendQuality.VeryHigh ? 32000 : 0;

        internal static float GateRangeDb => _gate == 1 ? -12f : _gate == 2 ? -24f : _gate == 3 ? -40f : 0f;

        static void SetInt(string key, ref int field, int value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(Prefix + key, value);
            PlayerPrefs.Save();
            Changed();
        }

        static void SetInt(string key, ref bool field, bool value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(Prefix + key, value ? 1 : 0);
            PlayerPrefs.Save();
            Changed();
        }

        static void Changed()
        {
            Rebuild();
            DissonanceSetup.Apply();
        }

        static void Rebuild()
        {
            Current = new Config
            {
                Enabled = _enabled,
                BoostDb = _boost,
                LowCutHz = _lowCut,
                HumHz = _hum,
                Clarity = _clarity,
                GateRangeDb = GateRangeDb,
                AutoGain = _autoGain,
            };
        }
    }
}
