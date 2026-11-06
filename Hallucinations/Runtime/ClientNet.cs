// MIMESIS Hallucinations - every player: the per-frame driver, the inbox (host -> client) and hello/ack
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// A "level" is one GamePlayScene with one avatar ProtoActor object (actor ids are per room and can repeat between
// levels, so the avatar object is the identity). At each new level: forget the old host view, say hello
// (Protocol.Hello(Control.LocalCaps)), retry every Const.HelloRetrySec until an Ack (at most Const.HelloMaxTries).
// No ack = the host doesn't run this (or another version): the feature stays off for this level, silently (one Info
// line). Leaving the level resets every client part (the dead player's tip, the living player's hallucination).

using System;
using System.Text;
using HarmonyLib;
using Mimic.Actors;
using ReluProtocol;
using UnityEngine;

namespace Hallucinations
{
    /// <summary>The host's state as this game last heard it (its Ack). Not Supported = vanilla host, other version, or no
    /// ack yet this level.</summary>
    struct HostView
    {
        public bool Supported;          // an Ack of our version arrived this level and said on
        public int ThresholdPct;
        public int CooldownSec;
        public int LifetimeSec;
        public WeaponMask Weapons;
        public bool Debug;
        public int[] Targets;           // actor ids in this level that show hallucinations (never null once acked)

        /// <summary>The view an Ack gives (the caller checked its version).</summary>
        public static HostView From(in HostMsg ack) => new HostView
        {
            Supported = ack.On,
            ThresholdPct = ack.ThresholdPct,
            CooldownSec = ack.CooldownSec,
            LifetimeSec = ack.LifetimeSec,
            Weapons = ack.Weapons,
            Debug = ack.Debug,
            Targets = ack.Targets ?? Array.Empty<int>(),
        };

        /// <summary>actorId shows hallucinations (the host says it said hello with Caps.Render this level).</summary>
        public bool IsTarget(int actorId) => Targets != null && Array.IndexOf(Targets, actorId) >= 0;
    }

    static class ClientNet
    {
        static GameObject _driver;

        /// <summary>The host's state for this level (see HostView): set from Acks, cleared on a new level.</summary>
        internal static HostView View = default;

        // ---- this level (main thread; times are Time.unscaledTime) ----
        static GamePlayScene _scene;
        static ProtoActor _avatar;
        static bool _acked;             // any Ack of our version this level (on or off): stop saying hello
        static int _helloTries;
        static float _nextHelloAt;
        static bool _noAnswerLogged;
        static bool _ackLogged;
        static bool _lastOn;
        static int _lastThreshold;
        static string _lastTargets;

        /// <summary>Called once from Plugin.Init (Hub.Awake): the per-frame driver, alive for the whole game.</summary>
        public static void Install()
        {
            if (_driver != null) return;
            _driver = new GameObject("MedovanxHallucinations");
            UnityEngine.Object.DontDestroyOnLoad(_driver);
            _driver.AddComponent<ClientDriver>();
        }

        /// <summary>The avatar of the current level (null between levels).</summary>
        internal static ProtoActor Avatar => _avatar;

