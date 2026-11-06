// MIMESIS Patches - everyone starts somewhere else in a special level (shared by Traitor and Deathmatch)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Compiled into each patch that scatters its players (each gets its own copy of this static state). In the patch's
// level (TramMode.Active), every player who enters before it started appears at their own random spot inside the
// building, far from each other and clear of monsters, instead of a seat in the tram: the game asks for a start point
// as each player enters a room (IVroom.GetPlayerStartPoint; the patch's hook calls Next), so they are there from their
// first frame and the arrival cutscene plays over it. The level's monsters wait meanwhile (TramMode pauses its AI until
// the arrival ends, as the game does from the cutscene on), and each player's game draws the level as from inside
// (TramMode.ClientTick: the game decides that once, before the avatar exists). Spots: the level's own spawn spots for
// scrap inside the building, on the navmesh; a level with none inside falls back to the ones outdoors, then to the tram.
// Whoever is still in the tram when the level starts (came in without a spot: the hook off after a game update, no
// spots) is moved then (MoveStragglers), and one of the group who comes in late, after the start, a moment after they
// load (OnPlayerLoaded).

using System;
using System.Collections.Generic;
using ReluProtocol;
using UnityEngine;

namespace MimesisPatches
{
    static class TramScatter
    {
        // Outdoors only (a level with no spots inside): far enough that nobody sees who walks out of the tram, near enough
        // to walk back in time. Inside, the building is reached through doors that teleport you: how far a spot is from
        // the tram in a straight line says nothing there.
        const float MinFromTram = 25f, MinFromTramFallback = 10f, MaxFromTram = 120f, MaxFromTramFar = 250f;
        const float ClearOfMonsterM = 10f;   // from a monster's spawn spot, or one already about
        const int PlanSize = 24;             // spots planned per level; more players than that share them, a little apart

        internal static Func<bool> Verbose = () => false;
        /// <summary>Set by the patch: with fewer players travelling, its level runs as a normal one (Traitor: 3), so they
        /// start in the tram as usual. Fake players count when testing (TramMode.CountFakePlayers).</summary>
        internal static int MinTravellers = 1;

        static DungeonRoom _room;   // the level the plan is for
        static readonly List<(Vector3 pos, bool indoor)> _plan = new List<(Vector3 pos, bool indoor)>();
        static int _handedOut;
        internal static int Moved;  // players moved in this level (came in without a spot: at the start, or late)
        static readonly System.Random _rng = new System.Random();

        /// <summary>IVroom.GetPlayerStartPoint prefix (the patch's hook): where the player entering now appears, for our
        /// level before it started; null = the game's own (a seat in the tram).</summary>
        internal static SpawnPointData Next(IVroom room)
        {
            if (!(room is DungeonRoom level) || !ReferenceEquals(level, TramMode.Active) || TramMode.Started || !Wanted(level)) return null;
            if (!TakeSpot(level, out Vector3 pos, out bool indoor)) return null;
            if (Verbose()) TramMode.LogInfo($"A player starts at {pos} ({(indoor ? "inside" : "outside")}, {Vector3.Distance(pos, TramCenter(level)):0} m from the tram)");
            return new SpawnPointData(_handedOut, new PosWithRot { pos = pos, yaw = _rng.Next(0, 360) }, indoor);
        }

        /// <summary>The level started: anyone still in the tram (came in without a spot) is moved to one now.</summary>
        internal static void MoveStragglers(DungeonRoom room, List<VPlayer> players)
        {
            if (!Wanted(room)) return;
            foreach (var p in players)
                if (p != null && InTram(room, p.PositionVector)) MoveToSpot(room, p, "was still in the tram");
        }

