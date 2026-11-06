// MIMESIS Traitor - the traitor job, on the host
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// At a traitor destination (TramMode: the players picked it on the tram's screen):
//   1. everyone arrives at a different random spot inside the building, far from the tram and from each other, not in
//      the tram (Common/TramScatter.cs: the game puts them there as they enter the level; from the tram you'd see who
//      walks out of it: spread out, a stranger in a corridor could be a friend, a mimic or the traitor);
// once its arrival cutscene is over:
//   2. one living player becomes the traitor; only they are told. The others learn that there is one;
//   3. monsters (mimics included) ignore the traitor: they never pick them as a target, never hold aggro on them,
//      and their attacks pass through them (Patches T6-T9). Players can still hurt and kill the traitor, and the
//      traitor them (the game's own friendly fire);
//   4. the tram can't leave for Settings.LockSec (else the traitor pulls the lever at once and strands everyone):
//      the lever springs back, as the tram's lever does when the team lacks the money (Patches T12);
//   5. the end: the traitor killed by a player -> the team gets Settings.RewardPct % of the stage's repair cost;
//      every crew member dead -> the traitor wins; the level ends with the traitor alive -> they got away. The
//      traitor is revealed to everyone in every case; a crew member killed by a player is announced as "not the
//      traitor".

using System;
using System.Collections.Generic;
using MimesisPatches;
using ReluProtocol;
using UnityEngine;

namespace Traitor
{
    static class Game
    {
        const int MinPlayers = 3;

        static DungeonRoom _room;
        internal static VPlayer Traitor;
        static string _traitorName = "";
        static long _lockUntilMs;
        static bool _over;
        static int _reward;
        static long _lastLockMsgMs;
        static readonly System.Random _rng = new System.Random();

        internal static long NowMs => Hub.s != null && Hub.s.timeutil != null ? Hub.s.timeutil.GetCurrentTickMilliSec() : 0L;

        /// <summary>The traitor of the running traitor level (monsters ignore exactly this player object).</summary>
        internal static bool IsTraitor(VActor a) => a != null && !ReferenceEquals(Traitor, null) && ReferenceEquals(a, Traitor) && !ReferenceEquals(_room, null);

        internal static bool Running => !ReferenceEquals(_room, null) && !_over;

        /// <summary>A new level (any): the last one's job is over.</summary>
        internal static void Reset()
        {
            _room = null;
            Traitor = null;
            _traitorName = "";
            _over = false;
            _lockUntilMs = 0;
        }

        // ============================================================================================ start

        internal static void OnLevelStarted(DungeonRoom room)
        {
            Reset();
            var players = TramMode.Players(room, aliveOnly: true);
            if (players.Count < MinPlayers && !(Settings.FakePlayers && players.Count >= 1))
            {
                TramMode.Banner(room, null, "NO TRAITOR", $"A traitor job needs {MinPlayers} players: this is a normal run.", "#c0c0c0", 6f);
                Log.Info($"Traitor level with {players.Count} player(s): no traitor");
                return;
            }
            // Only a player whose game shows them their role can be the traitor (on a traitor trip everyone has the patch;
            // someone who joined since may not).
            var candidates = players.FindAll(TramMode.HasPatch);
            if (candidates.Count == 0)
            {
                Log.Warn("Nobody in this traitor level has the patch's player side: a normal run");
                return;
            }
            _room = room;
            // Everyone came in at their own spot inside the building (TramScatter, T14); anyone still in the tram goes now.
            TramScatter.MoveStragglers(room, players);
            Traitor = candidates[_rng.Next(candidates.Count)];
            _traitorName = Traitor.ActorName;
            _reward = TramMode.StageMoney(room, Settings.RewardPct / 100f, 20);
            _lockUntilMs = Settings.LockSec > 0 ? NowMs + Settings.LockSec * 1000L : 0;
            ForgetTraitor(room, Traitor);

            string lockText = Settings.LockSec > 0 ? $" The tram can't leave for {LockText(Settings.LockSec)}." : "";
            foreach (var p in players)
            {
                bool isTraitor = ReferenceEquals(p, Traitor);
                TramMode.Send(p, "role", ("r", isTraitor ? "traitor" : "crew"), ("lock", Settings.LockSec.ToString()));
                if (isTraitor)
                    TramMode.Banner(room, p, "YOU ARE THE TRAITOR",
                        "Monsters ignore you. Keep the others from getting back with the loot, and don't get caught: " +
                        $"if they kill you, they get {_reward} gold.{lockText}", "#ff3b3b", 10f);
                else
                    TramMode.Banner(room, p, "THERE IS A TRAITOR AMONG YOU",
                        $"Monsters ignore them. Catch the traitor for {_reward} gold, but kill a friend and you're a player " +
                        $"down.{lockText}", "#ffb347", 10f);
            }
            // The host may be crew: the log doesn't name the traitor (unless asked for, with VerboseLog).
            Log.Info($"Traitor job: {players.Count} players scattered, reward {_reward}, tram locked {Settings.LockSec} s" +
                     (Settings.VerboseLog ? $"; the traitor is {_traitorName} (#{Traitor.ObjectID})" : ""));
        }

