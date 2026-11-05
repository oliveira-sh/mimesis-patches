// MIMESIS MimicControl - host settings and the lobby data that carries them to the players
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The host's choices (Patches window) are saved in PlayerPrefs and published in the Steam lobby data under
// Const.LobbyKey, like Inventory's slot count. A possessor's game reads them from there to know whether the host
// runs MimicControl at all (no key = vanilla host = vanilla possession) and with which rules. The host enforces
// every rule itself; players only use them to show the right hints.
// Format: "v=1;on=1;cd=2000;str=50;one=1;dl=0;self=0" (protocol version, enabled, attack cooldown ms, attack
// strength %, one mimic per death, delay after death ms, block after self-inflicted death). Unknown keys are ignored.

using System;
using System.Globalization;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace MimicControl
{
    static class Settings
    {
        const string Prefix = "medovanx.MimicControl.";

        public const int CooldownMin = 0, CooldownMax = 15, CooldownDefault = 2;      // seconds
        public const int StrengthMin = 0, StrengthMax = 200, StrengthDefault = 50;    // percent of a normal mimic's grab
        public const int DelayMin = 0, DelayMax = 60, DelayDefault = 0;               // seconds

        static bool _loaded;
        static bool _enabled, _onePerDeath, _blockSelf, _verbose;
        static int _cooldown, _strength, _delay;

        /// <summary>Reads PlayerPrefs once (Plugin.Init); the setters keep the cache and PlayerPrefs in step.</summary>
        public static void Load()
        {
            _enabled = PlayerPrefs.GetInt(Prefix + "Enabled", 1) == 1;
            _cooldown = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "AttackCooldown", CooldownDefault), CooldownMin, CooldownMax);
            _strength = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "AttackStrength", StrengthDefault), StrengthMin, StrengthMax);
            _onePerDeath = PlayerPrefs.GetInt(Prefix + "OnePerDeath", 1) == 1;
            _delay = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "DeathDelay", DelayDefault), DelayMin, DelayMax);
            _blockSelf = PlayerPrefs.GetInt(Prefix + "BlockSelfInflicted", 0) == 1;
            _verbose = PlayerPrefs.GetInt(Prefix + "VerboseLog", 0) == 1;
            _loaded = true;
        }

        static void EnsureLoaded() { if (!_loaded) Load(); }

        static void Save(string key, int value, bool publish = true)
        {
            PlayerPrefs.SetInt(Prefix + key, value);
            PlayerPrefs.Save();
            if (publish) Publish();
        }

        // ---------------- the host's settings (used when you host) ----------------

        /// <summary>Dead players can take control of mimics (host).</summary>
        public static bool Enabled
        {
            get { EnsureLoaded(); return _enabled; }
            set { EnsureLoaded(); _enabled = value; Save("Enabled", value ? 1 : 0); }
        }

        /// <summary>Seconds after a controlled grab ends before the next one.</summary>
        public static int AttackCooldownSec
        {
            get { EnsureLoaded(); return _cooldown; }
            set { EnsureLoaded(); _cooldown = Mathf.Clamp(value, CooldownMin, CooldownMax); Save("AttackCooldown", _cooldown); }
        }

        /// <summary>Contamination a controlled mimic's grab adds, in percent of a normal mimic's grab.</summary>
        public static int AttackStrengthPct
        {
            get { EnsureLoaded(); return _strength; }
            set { EnsureLoaded(); _strength = Mathf.Clamp(value, StrengthMin, StrengthMax); Save("AttackStrength", _strength); }
        }

        /// <summary>After their controlled mimic dies or they let go, a player can't take another until they die again.</summary>
        public static bool OnePerDeath
        {
            get { EnsureLoaded(); return _onePerDeath; }
            set { EnsureLoaded(); _onePerDeath = value; Save("OnePerDeath", value ? 1 : 0); }
        }

        /// <summary>Seconds after dying before a player can take a mimic.</summary>
        public static int DeathDelaySec
        {
            get { EnsureLoaded(); return _delay; }
            set { EnsureLoaded(); _delay = Mathf.Clamp(value, DelayMin, DelayMax); Save("DeathDelay", _delay); }
        }

        /// <summary>No mimic for a player whose death was self-inflicted (HostControl.Classify: falls, pits, own explosives,
        /// contamination nobody else caused).</summary>
        public static bool BlockSelfInflicted
        {
            get { EnsureLoaded(); return _blockSelf; }
            set { EnsureLoaded(); _blockSelf = value; Save("BlockSelfInflicted", value ? 1 : 0); }
        }

        /// <summary>Detailed log (this game only, not published): per-packet and per-step lines for bug reports.</summary>
        public static bool VerboseLog
        {
            get { EnsureLoaded(); return _verbose; }
            set { EnsureLoaded(); _verbose = value; Save("VerboseLog", value ? 1 : 0, publish: false); }
        }

        public static long AttackCooldownMs => AttackCooldownSec * 1000L;
        public static long DeathDelayMs => DeathDelaySec * 1000L;

        // ---------------- lobby data ----------------

        /// <summary>The host's settings as players see them.</summary>
        public struct HostView
        {
            /// <summary>The key was there, parsed, has our protocol version, and control is on.</summary>
            public bool Supported;
            public int Version;
            public bool Enabled;
            public long AttackCooldownMs;
            public int AttackStrengthPct;
            public bool OnePerDeath;
            public long DeathDelayMs;
            public bool BlockSelfInflicted;
        }

        /// <summary>This game's settings for the lobby. "on" is also 0 when a core host patch failed (HostReady false):
        /// players then stay vanilla instead of asking a host that can't do it.</summary>
        public static string Encode() => string.Format(CultureInfo.InvariantCulture,
            "v={0};on={1};cd={2};str={3};one={4};dl={5};self={6}",
            Const.ProtocolVersion, Enabled && Control.HostReady ? 1 : 0, AttackCooldownMs, AttackStrengthPct,
            OnePerDeath ? 1 : 0, DeathDelayMs, BlockSelfInflicted ? 1 : 0);

        public static bool TryParse(string text, out HostView view)
        {
            view = default;
            if (string.IsNullOrEmpty(text)) return false;
            bool hasVersion = false;
            foreach (var part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part.Substring(0, eq).Trim();
                if (!long.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
                switch (key)
                {
                    case "v": view.Version = (int)n; hasVersion = true; break;
                    case "on": view.Enabled = n == 1; break;
                    case "cd": view.AttackCooldownMs = Math.Max(0L, n); break;
                    case "str": view.AttackStrengthPct = (int)Math.Max(0L, n); break;
                    case "one": view.OnePerDeath = n == 1; break;
                    case "dl": view.DeathDelayMs = Math.Max(0L, n); break;
                    case "self": view.BlockSelfInflicted = n == 1; break;
                }
            }
            if (!hasVersion) return false;
            view.Supported = view.Version == Const.ProtocolVersion && view.Enabled;
            return true;
        }

        static CSteamID LobbyId => Hub.s != null && Hub.s.steamInviteDispatcher != null ? Hub.s.steamInviteDispatcher.joinedLobbyID : CSteamID.Nil;

        static HostView _lobby;
        static float _lobbyReadAt = -10f;
        static CSteamID _lobbyReadFrom = CSteamID.Nil;

        /// <summary>The host's settings from the lobby data (joined players). Re-read at most once a second; Steam keeps
        /// the members' copy of the lobby data up to date. No lobby (direct connect) or no key: not supported.</summary>
        public static HostView Lobby
        {
            get
            {
                var lobby = LobbyId;
                if (lobby != _lobbyReadFrom || Time.unscaledTime - _lobbyReadAt >= 1f || Time.unscaledTime < _lobbyReadAt)
                {
                    _lobbyReadFrom = lobby;
                    _lobbyReadAt = Time.unscaledTime;
                    _lobby = default;
                    if (lobby != CSteamID.Nil)
                    {
                        try { TryParse(SteamMatchmaking.GetLobbyData(lobby, Const.LobbyKey), out _lobby); }
                        catch (Exception e) { Log.WarnOnce("lobby-read", "Couldn't read the lobby data: " + e.Message); }
                    }
                }
                return _lobby;
            }
        }

        /// <summary>The settings in force this session: your own when hosting, the host's lobby data when joined.</summary>
        public static HostView Session
        {
            get
            {
                if (!Control.IsHost) return Lobby;
                return new HostView
                {
                    Supported = Enabled && Control.HostReady,
                    Version = Const.ProtocolVersion,
                    Enabled = Enabled,
                    AttackCooldownMs = AttackCooldownMs,
                    AttackStrengthPct = AttackStrengthPct,
                    OnePerDeath = OnePerDeath,
                    DeathDelayMs = DeathDelayMs,
                    BlockSelfInflicted = BlockSelfInflicted,
                };
            }
        }

        /// <summary>Hosting with a Steam lobby: writes the settings into its lobby data (no-op otherwise).</summary>
        public static void Publish()
        {
            if (!Control.IsHost) return;
            var lobby = LobbyId;
            if (lobby == CSteamID.Nil) return;
            try
            {
                var text = Encode();
                if (SteamMatchmaking.GetLobbyData(lobby, Const.LobbyKey) == text) return;
                SteamMatchmaking.SetLobbyData(lobby, Const.LobbyKey, text);
                Log.Trace("Published lobby settings: " + text);
            }
            catch (Exception e) { Log.WarnOnce("lobby-write", "Couldn't publish the settings in the lobby data: " + e.Message); }
        }
    }

    /// <summary>Publishes the host's settings when a level is created and whenever the pause menu opens (the lobby can
    /// be created after the settings last changed). Not core: without it players just don't see the host supports it.</summary>
    [HarmonyPatch]
    static class PublishPatch
    {
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
        static void OnNewLevel()
        {
            try { Settings.Publish(); }
            catch (Exception e) { Log.ErrorOnce("publish-level", "Publish on a new level failed: " + e); }
        }

        // EA 0.4.0's pause menu is UIPrefab_InGameMenu_new (same hook as Inventory).
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_InGameMenu_new), "OnEnable")]
        static void OnMenu()
        {
            try { Settings.Publish(); }
            catch (Exception e) { Log.ErrorOnce("publish-menu", "Publish on the pause menu failed: " + e); }
        }
    }
}
