// MIMESIS MimicControl - shared constants, wire protocol, logging and runtime facts
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The host and the possessing player talk only through vanilla packets carrying marker values that vanilla never
// sends, so a vanilla peer never receives anything it can't handle, and a vanilla host just answers a marker with an
// error (we never send one to a vanilla host anyway: the client checks the host's lobby data first).
//   possessor -> host  MoveStartReq  targetActorID = Protocol.HelloMarker  "I run MimicControl" (hello)
//                      MoveStartReq  targetActorID = <mimic id>          control input: direction, run, camera yaw
//                      UseSkillReq   actorID       = <mimic id>          grab
//   host -> possessor  SyncPossessorInfoSig  possessorLeftPossessingTime = Protocol.ControlMagic  "you control it"
//                      (vanilla re-sends that sig every second while possessing, so it doubles as a heartbeat)
// HostPatches.cs (host side) and ClientControl.cs (possessor side) build and read these packets only through
// Protocol, so the encoding lives in one place. Everything here is shared contract: change it on both sides at once
// and bump Const.ProtocolVersion.

using System;
using System.Collections.Generic;
using ReluNetwork.ConstEnum;
using ReluProtocol;
using ReluProtocol.Enum;
using UnityEngine;

namespace MimicControl
{
    enum Side { Host, Client }

    /// <summary>A patch class the feature can't work without. Plugin.Init applies the patch classes one at a time;
    /// if a core class of a side fails (a game update moved its target), that side stays off and plays vanilla.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    sealed class CoreAttribute : Attribute
    {
        public readonly Side Side;
        public CoreAttribute(Side side) { Side = side; }
    }

    static class Const
    {
        /// <summary>Wire protocol version. Host and possessor must match, or both stay vanilla.</summary>
        public const int ProtocolVersion = 1;
        public const string HarmonyId = "medovanx.mimiccontrol";
        /// <summary>Steam lobby data key with the host's settings (see Settings.Encode).</summary>
        public const string LobbyKey = "medovanx.mimiccontrol";
        /// <summary>AppDomain data key, a bool: true while this game controls a mimic. SpectatorCam reads it and keeps its
        /// free camera off (it would take over WASD). The patches are separate DLLs, so they share state this way.</summary>
        public const string ActiveFlagKey = "medovanx.mimiccontrol.active";

        /// <summary>The vanilla mimic attack: Skill.Grapple, sequence 3001, grab 30001, conta DOT 30003 on the victim.</summary>
        public const int GrabSkillId = 300;

        // ---- host timings (ms, host tick clock: Control.NowMs) ----
        /// <summary>No packet at all from the possessor for this long: the host gives the mimic back to its AI.</summary>
        public const long LeaseMs = 10000;
        /// <summary>No input for this long while moving (lost packets, frozen game): the mimic stops.</summary>
        public const long InputStaleMs = 600;
        /// <summary>Shortest time between two grabs, on top of the host's attack cooldown (spam guard).</summary>
        public const long MinAttackIntervalMs = 500;
        /// <summary>Conta from a mimic within this long after a controlled grab started is that grab's (scaled by
        /// Attack strength). The grab skill lasts 4.27 s and its DOT ends about 3.9 s after the start.</summary>
        public const long GrabWindowMs = 5000;
        /// <summary>A Conta death within this long of conta from someone else (a grab, a monster DOT, a teammate's
        /// sprayer) wasn't self-inflicted.</summary>
        public const long ForeignContaWindowMs = 5000;
        /// <summary>Idle: the mimic turns to the camera's yaw once the camera is this far off and input stopped this long.</summary>
        public const float IdleTurnMinAngle = 20f;
        public const long IdleTurnDelayMs = 400;
        public const long TurnIntervalMs = 200;

        // ---- host distances (m) ----
        /// <summary>Added to the game's possession distance (10 m) for the host-side check: the possessor saw the mimic
        /// within 10 m of the player they watch, but positions move on during the round trip.</summary>
        public const float PossessDistanceTolerance = 3f;
        /// <summary>How far ahead of the mimic the host sends it each step (walk / run), along a straight navmesh line.</summary>
        public const float WalkLookahead = 1.5f, RunLookahead = 3f;
        /// <summary>Grab aim assist: the nearest living player within this reach and this far off the possessor's aim
        /// (the grab's own hitbox is a 3 m, 120 degree fan).</summary>
        public const float GrabReach = 3f, GrabAimAngle = 75f;

