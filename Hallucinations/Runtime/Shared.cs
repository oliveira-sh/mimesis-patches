// MIMESIS Hallucinations - shared constants, wire protocol, logging and runtime facts
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The host and the players talk through two vanilla packets that carry text, so nothing vanilla ever receives
// something it can't handle:
//   client -> host  AdminCommandReq {command = Const.Command, args = "v=1^op=..^k=v.."}
//                   Every host registers the admin-command dispatcher (VWorld.RegisterAdminProtocol, no permission
//                   check). A vanilla host answers an unknown command with AdminCommandRes(InvalidAdminCommandArgument),
//                   which every client drops silently (NetworkManagerV2.HandleGlobalPacket). A modded host finds our
//                   command in VWorld.AdminCommandFuncs (HostPatches H1) and runs it on the room thread with the
//                   sending VPlayer. Safe from a living or a dead sender, to any host.
//   host -> client  DebugInfoSig {actorID = the RECEIVER's own avatar id, debugInfo = Const.S2CMagic + "v=1;op=..;k=v.."}
//                   Sent with VPlayer.SendToMe to that one player. The client routes it to its own avatar's
//                   ProtoActor.OnDebugInfoSig, where our prefix (client inbox) takes it; for a player actor the vanilla
//                   handler does nothing anyway. Only ever sent to players who said hello.
// Neither packet is recorded in match replays. The host's own game goes through the same dispatchers (virtual
// sessions), so hosting needs no special case. Message flow: hello/Ack at each level, Request -> Result to the dead
// player and Spawn to the target, End from the target -> Outcome to the dead player.
// Everything here is the contract between the host side and the client side: change it on both sides at once and
// bump Const.ProtocolVersion.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReluNetwork.ConstEnum;
using ReluReplay.Shared;
using UnityEngine;

namespace Hallucinations
{
    /// <summary>The parts of the patch. Plugin.Init applies the patch classes one at a time; when a [Core] class of a part
    /// fails (a game update moved its target), that part stays off and the game plays vanilla there.</summary>
    enum Part
    {
        /// <summary>Hosting: the admin command (validation, relay, cleanup). Off = no feature in your games.</summary>
        Host,
        /// <summary>Hosting: radiation from dead players talking through a possessed mimic.</summary>
        Talk,
        /// <summary>Any player: the host -> client inbox and the per-frame driver. Off = no hello, vanilla.</summary>
        Client,
        /// <summary>Dead player: E sends a hallucination. Off = E is vanilla possession only (hello without Caps.Request).</summary>
        Spectator,
    }

    [AttributeUsage(AttributeTargets.Class)]
    sealed class CoreAttribute : Attribute
    {
        public readonly Part Part;
        public CoreAttribute(Part part) { Part = part; }
    }

    static class Const
    {
        /// <summary>Wire protocol version. A host ignores a hello of another version; a client ignores messages of another
        /// version. Both then stay vanilla.</summary>
        public const int ProtocolVersion = 1;
        public const string HarmonyId = "medovanx.hallucinations";
        /// <summary>Steam lobby data key with the host's settings (Settings.Encode). Informational: clients enable the
        /// feature from the host's ack, not from the lobby (that also covers games without a Steam lobby).</summary>
        public const string LobbyKey = "medovanx.hallucinations";
        /// <summary>AdminCommandReq.command of every client -> host message. Lower case (VWorld.GetAdminCommand lower-cases;
        /// the dispatcher itself matches exactly). Never a vanilla command name.</summary>
        public const string Command = "medovanx.hallucinations";
        /// <summary>Prefix of DebugInfoSig.debugInfo for every host -> client message. Vanilla debug info never starts so.</summary>
        public const string S2CMagic = "MDXHAL|";

        /// <summary>Host: an active hallucination whose target never reported its end is forgotten this long after its
        /// lifetime (lost END packet, target crashed...).</summary>
        public const long SlotSlackMs = 5000;
        /// <summary>Client: hello is re-sent this often until the host acks, at most HelloMaxTries times per level.</summary>
        public const float HelloRetrySec = 2f;
        public const int HelloMaxTries = 5;
        /// <summary>Client: a request without a result for this long is forgotten (the tip leaves "sending").</summary>
        public const float RequestTimeoutSec = 3f;
        /// <summary>Debug: Ctrl+Shift+J adds this much radiation to yourself (host-validated, never past the talk cap).</summary>
        public const int DebugContaStepPct = 10;
    }

