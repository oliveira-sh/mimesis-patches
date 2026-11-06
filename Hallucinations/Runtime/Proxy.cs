// MIMESIS Hallucinations - host: playing a fake player's side (testing with FakePlayers)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// A fake player (the game's own dummy player, spawned by the FakePlayers patch or the game's "spawndummyplayer" admin
// command) has no game of its own: nothing would place its hallucination, move it on its own or decide its hits. With
// Debug keys on, the host's game does it instead, exactly like a target's game does for its own player (Victim), with
// the fake player's avatar as the player: Spawn with Proxy = its actor id comes here, Placed/Drive/Attack/Release/End
// go back as from the target. Its copy here is never seen or heard (FakeMimic proxy): the dead players see it through
// their own copies (Spectate), the host's included. Several fake players can each have one at a time.

using System;
using System.Collections.Generic;
using Mimic.Actors;
using UnityEngine;

namespace Hallucinations
{
    static class Proxy
    {
        sealed class Entry
        {
            public FakeMimic Fake;
            public int Seq;
            public float NextPoseAt;
        }

        static readonly Dictionary<int, Entry> _active = new Dictionary<int, Entry>();
        static readonly HashSet<int> _reported = new HashSet<int>();
        static readonly List<int> _scratch = new List<int>();

        /// <summary>The host says: hallucination m.Id for fake player m.Proxy; place it and run it.</summary>
        internal static void OnSpawn(in HostMsg m)
        {
            if (m.Id == 0 || _active.ContainsKey(m.Id) || _reported.Contains(m.Id)) return;
            var scene = Hub.Main as GamePlayScene;
            ProtoActor bot = null;
            try { bot = scene != null ? scene.GetActorByActorID(m.Proxy) : null; }
            catch (Exception) { /* not here */ }
            if (scene == null || !scene.IsGameLogicRunning || bot == null || bot.dead || Control.IsReplay)
            {
                End(m.Id, Outcome.Cancelled);
                return;
            }
            Vector3 pos;
            float yaw;
            string where;
            bool navOk;
            try
            {
                if (!Placement.TryFind(scene, bot, m.Seed, out pos, out yaw, out where, out navOk, avatarView: true))
                {
                    Log.Info($"Hallucination #{m.Id} for fake player {bot.nickName}: no place out of its view");
                    End(m.Id, Outcome.NoSpot);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("proxy-placement", "Placing a fake player's hallucination failed: " + e);
                End(m.Id, Outcome.Error);
                return;
            }
            var mode = m.Ctl != 0 ? FakeMimic.Mode.Remote : FakeMimic.Mode.Ai;
            var fake = FakeMimic.Create(scene, m.Id, m.Weapon, m.Seed, m.TtlMs, pos, yaw, navOk, mode, OnFakeEnded,
                onAttack: f => ClientNet.Send(Protocol.Attack(f.Id, f.transform.eulerAngles.y)),
                onTakeOver: f => ClientNet.Send(Protocol.Release(f.Id)),
                proxyTarget: bot);
            if (fake == null)
            {
                End(m.Id, Outcome.Error);
                return;
            }
            _active[m.Id] = new Entry { Fake = fake };
            ClientNet.Send(Protocol.Placed(m.Id, fake.transform.position, yaw));
            Log.Info($"Hallucination #{m.Id} ({fake.Description}) for fake player {bot.nickName} appears " +
                     $"{Vector3.Distance(pos, bot.transform.position):0.0} m away ({where}); this game plays its side");
        }

        internal static bool OnDrive(in HostMsg m)
        {
            if (!_active.TryGetValue(m.Id, out var e) || e.Fake == null) return false;
            if (e.Fake.CurrentMode == FakeMimic.Mode.Remote) e.Fake.ApplyRemote(m.Pose);
            return true;
        }

        internal static bool OnAttack(in HostMsg m)
        {
            if (!_active.TryGetValue(m.Id, out var e) || e.Fake == null) return false;
            if (e.Fake.CurrentMode == FakeMimic.Mode.Remote) e.Fake.AttackNow(m.Pose.Yaw);
            return true;
        }

        internal static bool OnRelease(in HostMsg m)
        {
            if (!_active.TryGetValue(m.Id, out var e) || e.Fake == null) return false;
            if (e.Fake.CurrentMode == FakeMimic.Mode.Remote)
            {
                e.Fake.SetMode(FakeMimic.Mode.Ai);
                Log.Info($"Hallucination #{m.Id} (fake player's): its dead player let go; it goes on its own");
            }
            return true;
        }

        /// <summary>Every frame: the poses of the ones running on their own (for the dead players' copies), and any
        /// destroyed behind our back.</summary>
        internal static void Tick()
        {
            if (_active.Count == 0) return;
            float now = Time.unscaledTime;
            _scratch.Clear();
            foreach (var kv in _active)
            {
                var e = kv.Value;
                if (e.Fake == null)
                {
                    _scratch.Add(kv.Key);
                    continue;
                }
                if (e.Fake.IsVanishing || e.Fake.CurrentMode != FakeMimic.Mode.Ai || now < e.NextPoseAt) continue;
                var pose = e.Fake.CurrentPose;
                e.NextPoseAt = now + (pose.Move == MoveKind.Idle ? Const.IdleSendInterval : Const.MoveSendInterval);
                ClientNet.Send(Protocol.Drive(kv.Key, pose, ++e.Seq));
            }
            foreach (int id in _scratch)
            {
                _active.Remove(id);
                End(id, Outcome.Cancelled);
            }
            _scratch.Clear();
        }

        static void OnFakeEnded(FakeMimic fake, Outcome outcome)
        {
            if (ReferenceEquals(fake, null)) return;
            if (_active.TryGetValue(fake.Id, out var e) && ReferenceEquals(e.Fake, fake)) _active.Remove(fake.Id);
            End(fake.Id, outcome);
        }

        static void End(int id, Outcome outcome)
        {
            if (id == 0 || !_reported.Add(id)) return;
            if (ClientNet.Send(Protocol.End(id, outcome))) Log.Info($"Hallucination #{id} (fake player's) ended: {outcome}");
        }

        /// <summary>A new level (or left one): they go at once.</summary>
        internal static void Reset()
        {
            _scratch.Clear();
            _scratch.AddRange(_active.Keys);
            foreach (int id in _scratch)
            {
                var f = _active.TryGetValue(id, out var e) ? e.Fake : null;
                if (f != null) f.Cancel("level left");
                else End(id, Outcome.Cancelled);
            }
            _scratch.Clear();
            _active.Clear();
            _reported.Clear();
        }
    }
}
