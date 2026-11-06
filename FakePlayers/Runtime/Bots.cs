// MIMESIS FakePlayers - the host's server side: spawning fake players and changing their state
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// A fake player is the game's own dummy player (SessionContext.CreateDummy, what its "spawndummyplayer" admin command
// makes): a real VPlayer in the host's room, seen by every game like any player, with a session that sends nothing.
// It never registers with the session (no Steam id the game knows), so room changes leave it behind and its room takes
// it away: it can't hold up a level start or end. Everything here runs on the main thread, like the host's server
// itself (VWorld updates in Hub.FixedUpdate); the panel queues its clicks and they run in Update.
// Changes go through the same calls the game's admin commands use: ForcedDying (suicide), Revive (revive), the stat
// controller (changehp, alterconta), Teleport (teleport), SetCheatVoicePeak (fakevoice), PendRemovePlayer
// (removealldummy). Walking is a player's own move messages (MovementController.DirectMoveStart/Stop, what a player's
// game sends as it walks) along a navmesh route: the server's path movement (PathMoveStart, how monsters walk) doesn't
// move players.

using System;
using System.Collections.Generic;
using ReluNetwork.ConstEnum;
using ReluProtocol;
using ReluProtocol.Enum;
using UnityEngine;

namespace FakePlayers
{
    static class Bots
    {
        const int MaxBots = 9;
        const float FollowInterval = 0.2f;      // one move message per step, like a player's game sends them
        const float FollowNear = 2.5f;          // stops this close to you
        const float FollowTeleportBeyond = 25f; // ...and comes over at once when it's this far or can't walk to you
        const float RouteInterval = 0.5f;
        const long TalkPeak = 60;               // voice peak (0-100) while it "talks"

        sealed class State
        {
            public bool Follow;
            public float NextFollowAt;
            public int FollowFails;
            public List<Vector3> Path;
            public int PathIdx;
            public float NextRouteAt;
            public int TransId;
            public bool Moving;
            public bool FailLogged;
        }

        // Per fake player of the current room (VPlayer by reference: ids repeat between rooms).
        static readonly Dictionary<VPlayer, State> _state = new Dictionary<VPlayer, State>();
        static readonly List<VPlayer> _scratch = new List<VPlayer>();
        static IVroom _stateRoom;

        /// <summary>This game hosts: its VWorld is the server.</summary>
        internal static bool IsHost => Hub.s != null && Hub.s.pdata != null && Hub.s.pdata.ClientMode == NetworkClientMode.Host && Hub.s.vworld != null;