        /// <summary>Every frame (ClientDriver): level changes, hello and its retries.</summary>
        internal static void Tick()
        {
            var scene = Hub.Main as GamePlayScene;
            if (scene == null)
            {
                if (!ReferenceEquals(_scene, null) || !ReferenceEquals(_avatar, null)) LeaveLevel("left the level");
                return;
            }
            float now = Time.unscaledTime;
            if (!ReferenceEquals(scene, _scene))
            {
                if (!ReferenceEquals(_scene, null)) LeaveLevel("new level");
                _scene = scene;
            }
            // No avatar (not spawned yet, or gone for a moment around a reconnect): keep what we have, say nothing.
            var avatar = scene.GetMyAvatar();
            if (avatar == null) return;
            if (!ReferenceEquals(avatar, _avatar))
            {
                // A new avatar object in this scene (first spawn, or re-entry after a reconnect: a new VPlayer on the
                // host, which needs a new hello).
                if (!ReferenceEquals(_avatar, null)) LeaveLevel("new avatar", keepScene: true);
                _avatar = avatar;
                _helloTries = 0;
                _nextHelloAt = now;
                _noAnswerLogged = false;
                _ackLogged = false;
            }

            if (_acked) return;
            if (_helloTries < Const.HelloMaxTries)
            {
                if (now < _nextHelloAt) return;
                var caps = Control.LocalCaps;
                if (caps == Caps.None) return; // the player side is off: say nothing, stay vanilla
                if (Send(Protocol.Hello(caps)))
                {
                    _helloTries++;
                    if (_helloTries == 1)
                        Log.Info($"Hello sent to the host (protocol {Const.ProtocolVersion}, shows {(caps.HasFlag(Caps.Render) ? "yes" : "no")}, " +
                                 $"sends {(caps.HasFlag(Caps.Request) ? "yes" : "no")})");
                    else Log.Trace($"Hello re-sent ({_helloTries}/{Const.HelloMaxTries})");
                }
                _nextHelloAt = now + Const.HelloRetrySec;
                return;
            }
            if (!_noAnswerLogged && now >= _nextHelloAt)
            {
                _noAnswerLogged = true;
                Log.Info("No answer from the host: it doesn't run Hallucinations (or another version). Off for this level.");
            }
        }

        /// <summary>Forgets the level: the host view, the dead player's tip and request, the living player's
        /// hallucination (destroyed at once, reported as cut short).</summary>
        static void LeaveLevel(string why, bool keepScene = false)
        {
            bool had = View.Supported;
            View = default;
            _acked = false;
            if (!keepScene) _scene = null;
            _avatar = null;
            _lastTargets = null;
            try { SpectatorInput.Reset(); }
            catch (Exception e) { Log.ErrorOnce("leave-spectator", "Resetting the spectator side failed: " + e); }
            try { Victim.Reset(cancelled: true); }
            catch (Exception e) { Log.ErrorOnce("leave-victim", "Resetting the hallucination failed: " + e); }
            if (had) Log.Trace("Level left: " + why);
        }

        /// <summary>A message from the host (client inbox, main thread).</summary>
        internal static void OnHostMessage(ProtoActor receiver, in HostMsg m)
        {
            // The host addresses every message to the receiver's own avatar, and only after this level's hello (sent once
            // _avatar is set). Anything else is stale: e.g. a message of the last level whose actor id now names another
            // actor here, which must not count as this level's Ack (it would stop the hello).
            if (ReferenceEquals(_avatar, null) || !ReferenceEquals(receiver, _avatar))
            {
                Log.Trace($"Message from the host not for this level's avatar ignored: {m.Op} (actor {(receiver != null ? receiver.ActorID : 0)})");
                return;
            }
            if (m.Version != Const.ProtocolVersion)
            {
                Log.WarnOnce("s2c-v" + m.Version,
                    $"The host runs Hallucinations protocol {m.Version}, this game runs {Const.ProtocolVersion}: ignored (everyone needs the same version).");
                return;
            }
            if (Settings.VerboseLog) Log.Trace($"From the host: {m.Op} (id {m.Id}, target {m.Target}, nonce {m.Nonce})");
            int me = receiver.ActorID;
            switch (m.Op)
            {
                case Sop.Ack:
                    OnAck(m);
                    break;
                case Sop.Spawn:
                    Victim.OnSpawn(m);
                    break;
                case Sop.Result:
                    if (me != 0 && m.Target == me) DebugKeys.OnResult(m);
                    else SpectatorInput.OnResult(m);
                    break;
                case Sop.Outcome:
                    if (me != 0 && m.Target == me) DebugKeys.OnOutcome(m);
                    else SpectatorInput.OnOutcome(m);
                    break;
                default:
                    Log.Trace("Unknown message from the host: op " + (int)m.Op);
                    break;
            }
        }

