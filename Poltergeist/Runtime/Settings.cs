// MIMESIS Poltergeist - settings (this player's own, saved in PlayerPrefs)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx

using UnityEngine;

namespace Poltergeist
{
    static class Settings
    {
        const string Prefix = "medovanx.Poltergeist.";

        static bool _loaded;
        static bool _enabled, _jumpscares, _apparitions, _flicker, _sounds, _verbose, _debug;
        static int _intensity;

        internal static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _enabled = Get("Enabled", true);
            _intensity = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Intensity", 1), 0, 2);
            _jumpscares = Get("Jumpscares", true);
            _apparitions = Get("Apparitions", true);
            _flicker = Get("Flicker", true);
            _sounds = Get("Sounds", true);
            _verbose = Get("VerboseLog", false);
            _debug = Get("Debug", false);
        }

        public static bool Enabled { get => _enabled; set => Set("Enabled", ref _enabled, value); }
        /// <summary>0 calm, 1 spooky, 2 terror: how often something happens.</summary>
        public static int Intensity
        {
            get => _intensity;
            set
            {
                value = Mathf.Clamp(value, 0, 2);
                if (_intensity == value) return;
                _intensity = value;
                PlayerPrefs.SetInt(Prefix + "Intensity", value);
                PlayerPrefs.Save();
            }
        }
        public static bool Jumpscares { get => _jumpscares; set => Set("Jumpscares", ref _jumpscares, value); }
        public static bool Apparitions { get => _apparitions; set => Set("Apparitions", ref _apparitions, value); }
        public static bool Flicker { get => _flicker; set => Set("Flicker", ref _flicker, value); }
        public static bool Sounds { get => _sounds; set => Set("Sounds", ref _sounds, value); }
        public static bool VerboseLog { get => _verbose; set => Set("VerboseLog", ref _verbose, value); }
        /// <summary>Debug mode: F8 in a level opens the debug window (TestWindow), to start any haunting at once.</summary>
        public static bool Debug { get => _debug; set => Set("Debug", ref _debug, value); }

        /// <summary>Average seconds between two hauntings.</summary>
        internal static float MeanIntervalSec => _intensity == 0 ? 150f : _intensity == 2 ? 50f : 90f;

        static bool Get(string key, bool fallback) => PlayerPrefs.GetInt(Prefix + key, fallback ? 1 : 0) == 1;

        static void Set(string key, ref bool field, bool value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(Prefix + key, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