        /// <summary>A player finished loading a room (the patch's T5/D5 hook): one of the group coming into our level after
        /// it started (their game was slow: the game starts without them after a while) appears in the tram, and is moved
        /// to a spot of their own a moment later (their game sets up their avatar with the answer to this load, and a
        /// move before that would be lost on it). Someone joining mid-level isn't one of the group: the game makes them
        /// spectate.</summary>
        internal static void OnPlayerLoaded(IVroom room, VPlayer p)
        {
            if (!(room is DungeonRoom level) || !ReferenceEquals(level, TramMode.Active) || !TramMode.Started || p == null || p.IsDummy) return;
            if (!Wanted(level) || !Travels(level, p)) return;
            level.AddEventTimer(() =>
            {
                try
                {
                    if (!ReferenceEquals(level.FindPlayerByObjectID(p.ObjectID), p) || !p.IsAliveStatus() || !InTram(level, p.PositionVector)) return;
                    MoveToSpot(level, p, "came in late");
                }
                catch (Exception e) { TramMode.LogInfo("Moving a late player failed: " + e.Message); }
            }, 1500);
        }

        static void MoveToSpot(DungeonRoom room, VPlayer p, string why)
        {
            if (!TakeSpot(room, out Vector3 pos, out _))
            {
                TramMode.LogInfo($"No spot to move {p.ActorName} to: they start in the tram");
                return;
            }
            try
            {
                // A teleport also makes their game draw the level as from where they are now (inside or out).
                p.Teleport(new PosWithRot { pos = pos, yaw = _rng.Next(0, 360) }, TeleportReason.System);
                Moved++;
                TramMode.LogInfo($"{p.ActorName} {why}: moved to their spot");
            }
            catch (Exception e) { TramMode.LogInfo($"Moving {p.ActorName} failed: " + e); }
        }

        /// <summary>Enough players travel for the patch's level to run (or fake players count, testing).</summary>
        static bool Wanted(IVroom room) => TramMode.CountFakePlayers() || Travellers(room) >= MinTravellers;

        static bool Travels(IVroom room, VPlayer p)
        {
            if (p.IsHost) return true;
            try { return room.RoomManager.GetGameSessionInfo().FixedPlayerSteamIDs.ContainsKey(p.SteamID); }
            catch (Exception) { return false; }
        }

        /// <summary>The next spot of this level's plan (made at the first call for a new level).</summary>
        static bool TakeSpot(DungeonRoom room, out Vector3 pos, out bool indoor)
        {
            pos = default;
            indoor = false;
            if (!ReferenceEquals(_room, room))
            {
                _room = room;
                _handedOut = 0;
                Moved = 0;
                MakePlan(room);
            }
            if (_plan.Count == 0) return false;
            (pos, indoor) = _plan[_handedOut % _plan.Count];
            int round = _handedOut / _plan.Count;
            _handedOut++;
            if (round > 0)
            {
                // More players than spots: the same spots again, a little to the side.
                Vector3 side = Quaternion.Euler(0f, _rng.Next(0, 360), 0f) * Vector3.forward * (1.2f * round);
                Vector3 n = Hub.s.vworld.FindNearestPoly(pos + side, 1.5f);
                if (n != NavMeshConstants.INVALID_POSITION && Mathf.Abs(n.y - pos.y) < 1f) pos = n;
            }
            return true;
        }