    static class Log
    {
        const string Tag = "[Hallucinations] ";
        static readonly HashSet<string> Once = new HashSet<string>();

        public static void Info(string message) => Debug.Log(Tag + message);
        public static void Warn(string message) => Debug.LogWarning(Tag + message);
        public static void Error(string message) => Debug.LogError(Tag + message);

        /// <summary>Per-packet and per-step details, only with "Detailed log" on (Patches window).</summary>
        public static void Trace(string message)
        {
            if (Settings.VerboseLog) Debug.Log(Tag + message);
        }

        /// <summary>Logs a warning once per key: for things that would otherwise repeat every frame or tick.</summary>
        public static void WarnOnce(string key, string message)
        {
            if (Once.Add(key)) Warn(message);
        }

        /// <summary>Logs an error once per key (a patch body that throws keeps throwing).</summary>
        public static void ErrorOnce(string key, string message)
        {
            if (Once.Add(key)) Error(message);
        }
    }

    // ---------------------------------------------------------------- wire enums (values are on the wire: never renumber)

    /// <summary>Client -> host operations.</summary>
    enum Op
    {
        None = 0,
        /// <summary>"I run this protocol", with my Caps. Sent at every level start (and retried until acked).</summary>
        Hello = 1,
        /// <summary>A dead player asks for a hallucination on a living player (or, Debug only, on themselves).</summary>
        Request = 2,
        /// <summary>The affected player reports how a hallucination ended.</summary>
        End = 3,
        /// <summary>Debug only: add radiation to the sender.</summary>
        DebugConta = 4,
    }

    /// <summary>Host -> client operations.</summary>
    enum Sop
    {
        None = 0,
        /// <summary>The host supports it, its settings, and which players can be targets. Sent on hello and whenever that
        /// changes.</summary>
        Ack = 1,
        /// <summary>To the affected player: show hallucination Id now.</summary>
        Spawn = 2,
        /// <summary>To the requester: the answer to its request (Nonce).</summary>
        Result = 3,
        /// <summary>To the requester: how its hallucination ended.</summary>
        Outcome = 4,
    }

    /// <summary>What the fake attacker carries. Melee: the victim's game picks bat, crowbar, frying pan or folding chair
    /// from the seed.</summary>
    enum Weapon
    {
        /// <summary>No weapon: the mimic's own strangle grab (also the fallback when a weapon can't be built).</summary>
        Grab = 0,
        Shotgun = 1,
        Bomb = 2,
        Melee = 3,
    }

    /// <summary>The host's weapon pool (Settings), on the wire as an int.</summary>
    [Flags]
    enum WeaponMask
    {
        None = 0,
        Shotgun = 1,
        Bomb = 2,
        Melee = 4,
        All = Shotgun | Bomb | Melee,
    }

    /// <summary>What a client can do, sent with its hello.</summary>
    [Flags]
    enum Caps
    {
        None = 0,
        /// <summary>Shows hallucinations (can be a target).</summary>
        Render = 1,
        /// <summary>Sends hallucinations with E while dead.</summary>
        Request = 2,
    }

    /// <summary>The host's answer to a request.</summary>
    enum Result
    {
        Ok = 0,
        /// <summary>Off on this host (settings, or a failed host patch).</summary>
        Disabled = 1,
        /// <summary>The requester is alive (and it isn't a debug request on themselves).</summary>
        NotDead = 2,
        /// <summary>No such player in the requester's level, or dead.</summary>
        BadTarget = 3,
        /// <summary>The target's game doesn't show hallucinations (no mod, other version, or its patch failed).</summary>
        NoMod = 4,
        /// <summary>The target's radiation isn't above the threshold.</summary>
        LowRadiation = 5,
        /// <summary>The requester's cooldown isn't over (CooldownLeftMs says how long).</summary>
        Cooldown = 6,
        /// <summary>The target already has a hallucination.</summary>
        Busy = 7,
        /// <summary>Not in a level (tram, lobby...).</summary>
        NotInLevel = 8,
        /// <summary>Malformed or of another version.</summary>
        BadRequest = 9,
    }

