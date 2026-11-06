// MIMESIS Hallucinations - host settings and the lobby data that carries them
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The host's choices (Patches window) are saved in PlayerPrefs. The host enforces every rule itself and tells each
// player what they need (on/off, threshold, cooldown, lifetime, debug, who can be a target) in its ack (Protocol.Ack),
// so players never depend on the lobby data. The settings are also published in the Steam lobby data under
// Const.LobbyKey, like Inventory's, for anyone looking at the lobby.
// Lobby format: "v=1;on=1;thr=50;cd=20;life=25;w=7;talk=1;rng=10;cap=90;dbg=0" (protocol version, enabled and the
// host side working, radiation threshold %, cooldown s, lifetime s, weapon pool mask, talk radiation %/s, talk range
// m, talk cap %, debug). Unknown keys are ignored.

using System;
using System.Globalization;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Hallucinations
{
    static class Settings
    {
        const string Prefix = "medovanx.Hallucinations.";

        public const int ThresholdMin = 0, ThresholdMax = 95, ThresholdDefault = 50;     // % radiation; "above"
        public const int CooldownMin = 5, CooldownMax = 120, CooldownDefault = 20;       // s, per dead player
        public const int LifetimeMin = 10, LifetimeMax = 60, LifetimeDefault = 25;       // s, then it fades away
        public const int TalkMin = 0, TalkMax = 5, TalkDefault = 1;                      // % radiation per second; 0 = off
        public const int RangeMin = 2, RangeMax = 30, RangeDefault = 10;                 // m from the possessed mimic
        public const int CapMin = 50, CapMax = 99, CapDefault = 90;                      // % radiation talking never passes

        static bool _loaded;
        static bool _enabled, _shotgun, _bomb, _melee, _debug, _verbose;
        static int _threshold, _cooldown, _lifetime, _talk, _range, _cap;

        /// <summary>Raised (main thread) after any published setting changes, so the host can re-send its ack.</summary>
        public static event Action Changed;

        /// <summary>Reads PlayerPrefs once (Plugin.Init); the setters keep the cache and PlayerPrefs in step.</summary>
        public static void Load()
        {
            _enabled = PlayerPrefs.GetInt(Prefix + "Enabled", 1) == 1;
            _threshold = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Threshold", ThresholdDefault), ThresholdMin, ThresholdMax);
            _cooldown = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Cooldown", CooldownDefault), CooldownMin, CooldownMax);
            _lifetime = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "Lifetime", LifetimeDefault), LifetimeMin, LifetimeMax);
            _shotgun = PlayerPrefs.GetInt(Prefix + "Shotgun", 1) == 1;
            _bomb = PlayerPrefs.GetInt(Prefix + "Bomb", 1) == 1;
            _melee = PlayerPrefs.GetInt(Prefix + "Melee", 1) == 1;
            _talk = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "TalkRadiation", TalkDefault), TalkMin, TalkMax);
            _range = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "TalkRange", RangeDefault), RangeMin, RangeMax);
            _cap = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "TalkCap", CapDefault), CapMin, CapMax);
            _debug = PlayerPrefs.GetInt(Prefix + "Debug", 0) == 1;
            _verbose = PlayerPrefs.GetInt(Prefix + "VerboseLog", 0) == 1;
            _loaded = true;
        }

        static void EnsureLoaded() { if (!_loaded) Load(); }

        static void Save(string key, int value, bool publish = true)
        {
            PlayerPrefs.SetInt(Prefix + key, value);
            PlayerPrefs.Save();
            if (!publish) return;
            Publish();
            try { Changed?.Invoke(); }
            catch (Exception e) { Log.ErrorOnce("settings-changed", "Applying the new settings failed: " + e); }
        }

        // ---------------- the host's settings (used when you host) ----------------

        /// <summary>The whole feature in games you host: hallucinations and talk radiation.</summary>
        public static bool Enabled
        {
            get { EnsureLoaded(); return _enabled; }
            set { EnsureLoaded(); _enabled = value; Save("Enabled", value ? 1 : 0); }
        }

        /// <summary>A player can be sent hallucinations while their radiation is above this many percent.</summary>
        public static int ThresholdPct
        {
            get { EnsureLoaded(); return _threshold; }
            set { EnsureLoaded(); _threshold = Mathf.Clamp(value, ThresholdMin, ThresholdMax); Save("Threshold", _threshold); }
        }

        /// <summary>Seconds a dead player waits after sending one before sending the next.</summary>
        public static int CooldownSec
        {
            get { EnsureLoaded(); return _cooldown; }
            set { EnsureLoaded(); _cooldown = Mathf.Clamp(value, CooldownMin, CooldownMax); Save("Cooldown", _cooldown); }
        }

        /// <summary>Seconds a hallucination lasts at most (it fades away if its attack never lands).</summary>
        public static int LifetimeSec
        {
            get { EnsureLoaded(); return _lifetime; }
            set { EnsureLoaded(); _lifetime = Mathf.Clamp(value, LifetimeMin, LifetimeMax); Save("Lifetime", _lifetime); }
        }

        public static bool Shotgun
        {
            get { EnsureLoaded(); return _shotgun; }
            set { EnsureLoaded(); _shotgun = value; Save("Shotgun", value ? 1 : 0); }
        }

        public static bool Bomb
        {
            get { EnsureLoaded(); return _bomb; }
            set { EnsureLoaded(); _bomb = value; Save("Bomb", value ? 1 : 0); }
        }

        public static bool Melee
        {
            get { EnsureLoaded(); return _melee; }
            set { EnsureLoaded(); _melee = value; Save("Melee", value ? 1 : 0); }
        }

        /// <summary>The weapon pool. Empty = bare-handed hallucinations only (the mimic's grab).</summary>
        public static WeaponMask Weapons =>
            (Shotgun ? WeaponMask.Shotgun : WeaponMask.None) | (Bomb ? WeaponMask.Bomb : WeaponMask.None) | (Melee ? WeaponMask.Melee : WeaponMask.None);

        /// <summary>Radiation per second, in percent, that living players near a possessed mimic get while its possessor
        /// talks. 0 = off.</summary>
        public static int TalkPctPerSec
        {
            get { EnsureLoaded(); return _talk; }
            set { EnsureLoaded(); _talk = Mathf.Clamp(value, TalkMin, TalkMax); Save("TalkRadiation", _talk); }
        }

        /// <summary>Meters from the possessed mimic within which living players get talk radiation.</summary>
        public static int TalkRangeM
        {
            get { EnsureLoaded(); return _range; }
            set { EnsureLoaded(); _range = Mathf.Clamp(value, RangeMin, RangeMax); Save("TalkRange", _range); }
        }

        /// <summary>Talk radiation never takes anyone past this many percent (so it never kills).</summary>
        public static int TalkCapPct
        {
            get { EnsureLoaded(); return _cap; }
            set { EnsureLoaded(); _cap = Mathf.Clamp(value, CapMin, CapMax); Save("TalkCap", _cap); }
        }

        /// <summary>Testing: Ctrl+Shift+H sends a hallucination to yourself while alive (any radiation, no cooldown),
        /// Ctrl+Shift+J adds 10 % radiation to yourself. The host's setting decides; your own decides whether your keys
        /// send anything.</summary>
        public static bool Debug
        {
            get { EnsureLoaded(); return _debug; }
            set { EnsureLoaded(); _debug = value; Save("Debug", value ? 1 : 0); }
        }

        /// <summary>Detailed log (this game only, not published): per-packet and per-step lines for bug reports.</summary>
        public static bool VerboseLog
        {
            get { EnsureLoaded(); return _verbose; }
            set { EnsureLoaded(); _verbose = value; Save("VerboseLog", value ? 1 : 0, publish: false); }
        }

        public static long CooldownMs => CooldownSec * 1000L;
        public static int LifetimeMs => LifetimeSec * 1000;

        /// <summary>Hallucinations work in games you host: enabled and the host side patched.</summary>
        public static bool HostActive => Enabled && Control.HostReady;

        /// <summary>Talk radiation works in games you host.</summary>
        public static bool TalkActive => Enabled && Control.TalkReady && TalkPctPerSec > 0;

        /// <summary>The radiation a player must be above, in conta (0..MAX_CONTA_VALUE).</summary>
        public static long ThresholdConta => Control.MaxConta * ThresholdPct / 100;

        /// <summary>The radiation talk radiation stops at, in conta.</summary>
        public static long TalkCapConta => Control.MaxConta * TalkCapPct / 100;

        // ---------------- lobby data ----------------

        /// <summary>This game's settings for the lobby. "on" is 0 when a core host patch failed (HostReady false).</summary>
        public static string Encode() => string.Format(CultureInfo.InvariantCulture,
            "v={0};on={1};thr={2};cd={3};life={4};w={5};talk={6};rng={7};cap={8};dbg={9}",
            Const.ProtocolVersion, HostActive ? 1 : 0, ThresholdPct, CooldownSec, LifetimeSec, (int)Weapons,
            Enabled && Control.TalkReady ? TalkPctPerSec : 0, TalkRangeM, TalkCapPct, Debug ? 1 : 0);

        static CSteamID LobbyId => Hub.s != null && Hub.s.steamInviteDispatcher != null ? Hub.s.steamInviteDispatcher.joinedLobbyID : CSteamID.Nil;

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

    /// <summary>Publishes the host's settings when a level is created and whenever the pause menu opens (the lobby can be
    /// created after the settings last changed). Not core: the lobby data is informational.</summary>
    [HarmonyPatch]
    static class PublishPatch
    {
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
        static void OnNewLevel()
        {
            try { Settings.Publish(); }
            catch (Exception e) { Log.ErrorOnce("publish-level", "Publish on a new level failed: " + e); }
        }

        // EA 0.4.x's pause menu is UIPrefab_InGameMenu_new (same hook as Inventory).
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_InGameMenu_new), "OnEnable")]
        static void OnMenu()
        {
            try { Settings.Publish(); }
            catch (Exception e) { Log.ErrorOnce("publish-menu", "Publish on the pause menu failed: " + e); }
        }
    }
}