        static void MakePlan(DungeonRoom room)
        {
            _plan.Clear();
            Vector3 tram = TramCenter(room);
            int need = Travellers(room);
            var monsters = MonsterSpots(room);
            // Inside the building, clear of monsters (two players to a spot, a little apart, rather than next to one);
            // any spot inside if none is clear; a level with no spots inside: outside, near enough to the tram to walk back.
            var spots = Spots(room, tram, true, monsters, MinFromTramFallback, float.MaxValue);
            if (spots.Count == 0) spots = Spots(room, tram, true, null, MinFromTramFallback, float.MaxValue);
            if (spots.Count == 0) spots = Spots(room, tram, false, monsters, MinFromTram, MaxFromTram);
            if (spots.Count == 0) spots = Spots(room, tram, false, monsters, MinFromTramFallback, MaxFromTramFar);
            if (spots.Count == 0)
            {
                TramMode.LogInfo("No spots to start the players at: they start in the tram");
                return;
            }
            // Each next spot as far as possible from the ones taken (a little random among the best three).
            var chosen = new List<(Vector3 pos, bool indoor)> { spots[_rng.Next(spots.Count)] };
            while (chosen.Count < PlanSize && chosen.Count < spots.Count)
            {
                var ranked = new List<((Vector3 pos, bool indoor) s, float d)>();
                foreach (var s in spots)
                {
                    float d = float.MaxValue;
                    foreach (var c in chosen) d = Mathf.Min(d, Vector3.Distance(s.pos, c.pos));
                    if (d > 0.5f) ranked.Add((s, d));
                }
                if (ranked.Count == 0) break;
                ranked.Sort((a, b) => b.d.CompareTo(a.d));
                chosen.Add(ranked[_rng.Next(Mathf.Min(3, ranked.Count))].s);
            }
            // The players expected get the most spread-out spots, in a random order; anyone else the next ones.
            int head = Mathf.Min(need, chosen.Count);
            var first = chosen.GetRange(0, head);
            for (int i = first.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (first[i], first[j]) = (first[j], first[i]);
            }
            _plan.AddRange(first);
            _plan.AddRange(chosen.GetRange(head, chosen.Count - head));
            int inside = 0;
            foreach (var s in _plan) if (s.indoor) inside++;
            TramMode.LogInfo($"Start spots for {need} player(s): {_plan.Count} planned, {inside} inside the building");
        }

        /// <summary>The level's spawn spots for scrap (inside the building only, or anywhere), on the navmesh, out of the
        /// tram, at least ClearOfMonsterM from every spot in avoid (null: no such check).</summary>
        static List<(Vector3 pos, bool indoor)> Spots(DungeonRoom room, Vector3 tram, bool insideOnly, List<Vector3> avoid, float minFromTram, float maxFromTram)
        {
            var list = new List<(Vector3 pos, bool indoor)>();
            foreach (var d in room._spawnedActorDatas.Values)
            {
                if (d == null || d.MarkerType != MapMarkerType.LootingObject) continue;
                if (insideOnly && !d.IsIndoor) continue;
                Vector3 p = d.PosVector;
                float fromTram = Vector3.Distance(p, tram);
                if (InTram(room, p) || fromTram < minFromTram || fromTram > maxFromTram) continue;
                Vector3 n = Hub.s.vworld.FindNearestPoly(p, 1.5f);
                if (n == NavMeshConstants.INVALID_POSITION || Mathf.Abs(n.y - p.y) > 2f || InTram(room, n)) continue;
                if (avoid != null && avoid.Exists(m => Vector3.Distance(m, n) < ClearOfMonsterM)) continue;
                list.Add((n, d.IsIndoor));
            }
            return list;
        }

        /// <summary>Where monsters are or will be: the map's monster spawn spots and the monsters already about.</summary>
        static List<Vector3> MonsterSpots(DungeonRoom room)
        {
            var list = new List<Vector3>();
            foreach (var d in room._spawnedActorDatas.Values)
            {
                if (d == null) continue;
                if (d.MarkerType == MapMarkerType.Creature || d.MarkerType == MapMarkerType.SpecialMonster || d.MarkerType == MapMarkerType.GroupCreature)
                    list.Add(d.PosVector);
            }
            room.IterateAllMonster(a => { if (a != null) list.Add(a.PositionVector); });
            return list;
        }

        /// <summary>How many players come along (the session's fixed players: the group that left the tram), at least 1.</summary>
        static int Travellers(IVroom room)
        {
            try { return Mathf.Max(1, room.RoomManager.GetGameSessionInfo().FixedPlayerSteamIDs.Count); }
            catch (Exception) { return 4; }
        }

        internal static Vector3 TramCenter(DungeonRoom room)
        {
            var sum = Vector3.zero;
            int n = 0;
            foreach (var sp in room._playerStartSpawnPoints.Values)
            {
                if (sp == null) continue;
                sum += sp.Pos.toVector3();
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        internal static bool InTram(DungeonRoom room, Vector3 p)
        {
            foreach (var v in room._playerStartingVolumes)
                if (v != null && v.IsInBounds(p)) return true;
            return false;
        }
    }
}