    /// <summary>How a hallucination ended (the affected player's report, forwarded to the requester).</summary>
    enum Outcome
    {
        None = 0,
        /// <summary>Its attack landed (it vanished at that moment).</summary>
        Landed = 1,
        /// <summary>The player hit it or walked into it.</summary>
        HitByPlayer = 2,
        /// <summary>Its lifetime ran out.</summary>
        Timeout = 3,
        /// <summary>No place out of the player's view to put it. The host refunds the requester's cooldown.</summary>
        NoSpot = 4,
        /// <summary>The player died, became a spectator, or left the level.</summary>
        Cancelled = 5,
        /// <summary>The player's game already showed one (shouldn't happen: the host checks).</summary>
        Busy = 6,
        /// <summary>It couldn't be built or failed while running. The host refunds the requester's cooldown.</summary>
        Error = 7,
        /// <summary>It lost its way to the player (no path, closed door). The host refunds the requester's cooldown.</summary>
        Lost = 8,
        /// <summary>It attacked but the player was out of reach by then (it vanished anyway).</summary>
        Missed = 9,
    }

    static class Outcomes
    {
        /// <summary>The hallucination never really reached the player: the host gives the requester its cooldown back.</summary>
        public static bool Refunds(Outcome o) => o == Outcome.NoSpot || o == Outcome.Error || o == Outcome.Lost;
    }

    // ---------------------------------------------------------------- messages as read

    /// <summary>A client -> host message as the host reads it. Fields that the op doesn't use stay at their defaults
    /// (Weapon is -1 when not sent: the host picks).</summary>
    struct ClientMsg
    {
        public int Version;
        public Op Op;
        public Caps Caps;          // Hello
        public int Target;         // Request: target actor id (the sender's own id = debug self-spawn)
        public int Nonce;          // Request: the sender's request number, echoed in the Result
        public int Weapon;         // Request: Debug only, the weapon to use; -1 = host picks from its pool
        public int Id;             // End: hallucination id
        public Outcome Outcome;    // End
        public int Pct;            // DebugConta: radiation to add, in percent
    }

    /// <summary>A host -> client message as a client reads it (one flat struct: the fields the op doesn't use stay at
    /// their defaults).</summary>
    struct HostMsg
    {
        public int Version;
        public Sop Op;
        // Ack
        public bool On;
        public int ThresholdPct;
        public int CooldownSec;
        public int LifetimeSec;
        public WeaponMask Weapons;
        public bool Debug;             // Ack and Spawn
        public int[] Targets;          // Ack: actor ids (in your level) that show hallucinations; never null after TryRead
        // Spawn
        public int Id;                 // Spawn, Result, Outcome
        public Weapon Weapon;
        public int Seed;
        public int TtlMs;
        public int From;               // Spawn: the requester's actor id (0 = debug self-spawn)
        // Result
        public int Nonce;
        public Result Result;
        public int Target;             // Result, Outcome
        public int CooldownLeftMs;
        // Outcome
        public Outcome Outcome;
    }

    /// <summary>Builds and reads every packet of the protocol. HostLogic and the client side only go through here, so the
    /// encoding lives in one place. Numbers are invariant-culture ints; keys are short ASCII.</summary>
    static class Protocol
    {
        // ======================= client -> host: AdminCommandReq.args = "v=1^op=N^key=value^..." =======================
        // AdminCommandArgs splits on '^' then '=' (exactly two parts each), so values are plain ints, never containing
        // '^' or '='. args is never null (the vanilla dispatcher would throw on it).

        public static AdminCommandReq Hello(Caps caps) => C2S(Op.Hello, ("c", (int)caps));

        /// <summary>weapon: Debug only (the host ignores it otherwise); null = the host picks.</summary>
        public static AdminCommandReq Request(int targetId, int nonce, Weapon? weapon = null) =>
            weapon.HasValue
                ? C2S(Op.Request, ("t", targetId), ("n", nonce), ("w", (int)weapon.Value))
                : C2S(Op.Request, ("t", targetId), ("n", nonce));

        public static AdminCommandReq End(int id, Outcome outcome) => C2S(Op.End, ("id", id), ("o", (int)outcome));

        public static AdminCommandReq DebugConta(int pct) => C2S(Op.DebugConta, ("p", pct));

