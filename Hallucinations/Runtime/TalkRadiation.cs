// MIMESIS Hallucinations - host side: radiation from dead players talking through a possessed mimic
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The host hears every player: its own game's voice chat receives the player channel whether its player is alive or
// dead, and the game's own server code reads it every tick (VPlayer.Update -> VoiceManager.TryGetVoiceAmplitude). So
// "the possessor is talking" is read here, on the host, for any possessor (modded or not, remote or the host itself),
// and nobody can fake it: Hub.s.voiceman.IsPlayerSpeaking(possessor.VoiceUID), the same signal that lights the speak
// icon on the possessed mimic for everyone.
// Per tick (H2, possessor Dead and possessing, after vanilla's update):
//   speaking = heard within HoldMs (hysteresis against voice packet gaps), not counting the first StartGraceMs of a
//   possession (the possessor's game is still moving its voice from the dead chat to the mimic)
//   for each living, not HarmBlocked player in the possessor's DungeonRoom, same IsIndoor as the mimic, within
//   Settings.TalkRangeM of the mimic: charge once per room tick (no stacking between possessors) into a fractional
//   accumulator; apply its whole part with StatController.IncreaseConta, never past Settings.TalkCapConta.
// Never kills: the cap is at most 99 % and conta at MAX_CONTA_VALUE is what kills (StatManager.IsContaExceeded). The
// new value reaches every client through the game's own stat sync (HUD, paranoia effects).

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Hallucinations
{
    static class TalkRadiation
    {
        /// <summary>Still "talking" this long after the voice chat last said so (gaps between voice packets).</summary>
        const long HoldMs = 250;
        /// <summary>No tick for this long, or another mimic: a new possession (vanilla's cooldown is 40 s).</summary>
        const long NewPossessionGapMs = 1000;
        /// <summary>Detailed log: at most one line per possessor this often while talking.</summary>
        const long TraceEveryMs = 1000;
        /// <summary>After the voice chat threw (it failed to start on this host), don't ask it again for this long.</summary>
        const long VoiceRetryMs = 5000;
        /// <summary>Speech this early in a possession doesn't count: the server already possesses, but the possessor's
        /// voice only moves from the dead chat to the players' channel once their game handles StartPossessingSig
        /// (VoiceManager.StartPossessionToMimic). A dead host hears the dead chat too, so until then it would count speech
        /// that the living can't hear.</summary>
        const long StartGraceMs = 500;

        /// <summary>One possession by one dead player, as far as talking goes.</summary>
        sealed class Session
        {
            public int MimicId;
            public long StartMs;
            public long LastTickMs;
            public long HeardMs;
            public bool Heard;
            public bool Talking;
            public bool Announced;
            public long LastTraceMs;
            /// <summary>Conta given since the last detailed-log line (all targets).</summary>
            public long GivenSinceTrace;
            /// <summary>Conta given during this possession (all targets).</summary>
            public long Given;
        }

        /// <summary>A living player's fractional radiation and the fixed step it was last charged in.</summary>
        sealed class Acc
        {
            public double Value;
            public float Tick = -1f;
        }

        static readonly Dictionary<VPlayer, Session> _sessions = new Dictionary<VPlayer, Session>(HostLogic.ByRef.Instance);
        static readonly Dictionary<VPlayer, Acc> _acc = new Dictionary<VPlayer, Acc>(HostLogic.ByRef.Instance);
        static readonly List<VPlayer> _candidates = new List<VPlayer>();
        static long _voiceBrokenUntil = long.MinValue;

        /// <summary>H2: pc is a possessor's controller (Dead, possessing, after vanilla's update); deltaTime in ms.</summary>
        internal static void Tick(PossessionController pc, long deltaTime)
        {
            if (!Settings.TalkActive || deltaTime <= 0) return;
            if (!(pc._self is VPlayer possessor)) return;
            var room = possessor.VRoom;
            if (!(room is DungeonRoom) || !room.DamageAppliable()) return;
            if (!(room.FindActorByObjectID(pc._possessingTargetActorID) is VCreature mimic) || !mimic.IsAliveStatus()) return;

            long now = Control.NowMs;
            var s = SessionOf(possessor, mimic.ObjectID, now);
            if (now - s.StartMs >= StartGraceMs && Speaking(possessor, now)) { s.HeardMs = now; s.Heard = true; }
            bool talking = s.Heard && now - s.HeardMs <= HoldMs;
            if (talking != s.Talking)
            {
                s.Talking = talking;
                Log.Trace($"{HostLogic.Name(possessor)} {(talking ? "started" : "stopped")} talking through mimic #{mimic.ObjectID}" +
                          (talking ? "" : $" (+{s.Given} radiation given this possession)"));
            }
            if (!talking) return;

            // Candidates first: no stat change while iterating the room's players.
            float range = Settings.TalkRangeM;
            float rangeSq = range * range;
            Vector3 at = mimic.PositionVector;
            bool indoor = mimic.IsIndoor;
            _candidates.Clear();
            try
            {
                foreach (var p in room._vPlayerDict.Values)
                {
                    if (p == null || !p.IsAliveStatus() || p.HarmBlocked || p.IsIndoor != indoor) continue;
                    if ((p.PositionVector - at).sqrMagnitude > rangeSq || p.StatControlUnit == null) continue;
                    _candidates.Add(p);
                }

                if (!s.Announced)
                {
                    s.Announced = true;
                    Log.Info($"{HostLogic.Name(possessor)} is talking through mimic #{mimic.ObjectID}: +{Settings.TalkPctPerSec}%/s " +
                             $"radiation to {_candidates.Count} player(s) within {Settings.TalkRangeM} m (never past {Settings.TalkCapPct}%)");
                }
                if (_candidates.Count == 0) return;

                long cap = Settings.TalkCapConta;
                double perMs = Control.MaxConta * Settings.TalkPctPerSec / 100.0 / 1000.0;
                float tick = Time.fixedTime;   // one room tick per FixedUpdate step (Hub.FixedUpdate -> VWorld.Update)
                foreach (var p in _candidates)
                {
                    if (!_acc.TryGetValue(p, out var acc)) _acc[p] = acc = new Acc();
                    if (acc.Tick == tick) continue;   // another possessor already charged this player this step
                    acc.Tick = tick;
                    var stat = p.StatControlUnit;
                    long cur = stat.GetCurrentConta();
                    if (cur >= cap) { acc.Value = 0; continue; }
                    acc.Value += perMs * deltaTime;
                    long whole = (long)acc.Value;
                    if (whole <= 0) continue;
                    long amount = Math.Min(whole, cap - cur);
                    stat.IncreaseConta(amount);
                    acc.Value -= whole;
                    s.Given += amount;
                    s.GivenSinceTrace += amount;
                }

                if (Settings.VerboseLog && now - s.LastTraceMs >= TraceEveryMs)
                {
                    s.LastTraceMs = now;
                    var sb = new StringBuilder(96);
                    sb.Append(HostLogic.Name(possessor)).Append(" talking through mimic #").Append(mimic.ObjectID)
                      .Append(": +").Append(s.GivenSinceTrace).Append(" radiation in total to");
                    foreach (var p in _candidates)
                    {
                        var stat = p.StatControlUnit;
                        sb.Append(' ').Append(HostLogic.Name(p)).Append(" (")
                          .Append(stat != null ? HostLogic.Pct(stat.GetCurrentConta()) : 0).Append("%)");
                    }
                    s.GivenSinceTrace = 0;
                    Log.Trace(sb.ToString());
                }
            }
            finally { _candidates.Clear(); }
        }

        /// <summary>The possessor's current possession (a new one for another mimic or after a gap).</summary>
        static Session SessionOf(VPlayer possessor, int mimicId, long now)
        {
            if (!_sessions.TryGetValue(possessor, out var s) || s.MimicId != mimicId || now - s.LastTickMs > NewPossessionGapMs)
            {
                if (s != null && s.Given > 0)
                    Log.Trace($"{HostLogic.Name(possessor)}'s possession of mimic #{s.MimicId} gave +{s.Given} radiation in total");
                _sessions[possessor] = s = new Session { MimicId = mimicId, StartMs = now };
            }
            s.LastTickMs = now;
            return s;
        }

        /// <summary>The voice chat hears this player talking right now (the mimic's speak icon). False when it can't tell:
        /// no voice id, or the voice chat isn't running on this host (fail-safe: no radiation).</summary>
        static bool Speaking(VPlayer vp, long now)
        {
            string id = vp.VoiceUID;
            if (string.IsNullOrEmpty(id)) return false;   // IsPlayerSpeaking logs an error on an empty id
            if (now < _voiceBrokenUntil) return false;
            var hub = Hub.s;
            if (hub == null) return false;
            var voice = hub.voiceman;
            if (voice == null) return false;
            try { return voice.IsPlayerSpeaking(id); }
            catch (Exception e)
            {
                // DissonanceFishNetComms.Instance (or its Comms) is null when voice chat failed to start.
                _voiceBrokenUntil = now + VoiceRetryMs;
                Log.WarnOnce("talk-voice", "Talk radiation can't read the voice chat on this host (it's off until it works): " + e.Message);
                return false;
            }
        }

        /// <summary>H4: a new level.</summary>
        internal static void Reset()
        {
            _sessions.Clear();
            _acc.Clear();
            _candidates.Clear();
        }

        /// <summary>H3 (prefix: the player is still in the room): drop their possession and accumulator.</summary>
        internal static void OnRemovePlayer(IVroom room, int actorId)
        {
            var p = room?.FindPlayerByObjectID(actorId);
            if (p == null) return;
            _sessions.Remove(p);
            _acc.Remove(p);
        }
    }
}