        // ---- possessor timings (seconds, Time.unscaledTime) ----
        /// <summary>Input packets while moving, or right away (at most this often) when the input changes.</summary>
        public const float MoveSendInterval = 0.1f, ChangeSendInterval = 0.05f;
        /// <summary>Input packets while idle, which also keep the host's lease alive.</summary>
        public const float HeartbeatInterval = 1f;
        /// <summary>No ack (the per-second ControlMagic sync) for this long: the host stopped controlling; back to vanilla.</summary>
        public const float AckTimeout = 5f;
    }

    static class Log
    {
        const string Tag = "[MimicControl] ";
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

    /// <summary>One control input packet, as the host reads it.</summary>
    struct ControlInput
    {
        public int MimicId;
        /// <summary>World-space walking direction on the ground plane, length 0..1 (0 = stand still).</summary>
        public Vector3 Dir;
        public bool Run;
        /// <summary>The possessor's camera yaw in degrees (Unity convention, 0 = +Z), or NaN if not sent.</summary>
        public float Yaw;
        /// <summary>Sender's sequence number (increases by one per input packet).</summary>
        public int Seq;

        public bool Moving => Dir.sqrMagnitude > Protocol.MinDirSqr;
    }

    static class Protocol
    {
        /// <summary>MoveStartReq.targetActorID of a hello ("MC"). Vanilla always sends 0 there, and actor ids are positive.</summary>
        public const int HelloMarker = -0x4D43;

        /// <summary>possessorLeftPossessingTime of the host's ack. Positive, so the vanilla possession timer code treats it
        /// as time left; the host keeps it from counting down while controlling.</summary>
        public const long ControlMagic = 0x4D430000L + Const.ProtocolVersion;

        /// <summary>Possession cooldown that means "no more mimics until you die again" (24 h). The game's own cooldown
        /// machinery syncs it to the player every second, which blocks possession on their side and shows the gauge.</summary>
        public const long BlockedCooltime = 24L * 60 * 60 * 1000;

        /// <summary>A synced possession cooldown that means blocked (counted down a little since it was set).</summary>
        public static bool IsBlocked(long cooltime) => cooltime > BlockedCooltime / 2;

        public const float MinDirSqr = 0.04f * 0.04f;

        // ---------------- hello ----------------

        /// <summary>Reliable, unlike a vanilla MoveStartReq: it must not be lost, and the one sent right before a
        /// StartPossessingReq (also reliable) must reach the host first, on the same ordered channel. The flag only picks
        /// the send queue (IMsg.reliable isn't serialized), so the wire format is unchanged.</summary>
        public static MoveStartReq Hello() => new MoveStartReq
        {
            reliable = true,
            transID = Const.ProtocolVersion,
            actorMoveType = ActorMoveType.None,
            targetActorID = HelloMarker,
            basePositionPrev = new PosWithRot(),
            basePositionCurr = new PosWithRot(),
            basePositionFuture = new PosWithRot(),
            futureTime = 0L,
            pitch = 0f,
        };

        /// <summary>True for a hello; version is the sender's ProtocolVersion.</summary>
        public static bool IsHello(MoveStartReq msg, out int version)
        {
            version = msg != null ? msg.transID : 0;
            return msg != null && msg.targetActorID == HelloMarker;
        }

        // ---------------- control input ----------------

        /// <summary>Control input for mimicId: dir is a world-space direction (y ignored, clamped to length 1),
        /// yaw the camera yaw in degrees. Direction rides in basePositionFuture.x/z, yaw in basePositionCurr.yaw.</summary>
        public static MoveStartReq Input(int mimicId, Vector3 dir, bool run, float yaw, int seq)
        {
            dir.y = 0f;
            if (!Finite(dir.x) || !Finite(dir.z)) dir = Vector3.zero;
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            bool moving = dir.sqrMagnitude > MinDirSqr;
            var future = new PosWithRot();
            if (moving) { future.x = dir.x; future.z = dir.z; }
            var curr = new PosWithRot();
            curr.yaw = Finite(yaw) ? Mathf.Repeat(yaw, 360f) : 0f;
            return new MoveStartReq
            {
                transID = seq,
                actorMoveType = !moving ? ActorMoveType.None : run ? ActorMoveType.Run : ActorMoveType.Walk,
                targetActorID = mimicId,
                basePositionPrev = new PosWithRot(),
                basePositionCurr = curr,
                basePositionFuture = future,
                futureTime = 0L,
                pitch = 0f,
            };
        }