        /// <summary>Your own player on the server and its room (null when not hosting, or between levels).</summary>
        internal static bool TryGetHost(out VPlayer host, out IVroom room)
        {
            host = null;
            room = null;
            if (!IsHost) return false;
            var me = Hub.Main != null ? Hub.Main.GetMyAvatar() : null;
            var rooms = Hub.s.vworld.VRoomManager?._vrooms;
            if (me == null || rooms == null) return false;
            foreach (var r in rooms.Values)
            {
                // Actor ids are per room: the host player with your avatar's id is you.
                var p = r?.FindPlayerByObjectID(me.ActorID);
                if (p != null && p.IsHost)
                {
                    host = p;
                    room = r;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The fake players in room (in join order).</summary>
        internal static List<VPlayer> InRoom(IVroom room)
        {
            var list = new List<VPlayer>();
            room?.IterateAllPlayer(p =>
            {
                if (p != null && p.IsDummy) list.Add(p);
            });
            list.Sort((a, b) => a.ObjectID.CompareTo(b.ObjectID));
            return list;
        }

        // ============================================================================================ spawn, remove

        internal static string Spawn(int count)
        {
            if (!TryGetHost(out var host, out var room)) return "Host a game first";
            var existing = InRoom(room);
            count = Mathf.Clamp(count, 1, MaxBots - existing.Count);
            if (count <= 0) return $"At most {MaxBots} fake players";
            var used = new HashSet<string>();
            foreach (var b in existing) used.Add(b.ActorName);
            int made = 0, n = 1;
            for (int i = 0; i < count; i++)
            {
                while (used.Contains("Bot " + n)) n++;
                string name = "Bot " + n;
                used.Add(name);
                try
                {
                    var pos = SpotNear(host, i);
                    var player = SessionContext.CreateDummy(host.MasterID, name, room.Property.vRoomType)
                        .CreatePlayer(room.GetNewObjectID(), room, pos, host.IsIndoor);
                    if (player == null) { Log.Warn($"{name}: the game couldn't create it"); continue; }
                    var res = room.AddPlayer(player, 0);
                    if (res != MsgErrorCode.Success) { Log.Warn($"{name}: the room refused it ({res})"); continue; }
                    player.ForceSpawnIfReady();
                    player.HandleLevelLoadComplete(0);
                    made++;
                    Log.Info($"Spawned {name} (#{player.ObjectID}) in {room.GetType().Name}");
                }
                catch (Exception e) { Log.Error($"Spawning {name} failed: " + e); }
            }
            return made == 0 ? "Couldn't spawn (see the log)" : made == 1 ? "Spawned 1 fake player" : $"Spawned {made} fake players";
        }

        internal static string Remove(VPlayer p)
        {
            if (!Valid(p, out var room)) return "It's gone";
            room.PendRemovePlayer(p.ObjectID, backup: false, kill: false);
            _state.Remove(p);
            Log.Info($"Removed {p.ActorName}");
            return "Removed " + p.ActorName;
        }

        internal static string RemoveAll()
        {
            if (!TryGetHost(out _, out var room)) return "Host a game first";
            int n = 0;
            foreach (var p in InRoom(room))
            {
                room.PendRemovePlayer(p.ObjectID, backup: false, kill: false);
                n++;
            }
            _state.Clear();
            if (n > 0) Log.Info($"Removed {n} fake player(s)");
            return n == 0 ? "No fake players" : $"Removed {n}";
        }

        // ============================================================================================ life and stats

        internal static string Kill(VPlayer p)
        {
            if (p == null) return "It's gone";
            if (p.LifeCycle != VCreatureLifeCycle.Alive) return p.ActorName + " isn't alive";
            p.ForcedDying();
            Log.Info($"Killed {p.ActorName}");
            return "Killed " + p.ActorName;
        }

        /// <summary>Back to life where it is (a fake player: next to you), full health and stamina, no radiation, as the
        /// game's revive command does.</summary>
        internal static string Revive(VPlayer p)
        {
            if (p == null || p.VRoom == null) return "It's gone";
            if (p.LifeCycle != VCreatureLifeCycle.Dead) return p.ActorName + " isn't dead" + (p.LifeCycle == VCreatureLifeCycle.Dying || p.LifeCycle == VCreatureLifeCycle.ForceDying ? " yet" : "");
            if (!p.VRoom.CanReviveCheat()) return "This level is over: no reviving";
            PosWithRot pos = p.Position;
            if (p.IsDummy && TryGetHost(out var host, out _)) pos = SpotNear(host, 0);
            if (!p.Revive(pos)) return "The game couldn't revive " + p.ActorName;
            var stat = p.StatControlUnit;
            if (stat != null)
            {
                stat.AdjustHP(0L, full: true);
                stat.RecoverStamina(stat.GetSpecificStatValue(StatType.Stamina));
                stat.AdjustConta(0);
            }
            // Monsters notice it again (the revive command does the same).
            var revived = p;
            p.VRoom.IterateAllMonster(m =>
            {
                if (m.IsAliveStatus()) m.AIControlUnit?.OnSightIn(revived);
            });
            Log.Info($"Revived {p.ActorName}");
            return "Revived " + p.ActorName;
        }

        internal static int HpPct(VPlayer p)
        {
            var stat = p?.StatControlUnit;
            if (stat == null) return 0;
            long max = stat.GetSpecificStatValue(StatType.HP);
            return max > 0 ? (int)Math.Round(stat.GetCurrentHP() * 100.0 / max) : 0;
        }

        internal static int RadPct(VPlayer p)
        {
            var stat = p?.StatControlUnit;
            if (stat == null) return 0;
            long max = MaxConta;
            return max > 0 ? (int)Math.Round(stat.GetCurrentConta() * 100.0 / max) : 0;
        }

        /// <summary>Health to pct of its maximum (1-100: 0 isn't a way to die, Kill is).</summary>
        internal static string SetHp(VPlayer p, int pct)
        {
            var stat = p?.StatControlUnit;
            if (stat == null || !p.IsAliveStatus()) return "Only a living player has health";
            pct = Mathf.Clamp(pct, 1, 100);
            long max = stat.GetSpecificStatValue(StatType.HP);
            long target = Math.Max(1L, max * pct / 100);
            stat.AdjustHP(target - stat.GetCurrentHP());
            return $"{p.ActorName}: health {pct}%";
        }

        /// <summary>Radiation to pct (0-99: 100 % kills, Kill is the way to die).</summary>
        internal static string SetRad(VPlayer p, int pct)
        {
            var stat = p?.StatControlUnit;
            if (stat == null || !p.IsAliveStatus()) return "Only a living player has radiation";
            pct = Mathf.Clamp(pct, 0, 99);
            stat.AdjustConta(pct);
            return $"{p.ActorName}: radiation {pct}%";
        }

        /// <summary>Its voice "talks" for seconds (as the game's fakevoice command): monsters that hear voices hear it.</summary>
        internal static string Talk(VPlayer p, float seconds)
        {
            if (p == null || !p.IsAliveStatus()) return "Only a living player can talk";
            p.SetCheatVoicePeak(TalkPeak, (long)(seconds * 1000f));
            return $"{p.ActorName} talks for {seconds:0} s";
        }

        // ============================================================================================ moving

        internal static string Bring(VPlayer p)
        {
            if (!Valid(p, out _) || !TryGetHost(out var host, out _)) return "It's gone";
            if (!p.IsAliveStatus()) return "Only a living player can move";
            p.Teleport(SpotNear(host, p.ObjectID), TeleportReason.System);
            return $"{p.ActorName} is next to you";
        }

        internal static bool Following(VPlayer p) => p != null && _state.TryGetValue(p, out var s) && s.Follow;

        internal static string SetFollow(VPlayer p, bool on)
        {
            if (!Valid(p, out _)) return "It's gone";
            var s = StateOf(p);
            s.Follow = on;
            s.NextFollowAt = 0f;
            s.FollowFails = 0;
            s.Path = null;
            s.FailLogged = false;
            if (!on) StopWalking(p, s);
            return on ? $"{p.ActorName} follows you" : $"{p.ActorName} stays";
        }

        /// <summary>Every frame (Panel.Update): followers walk to you, along the server's navmesh.</summary>
        internal static void Tick()
        {
            if (_state.Count == 0) return;
            if (!TryGetHost(out var host, out var room))
            {
                _state.Clear();
                return;
            }
            if (!ReferenceEquals(room, _stateRoom))
            {
                // Another room: its fake players stayed behind (and went with it).
                _stateRoom = room;
                _state.Clear();
                return;
            }
            float now = Time.unscaledTime;
            _scratch.Clear();
            foreach (var kv in _state)
            {
                var p = kv.Key;
                var s = kv.Value;
                if (!Valid(p, out var r) || !ReferenceEquals(r, room)) { _scratch.Add(p); continue; }
                if (!s.Follow || now < s.NextFollowAt || !p.IsAliveStatus()) continue;
                s.NextFollowAt = now + FollowInterval;
                try { FollowStep(p, s, host); }
                catch (Exception e)
                {
                    s.Follow = false;
                    Log.Error($"{p.ActorName} can't follow you: " + e);
                }
            }
            foreach (var p in _scratch) _state.Remove(p);
            _scratch.Clear();
        }

        /// <summary>One step toward you, sent as a player's game sends its moves (MoveStartReq: from where it is, toward
        /// where it'll be in FollowInterval), along a route on the navmesh. The server checks it like any player's move
        /// (speed, distance) and every game sees it walk.</summary>
        static void FollowStep(VPlayer p, State s, VPlayer host)
        {
            var move = p.MovementControlUnit;
            if (move == null) return;
            Vector3 me = host.PositionVector, at = p.PositionVector;
            float dist = Flat(me - at).magnitude;
            if (dist <= FollowNear && Mathf.Abs(me.y - at.y) < 2f)
            {
                StopWalking(p, s);
                s.FollowFails = 0;
                return;
            }
            if (dist > FollowTeleportBeyond || s.FollowFails >= 6)
            {
                StopWalking(p, s);
                p.Teleport(SpotNear(host, p.ObjectID), TeleportReason.System);
                s.FollowFails = 0;
                s.Path = null;
                Log.Info($"{p.ActorName} was {(dist > FollowTeleportBeyond ? "too far" : "stuck")}: brought over");
                return;
            }
            float now = Time.unscaledTime;
            if (s.Path == null || s.PathIdx >= s.Path.Count || now >= s.NextRouteAt)
            {
                s.NextRouteAt = now + RouteInterval;
                // A spot next to you on its side, not your own.
                Vector3 side = Flat(at - me);
                Vector3 goal = me + (side.sqrMagnitude > 0.01f ? side.normalized : Vector3.forward) * (FollowNear - 0.8f);
                PathFindResult route = null;
                try { route = Hub.s.navman.GetRoute(at, goal, shouldArriveEndPoint: false); }
                catch (Exception) { /* below */ }
                if (route == null || !route.Success || route.PathPoints == null || route.PathPoints.Count == 0)
                {
                    s.FollowFails++;
                    return;
                }
                s.Path = new List<Vector3>(route.PathPoints);
                s.PathIdx = 0;
            }

            // How far it walks in one interval (a little under its speed: the server allows it 1.1x).
            float speed = p.StatControlUnit != null ? p.StatControlUnit.GetMoveSpeed() : 3.5f;
            float budget = Mathf.Max(0.5f, speed) * FollowInterval * 0.95f;
            Vector3 next = at;
            while (budget > 0.001f && s.PathIdx < s.Path.Count)
            {
                Vector3 wp = s.Path[s.PathIdx];
                float d = Flat(wp - next).magnitude;
                if (d <= budget)
                {
                    next = wp;
                    budget -= d;
                    s.PathIdx++;
                }
                else
                {
                    next = Vector3.MoveTowards(next, wp, budget);
                    budget = 0f;
                }
            }
            Vector3 step = Flat(next - at);
            if (step.sqrMagnitude < 0.0001f)
            {
                s.FollowFails++;
                return;
            }
            float yaw = Quaternion.LookRotation(step).eulerAngles.y;
            var res = move.DirectMoveStart(new MoveStartReq
            {
                transID = ++s.TransId,
                actorMoveType = ActorMoveType.Walk,
                basePositionPrev = new PosWithRot { pos = at, yaw = yaw },
                basePositionCurr = new PosWithRot { pos = at, yaw = yaw },
                basePositionFuture = new PosWithRot { pos = next, yaw = yaw },
                futureTime = (long)(FollowInterval * 1000f),
                targetActorID = 0,
                pitch = 0f,
            });
            if (res == MsgErrorCode.Success)
            {
                s.Moving = true;
                s.FollowFails = 0;
                return;
            }
            s.FollowFails++;
            if (!s.FailLogged)
            {
                s.FailLogged = true;
                Log.Warn($"{p.ActorName}: the server refused a step ({res})");
            }
        }

        static void StopWalking(VPlayer p, State s)
        {
            if (!s.Moving) return;
            s.Moving = false;
            var move = p.MovementControlUnit;
            if (move == null || !p.IsAliveStatus()) return;
            try
            {
                move.DirectMoveStop(new MoveStopReq { actorMoveType = ActorMoveType.Walk, prevPos = p.Position, currentPos = p.Position });
            }
            catch (Exception e) { Log.Warn($"{p.ActorName}: stopping failed: " + e.Message); }
        }

        // ============================================================================================ helpers

        static State StateOf(VPlayer p)
        {
            if (!ReferenceEquals(_stateRoom, p.VRoom))
            {
                _state.Clear(); // the last room's fake players are gone with it
                _stateRoom = p.VRoom;
            }
            if (!_state.TryGetValue(p, out var s))
            {
                s = new State();
                _state[p] = s;
            }
            return s;
        }

        /// <summary>p is still in its room (not removed).</summary>
        static bool Valid(VPlayer p, out IVroom room)
        {
            room = p?.VRoom;
            return room != null && ReferenceEquals(room.FindPlayerByObjectID(p.ObjectID), p);
        }

        /// <summary>A free spot 1.5-2.5 m from you on the (client) navmesh, in sight; else the game's own admin spot (2 m
        /// ahead of you). salt spreads several of them around you.</summary>
        static PosWithRot SpotNear(VPlayer host, int salt)
        {
            Vector3 c = host.PositionVector;
            var nav = Hub.s.navman;
            if (nav != null)
            {
                float start = (salt * 47) % 360;
                for (int k = 0; k < 12; k++)
                {
                    float a = (start + k * 30f) * Mathf.Deg2Rad;
                    float r = k < 8 ? 2f : 1.5f;
                    Vector3 q = c + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r;
                    Vector3 n;
                    try { n = nav.GetNearestPointOnNavMesh(q, 1f); }
                    catch (Exception) { break; }
                    if (!NavMeshConstants.IsValid(n) || Mathf.Abs(n.y - c.y) > 1.2f) continue;
                    if (!PhysicsUtility.CheckBlockByWall(c + Vector3.up * 1.2f, n + Vector3.up * 1.2f)) continue;
                    Vector3 face = Flat(c - n);
                    return new PosWithRot
                    {
                        pos = n,
                        yaw = face.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(face).eulerAngles.y : 0f,
                    };
                }
            }
            return Hub.s.vworld.CreateAdminSpawnPos(host.Position, randomYaw: true);
        }

        internal static long MaxConta
        {
            get
            {
                var consts = Hub.s?.dataman?.ExcelDataManager?.Consts;
                return consts != null && consts.C_MaxContaValue > 0 ? consts.C_MaxContaValue : 10000L;
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }

    static class Log
    {
        const string Tag = "[FakePlayers] ";
        public static void Info(string message) => Debug.Log(Tag + message);
        public static void Warn(string message) => Debug.LogWarning(Tag + message);
        public static void Error(string message) => Debug.LogError(Tag + message);
    }
}