        static AdminCommandReq C2S(Op op, params (string key, int value)[] fields)
        {
            var sb = new StringBuilder(48);
            sb.Append("v=").Append(Const.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append("^op=").Append(((int)op).ToString(CultureInfo.InvariantCulture));
            foreach (var f in fields) sb.Append('^').Append(f.key).Append('=').Append(f.value.ToString(CultureInfo.InvariantCulture));
            // AdminCommandReq's constructor already sets reliable; set again so this never depends on it.
            return new AdminCommandReq { command = Const.Command, args = sb.ToString(), reliable = true };
        }

        /// <summary>Host side. False for anything malformed. The version is read but not checked here (HostLogic answers a
        /// wrong version).</summary>
        public static bool TryRead(AdminCommandArgs args, out ClientMsg m)
        {
            m = default;
            m.Weapon = -1;
            if (args == null) return false;
            if (!TryInt(args["v"], out m.Version) || !TryInt(args["op"], out int op)) return false;
            m.Op = (Op)op;
            switch (m.Op)
            {
                case Op.Hello:
                    if (!TryInt(args["c"], out int caps)) return false;
                    m.Caps = (Caps)caps & (Caps.Render | Caps.Request);
                    return true;
                case Op.Request:
                    if (!TryInt(args["t"], out m.Target) || !TryInt(args["n"], out m.Nonce) || m.Target <= 0) return false;
                    if (TryInt(args["w"], out int w) && w >= (int)Weapon.Grab && w <= (int)Weapon.Melee) m.Weapon = w;
                    return true;
                case Op.End:
                    if (!TryInt(args["id"], out m.Id) || !TryInt(args["o"], out int o)) return false;
                    m.Outcome = (Outcome)o;
                    return true;
                case Op.DebugConta:
                    return TryInt(args["p"], out m.Pct);
                default:
                    return false;
            }
        }

        // ======================= host -> client: DebugInfoSig.debugInfo = S2CMagic + "v=1;op=N;key=value;..." ==========
        // receiverId is the receiving player's own actor id (VPlayer.ObjectID): the client routes an actor message to the
        // ProtoActor with that id, and its own avatar always exists while it's in the level. Lists are '.'-separated.

        public static DebugInfoSig Ack(int receiverId, bool on, int thresholdPct, int cooldownSec, int lifetimeSec,
            WeaponMask weapons, bool debug, IEnumerable<int> targets)
        {
            var sb = S2CBegin(Sop.Ack);
            Add(sb, "on", on ? 1 : 0);
            Add(sb, "thr", thresholdPct);
            Add(sb, "cd", cooldownSec);
            Add(sb, "life", lifetimeSec);
            Add(sb, "w", (int)weapons);
            Add(sb, "dbg", debug ? 1 : 0);
            sb.Append(";tg=");
            bool first = true;
            if (targets != null)
            {
                foreach (int id in targets)
                {
                    if (!first) sb.Append('.');
                    sb.Append(id.ToString(CultureInfo.InvariantCulture));
                    first = false;
                }
            }
            return S2C(receiverId, sb);
        }

        public static DebugInfoSig Spawn(int receiverId, int id, Weapon weapon, int seed, int ttlMs, int fromActorId, bool debug)
        {
            var sb = S2CBegin(Sop.Spawn);
            Add(sb, "id", id);
            Add(sb, "w", (int)weapon);
            Add(sb, "seed", seed);
            Add(sb, "ttl", ttlMs);
            Add(sb, "from", fromActorId);
            Add(sb, "dbg", debug ? 1 : 0);
            return S2C(receiverId, sb);
        }

        public static DebugInfoSig Result(int receiverId, int nonce, Result result, int id, int targetId, int cooldownLeftMs)
        {
            var sb = S2CBegin(Sop.Result);
            Add(sb, "n", nonce);
            Add(sb, "r", (int)result);
            Add(sb, "id", id);
            Add(sb, "t", targetId);
            Add(sb, "left", cooldownLeftMs);
            return S2C(receiverId, sb);
        }

        public static DebugInfoSig OutcomeMsg(int receiverId, int id, Outcome outcome, int targetId)
        {
            var sb = S2CBegin(Sop.Outcome);
            Add(sb, "id", id);
            Add(sb, "o", (int)outcome);
            Add(sb, "t", targetId);
            return S2C(receiverId, sb);
        }

        /// <summary>True for one of ours (the client inbox swallows it even when it can't be parsed).</summary>
        public static bool IsOurs(DebugInfoSig sig) =>
            sig != null && sig.debugInfo != null && sig.debugInfo.StartsWith(Const.S2CMagic, StringComparison.Ordinal);

        /// <summary>Client side. False for anything that isn't ours or is malformed. The version is read but not checked
        /// here (the client ignores another version).</summary>
        public static bool TryRead(DebugInfoSig sig, out HostMsg m)
        {
            m = default;
            m.Targets = Array.Empty<int>();
            if (!IsOurs(sig)) return false;
            bool hasVersion = false, hasOp = false;
            foreach (var part in sig.debugInfo.Substring(Const.S2CMagic.Length).Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq), value = part.Substring(eq + 1);
                if (key == "tg")
                {
                    var ids = new List<int>();
                    foreach (var s in value.Split('.'))
                        if (TryInt(s, out int id) && id > 0) ids.Add(id);
                    m.Targets = ids.ToArray();
                    continue;
                }
                if (!TryInt(value, out int n)) continue;
                switch (key)
                {
                    case "v": m.Version = n; hasVersion = true; break;
                    case "op": m.Op = (Sop)n; hasOp = true; break;
                    case "on": m.On = n == 1; break;
                    case "thr": m.ThresholdPct = n; break;
                    case "cd": m.CooldownSec = n; break;
                    case "life": m.LifetimeSec = n; break;
                    case "w":
                        // Ack: the weapon pool; Spawn: the weapon.
                        m.Weapons = (WeaponMask)n & WeaponMask.All;
                        m.Weapon = n >= (int)Weapon.Grab && n <= (int)Weapon.Melee ? (Weapon)n : Weapon.Grab;
                        break;
                    case "dbg": m.Debug = n == 1; break;
                    case "id": m.Id = n; break;
                    case "seed": m.Seed = n; break;
                    case "ttl": m.TtlMs = n; break;
                    case "from": m.From = n; break;
                    case "n": m.Nonce = n; break;
                    case "r": m.Result = (Result)n; break;
                    case "t": m.Target = n; break;
                    case "left": m.CooldownLeftMs = n; break;
                    case "o": m.Outcome = (Outcome)n; break;
                }
            }
            return hasVersion && hasOp && m.Op != Sop.None;
        }