        /// <summary>Monsters that already noticed the traitor drop them now.</summary>
        static void ForgetTraitor(IVroom room, VPlayer traitor)
        {
            room.IterateAllMonster(a =>
            {
                if (!(a is VMonster m) || m.AIControlUnit == null) return;
                try
                {
                    m.AIControlUnit.m_AggroDict.Remove(traitor.ObjectID);
                    var bt = m.AIControlUnit._btState;
                    if (bt != null && ReferenceEquals(bt.Target, traitor)) bt.ResetTargetActor();
                }
                catch (Exception e) { Log.ErrorOnce("forget", "Clearing a monster's aggro failed: " + e); }
            });
        }

        // ============================================================================================ during

        /// <summary>A player died in the traitor level.</summary>
        internal static void OnPlayerDied(DungeonRoom room, VPlayer victim, int attackerId)
        {
            if (!Running || !ReferenceEquals(room, _room)) return;
            var killer = room.FindActorByObjectID(attackerId) as VPlayer;
            if (ReferenceEquals(victim, Traitor))
            {
                _over = true;
                if (killer != null && !ReferenceEquals(killer, Traitor))
                {
                    bool paid = TramMode.AddTeamMoney(_reward);
                    TramMode.Banner(room, null, "TRAITOR CAUGHT",
                        $"{_traitorName} was the traitor. {killer.ActorName} got them: {(paid ? "+" + _reward + " gold for the team" : "well done")}.",
                        "#5cff7a", 9f);
                    Log.Info($"The traitor {_traitorName} was killed by {killer.ActorName}: +{_reward} gold ({(paid ? "paid" : "no maintenance room")})");
                }
                else
                {
                    TramMode.Banner(room, null, "THE TRAITOR IS DEAD", $"{_traitorName} was the traitor.", "#5cff7a", 8f);
                    Log.Info($"The traitor {_traitorName} died ({(killer != null ? "by their own hand" : "not by a player")})");
                }
                SendRoles(room);
                return;
            }
            if (killer != null && !ReferenceEquals(killer, victim))
                TramMode.Banner(room, null, $"{victim.ActorName.ToUpperInvariant()} WAS NOT THE TRAITOR", "", "#ffb347", 5f);
            int crewAlive = 0;
            foreach (var p in TramMode.Players(room, aliveOnly: true))
                if (!ReferenceEquals(p, Traitor)) crewAlive++;
            if (crewAlive == 0)
            {
                _over = true;
                TramMode.Banner(room, null, "THE TRAITOR WINS", $"{_traitorName} was the traitor.", "#ff3b3b", 9f);
                Log.Info($"Every crew member is dead: the traitor {_traitorName} wins");
                SendRoles(room);
            }
        }

        /// <summary>The level ends (the tram left, or time ran out).</summary>
        internal static void OnLevelEnded(DungeonRoom room)
        {
            if (!Running || !ReferenceEquals(room, _room)) return;
            _over = true;
            bool alive = Traitor != null && Traitor.IsAliveStatus();
            TramMode.Banner(room, null, alive ? "THE TRAITOR GOT AWAY" : "THE TRAITOR IS DEAD", $"{_traitorName} was the traitor.", alive ? "#ff3b3b" : "#5cff7a", 8f);
            Log.Info($"Traitor level over: {_traitorName} {(alive ? "got away" : "is dead")}");
            SendRoles(room);
        }

        /// <summary>A player left the level: the traitor leaving ends the job (they're revealed).</summary>
        internal static void OnPlayerLeft(IVroom room, int actorId)
        {
            if (!Running || !ReferenceEquals(room, _room) || Traitor == null || Traitor.ObjectID != actorId) return;
            _over = true;
            TramMode.Banner(room, null, "THE TRAITOR LEFT", $"{_traitorName} was the traitor.", "#c0c0c0", 7f);
            Log.Info($"The traitor {_traitorName} left the level");
            SendRoles(room);
        }

        /// <summary>Everyone's role on screen is cleared (the job is over).</summary>
        static void SendRoles(IVroom room) => TramMode.SendRoom(room, "role", ("r", "none"));

        /// <summary>A late joiner or a reconnect: their role again.</summary>
        internal static void OnPlayerLoaded(IVroom room, VPlayer p)
        {
            if (!Running || !ReferenceEquals(room, _room) || p == null) return;
            bool isTraitor = Traitor != null && Traitor.SteamID == p.SteamID && p.SteamID != 0;
            if (isTraitor) Traitor = p; // a reconnect is a new player object
            TramMode.SendSoon(room, p, "role", () => !Running ? null : new[] { ("r", ReferenceEquals(Traitor, p) ? "traitor" : "crew"), ("lock", "0") });
        }

        // ============================================================================================ the lever

        /// <summary>The tram's lever is moving to "leave": false = it can't leave yet (Patches T12 refuses the move, and
        /// the lever springs back).</summary>
        internal static bool CanLeave(DungeonRoom room)
        {
            if (!Running || !ReferenceEquals(room, _room) || _lockUntilMs == 0) return true;
            long left = _lockUntilMs - NowMs;
            if (left <= 0) return true;
            if (NowMs - _lastLockMsgMs > 2000)
            {
                _lastLockMsgMs = NowMs;
                TramMode.Banner(room, null, "THE TRAM IS LOCKED", $"It can leave in {LockText((int)((left + 999) / 1000))}.", "#ffb347", 3f);
            }
            if (Settings.VerboseLog) Log.Info($"Lever pulled {left / 1000} s too early: the tram stays");
            return false;
        }

        static string LockText(int sec) => sec >= 60 ? $"{sec / 60}:{sec % 60:00}" : $"{sec} s";
    }
}