        /// <summary>Reads a control input. False for anything that isn't one (vanilla moves have targetActorID 0).
        /// Never trusts the numbers: NaN or oversized directions are cleaned up here.</summary>
        public static bool TryReadInput(MoveStartReq msg, out ControlInput input)
        {
            input = default;
            if (msg == null || msg.targetActorID <= 0) return false;
            var dir = Vector3.zero;
            var future = msg.basePositionFuture;
            if (future != null && (msg.actorMoveType == ActorMoveType.Walk || msg.actorMoveType == ActorMoveType.Run)
                && Finite(future.x) && Finite(future.z))
            {
                dir = new Vector3(future.x, 0f, future.z);
                if (dir.sqrMagnitude > 1f) dir.Normalize();
                if (dir.sqrMagnitude <= MinDirSqr) dir = Vector3.zero;
            }
            var curr = msg.basePositionCurr;
            input = new ControlInput
            {
                MimicId = msg.targetActorID,
                Dir = dir,
                Run = msg.actorMoveType == ActorMoveType.Run,
                Yaw = curr != null && Finite(curr.yaw) ? Mathf.Repeat(curr.yaw, 360f) : float.NaN,
                Seq = msg.transID,
            };
            return true;
        }

        // ---------------- attack ----------------

        /// <summary>Grab request for mimicId, aimed at the camera's yaw. The host always uses Const.GrabSkillId.</summary>
        public static UseSkillReq Attack(int mimicId, float yaw)
        {
            var start = new PosWithRot();
            start.yaw = Finite(yaw) ? Mathf.Repeat(yaw, 360f) : 0f;
            return new UseSkillReq
            {
                actorID = mimicId,
                skillMasterID = Const.GrabSkillId,
                targetID = 0,
                startBasePosition = start,
                endBasePosition = new PosWithRot(),
            };
        }

        /// <summary>Reads a grab request from senderId. False for vanilla skill requests (their actorID is the sender's own).</summary>
        public static bool TryReadAttack(UseSkillReq req, int senderId, out int mimicId, out float yaw)
        {
            mimicId = req != null ? req.actorID : 0;
            yaw = float.NaN;
            if (req == null || mimicId <= 0 || mimicId == senderId) return false;
            var start = req.startBasePosition;
            if (start != null && Finite(start.yaw)) yaw = Mathf.Repeat(start.yaw, 360f);
            return true;
        }

        // ---------------- ack ----------------

        /// <summary>The host's "you control mimicId" (sent at takeover; vanilla then repeats it every second).</summary>
        public static SyncPossessorInfoSig Ack(int possessorId, int mimicId) => new SyncPossessorInfoSig
        {
            possessorActorID = possessorId,
            possessorLeftPossessingTime = ControlMagic,
            possessorLeftPossessingCooltime = 0L,
            possessedActorID = mimicId,
        };

        public static bool IsAck(SyncPossessorInfoSig sig, int myActorId) =>
            sig != null && myActorId != 0 && sig.possessorActorID == myActorId && sig.possessedActorID != 0
            && sig.possessorLeftPossessingTime == ControlMagic;

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }

    /// <summary>Runtime facts both sides use.</summary>
    static class Control
    {
        /// <summary>Every core host / client patch class applied (Plugin.Init). Patch bodies of a side do nothing until
        /// it's true, so a half-applied side never changes the game.</summary>
        public static bool HostReady, ClientReady;

        /// <summary>This game hosts the session (its VWorld is the server).</summary>
        public static bool IsHost => Hub.s != null && Hub.s.pdata != null && Hub.s.pdata.ClientMode == NetworkClientMode.Host;

        /// <summary>The server's millisecond clock (host side; the game's own timers use it).</summary>
        public static long NowMs => Hub.s != null && Hub.s.timeutil != null ? Hub.s.timeutil.GetCurrentTickMilliSec() : 0L;

        /// <summary>Possessor side: the host of this session runs a compatible MimicControl with control on. Hosting
        /// yourself: your own settings. Joined: the host's Steam lobby data (none, e.g. a vanilla host: false).</summary>
        public static bool IsSupportedByHost =>
            ClientReady && (IsHost ? HostReady && Settings.Enabled : Settings.Lobby.Supported);

        /// <summary>Tells the other patches (SpectatorCam) whether this game is driving a mimic right now.</summary>
        public static void SetActiveFlag(bool on)
        {
            try { AppDomain.CurrentDomain.SetData(Const.ActiveFlagKey, on); }
            catch (Exception e) { Log.WarnOnce("flag", "Couldn't set the active flag: " + e.Message); }
        }
    }
}