        static void OnAck(in HostMsg m)
        {
            _acked = true;
            View = HostView.From(m);
            string targets = TargetNames(View.Targets);
            if (!_ackLogged || m.On != _lastOn || m.ThresholdPct != _lastThreshold)
            {
                Log.Info(m.On
                    ? $"Host supports Hallucinations: on, above {m.ThresholdPct} % radiation, {m.CooldownSec} s cooldown, " +
                      $"lifetime {m.LifetimeSec} s, weapons {m.Weapons}{(m.Debug ? ", debug keys on" : "")}; targets [{targets}]"
                    : "Host supports Hallucinations but has them off");
            }
            else if (targets != _lastTargets) Log.Trace($"Targets now [{targets}]");
            _ackLogged = true;
            _lastOn = m.On;
            _lastThreshold = m.ThresholdPct;
            _lastTargets = targets;
            SpectatorInput.OnViewChanged();
        }

        static string TargetNames(int[] ids)
        {
            if (ids == null || ids.Length == 0) return "";
            var sb = new StringBuilder();
            var main = Hub.Main;
            foreach (int id in ids)
            {
                if (sb.Length > 0) sb.Append(", ");
                ProtoActor a = null;
                try { a = main != null ? main.GetActorByActorID(id) : null; }
                catch (Exception) { /* name only */ }
                if (a != null && a.AmIAvatar()) sb.Append("you");
                else if (a != null) sb.Append(a.nickName);
                else sb.Append('#').Append(id);
            }
            return sb.ToString();
        }

        /// <summary>Fire-and-forget to the host (no callback: AdminCommandRes never reaches a callback, every client drops it
        /// first, so SendPacketWithCallback would time out).</summary>
        internal static bool Send(IMsg msg)
        {
            var main = Hub.Main;
            if (main == null || msg == null) return false;
            try
            {
                main.SendPacket(msg);
                if (Settings.VerboseLog && msg is AdminCommandReq req) Log.Trace("To the host: " + req.args);
                return true;
            }
            catch (Exception e)
            {
                Log.WarnOnce("send", "Couldn't send to the host: " + e.Message);
                return false;
            }
        }
    }

    sealed class ClientDriver : MonoBehaviour
    {
        void Update()
        {
            if (!Control.ClientReady || Control.IsReplay) return;
            try { ClientNet.Tick(); }
            catch (Exception e) { Log.ErrorOnce("tick-net", "Hello/ack update failed: " + e); }
            try { SpectatorInput.Tick(); }
            catch (Exception e) { Log.ErrorOnce("tick-spectator", "Spectator update failed: " + e); }
            try { Victim.Tick(); }
            catch (Exception e) { Log.ErrorOnce("tick-victim", "Hallucination update failed: " + e); }
            try { DebugKeys.Tick(); }
            catch (Exception e) { Log.ErrorOnce("tick-debug", "Debug keys failed: " + e); }
        }
    }

    /// <summary>C1: the client inbox. Without it this game can't hear the host, so it never says hello (vanilla).</summary>
    [Core(Part.Client)]
    [HarmonyPatch(typeof(ProtoActor), nameof(ProtoActor.OnDebugInfoSig))]
    static class ClientInboxPatch
    {
        // C1. ProtoActor's [PacketHandler] for DebugInfoSig (private; the packet mediator calls it by reflection with the
        // actor whose id the sig names: our host addresses it to our own avatar). Ours (Const.S2CMagic) never reach the
        // vanilla handler, even when malformed, in a replay, or with the client part off; anything else is vanilla debug
        // info and passes untouched.
        static bool Prefix(ProtoActor __instance, DebugInfoSig sig)
        {
            if (!Protocol.IsOurs(sig)) return true;
            if (!Control.ClientReady || Control.IsReplay) return false;
            try
            {
                if (Protocol.TryRead(sig, out var m)) ClientNet.OnHostMessage(__instance, m);
                else Log.WarnOnce("inbox-malformed", "A malformed message from the host was ignored: " + sig.debugInfo);
            }
            catch (Exception e) { Log.ErrorOnce("C1", "Reading a message from the host failed: " + e); }
            return false;
        }
    }
}