        static StringBuilder S2CBegin(Sop op)
        {
            var sb = new StringBuilder(96);
            sb.Append(Const.S2CMagic);
            sb.Append("v=").Append(Const.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(";op=").Append(((int)op).ToString(CultureInfo.InvariantCulture));
            return sb;
        }

        static void Add(StringBuilder sb, string key, int value) =>
            sb.Append(';').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));

        /// <summary>Reliable (IMsg.reliable picks the send queue and defaults to false = the lossy one). A new object per
        /// send: the host's own game receives the same object through the virtual session, so never reuse or change one
        /// after sending it.</summary>
        static DebugInfoSig S2C(int receiverId, StringBuilder sb) => new DebugInfoSig
        {
            actorID = receiverId,
            debugInfo = sb.ToString(),
            hitCheckDrawInfos = new List<HitCheckDrawInfo>(),
            reliable = true,
        };

        static bool TryInt(string s, out int value) =>
            int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Runtime facts both sides use.</summary>
    static class Control
    {
        /// <summary>Every [Core] patch class of that part applied (Plugin.Init). Patch bodies of a part do nothing until
        /// it's true, so a half-applied part never changes the game.</summary>
        public static bool HostReady, TalkReady, ClientReady, SpectatorReady;

        /// <summary>The retired MimicControl patch is loaded too (its DLL is still in Mods): it would take possessions over.</summary>
        public static bool MimicControlLoaded;

        /// <summary>What this game can do, sent in its hello.</summary>
        public static Caps LocalCaps => !ClientReady ? Caps.None : Caps.Render | (SpectatorReady ? Caps.Request : Caps.None);

        /// <summary>This game hosts the session (its VWorld is the server).</summary>
        public static bool IsHost => Hub.s != null && Hub.s.pdata != null && Hub.s.pdata.ClientMode == NetworkClientMode.Host;

        /// <summary>The server's millisecond clock (host side; the game's own timers use it).</summary>
        public static long NowMs => Hub.s != null && Hub.s.timeutil != null ? Hub.s.timeutil.GetCurrentTickMilliSec() : 0L;

        /// <summary>Watching a replay: nothing of ours runs (hallucinations are live-only, like the game's own).</summary>
        public static bool IsReplay => ReplaySharedData.IsReplayPlayMode;

        /// <summary>Radiation at which a player dies (MAX_CONTA_VALUE, 10000 = 100 %).</summary>
        public static long MaxConta
        {
            get
            {
                var consts = Hub.s != null && Hub.s.dataman != null && Hub.s.dataman.ExcelDataManager != null
                    ? Hub.s.dataman.ExcelDataManager.Consts : null;
                return consts != null && consts.C_MaxContaValue > 0 ? consts.C_MaxContaValue : 10000L;
            }
        }
    }
}
