// MIMESIS Deathmatch - settings (the host's, saved in PlayerPrefs)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx

using UnityEngine;

namespace Deathmatch
{
    static class Settings
    {
        const string Prefix = "medovanx.Deathmatch.";

        static bool _loaded;
        static int _chance, _reward;
        static bool _fakePlayers, _verbose;

        internal static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _chance = Pick(PlayerPrefs.GetInt(Prefix + "Chance", 15), 15, 0, 15, 35, 100);
            _reward = Pick(PlayerPrefs.GetInt(Prefix + "RewardPct", 15), 15, 10, 15, 25);
            _fakePlayers = PlayerPrefs.GetInt(Prefix + "FakePlayers", 0) == 1;
            _verbose = PlayerPrefs.GetInt(Prefix + "VerboseLog", 0) == 1;
        }

        /// <summary>Percent chance the tram offers a deathmatch destination at each roll (0 = never, 100 = always).</summary>
        public static int ChancePct { get => _chance; set => SetInt("Chance", ref _chance, value); }
        /// <summary>Each death pays the team this percent of the stage's repair cost.</summary>
        public static int RewardPct { get => _reward; set => SetInt("RewardPct", ref _reward, value); }
        /// <summary>Testing: FakePlayers' fake players take part (count as players, get a shotgun).</summary>
        public static bool FakePlayers { get => _fakePlayers; set => SetBool("FakePlayers", ref _fakePlayers, value); }
        public static bool VerboseLog { get => _verbose; set => SetBool("VerboseLog", ref _verbose, value); }

        static int Pick(int v, int fallback, params int[] allowed)
        {
            foreach (int a in allowed) if (a == v) return v;
            return fallback;
        }

        static void SetInt(string key, ref int field, int value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(Prefix + key, value);
            PlayerPrefs.Save();
        }

        static void SetBool(string key, ref bool field, bool value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(Prefix + key, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
