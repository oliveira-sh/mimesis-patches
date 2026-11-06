// MIMESIS BetterMimics - settings (the host's, saved in PlayerPrefs)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx

using UnityEngine;

namespace BetterMimics
{
    static class Settings
    {
        const string Prefix = "medovanx.BetterMimics.";

        static bool _loaded;
        static bool _enabled, _voice, _gaze, _loot, _emotes, _walk, _verbose;

        internal static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _enabled = Get("Enabled", true);
            _voice = Get("Voice", true);
            _gaze = Get("Gaze", true);
            _loot = Get("Loot", true);
            _emotes = Get("Emotes", true);
            _walk = Get("Walk", true);
            _verbose = Get("VerboseLog", false);
        }

        /// <summary>Everything below; off = the game's own mimics.</summary>
        public static bool Enabled { get => _enabled; set => Set("Enabled", ref _enabled, value); }
        /// <summary>One voice per mimic, no talking over people or next to the player it copies, replies that sound like
        /// replies, a human pause before answering.</summary>
        public static bool Voice { get => _voice; set => Set("Voice", ref _voice, value); }
        /// <summary>Standing mimics look at whoever talks, back at whoever stares, at the nearest player, or around.</summary>
        public static bool Gaze { get => _gaze; set => Set("Gaze", ref _gaze, value); }
        /// <summary>Wandering mimics pick up scrap lying around (never from the tram) and carry it.</summary>
        public static bool Loot { get => _loot; set => Set("Loot", ref _loot, value); }
        /// <summary>Mimics join a player's dance or emote, and now and then emote near players.</summary>
        public static bool Emotes { get => _emotes; set => Set("Emotes", ref _emotes, value); }
        /// <summary>Mimics walk around instead of switching between walking and running every few seconds.</summary>
        public static bool Walk { get => _walk; set => Set("Walk", ref _walk, value); }
        /// <summary>Logs every decision (for testing).</summary>
        public static bool VerboseLog { get => _verbose; set => Set("VerboseLog", ref _verbose, value); }

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
