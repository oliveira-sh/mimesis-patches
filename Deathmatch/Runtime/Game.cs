// MIMESIS Deathmatch - the deathmatch job, on the host
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// When the tram arrives at a deathmatch destination (TramMode: the players picked it on the tram's screen):
//   - everyone arrives at a different random spot inside the building, far from the tram and from each other, not in
//     the tram (Common/TramScatter.cs: the game puts them there as they enter the level);
//   - from the start, no monster but mimics spawns there: the level's monster budget is zero, its special and group
//     spawns are dropped, its fixed monsters and traps stay unspawned (Patches D6), and anything else that would
//     create a non-mimic monster is refused (D7). Mimics come as usual: they're the only thing to fear besides
//     each other;
//   - once the arrival cutscene is over, every player is lent the upgraded shotgun (the double barrel, item 1500) at
//     full durability with both barrels loaded (2 shells), in their inventory (at their feet if it's full). Lent: when
//     the level ends, each one goes, wherever it is (a hand, the floor, the tram, a stash hanger), and one that turns
//     up in a later room (someone left the game holding it and came back) goes there;
//   - every player's death in the level pays the whole team StageMoney (Settings.RewardPct % of the stage's repair
//     cost), once per player per level, and the dead player loses everything they carried (D8: their inventory isn't
//     dropped, the game clears it). What's in the tram is never touched. Someone joining mid-level is killed by the
//     game itself to spectate: no shotgun, no pay (else a rejoin would pay again and again);
//   - the level ends as usual (lever or time); the total is shown at the end.

using System;
using System.Collections.Generic;
using Bifrost.Cooked;
using MimesisPatches;
using ReluProtocol;
using ReluProtocol.Enum;
using UnityEngine;

namespace Deathmatch
{
    static class Game
    {
        internal const int ShotgunItem = 1500;   // "STRING_ITEM_NAME_1500": the double-barrel shotgun (the 1000's upgrade)
        internal const int Shells = 2;           // its max gauge: both barrels

        static DungeonRoom _room;
        static bool _started, _over;
        static int _reward, _deaths, _paid;
        static readonly HashSet<ulong> _armed = new HashSet<ulong>();
        static readonly HashSet<ulong> _paidFor = new HashSet<ulong>();   // whose death paid already (once per player per level)
        static readonly HashSet<long> _lent = new HashSet<long>();        // the shotguns lent this session (item ids), taken back
        static IVroom _sweptRoom;                                         // the last room checked for a lent shotgun
        static readonly HashSet<int> _refusedLogged = new HashSet<int>();
        static readonly Dictionary<int, bool> _isMimic = new Dictionary<int, bool>();

        /// <summary>AppDomain data other patches read on the host (every patch is its own assembly): the DungeonRoom of the
        /// deathmatch while it runs, else null. Hallucinations sends none there: dead players' fake attackers would make
        /// the living waste their shells.</summary>
        internal const string FairFightRoomKey = "MimesisPatches.FairFightRoom";

        /// <summary>A new level: is it ours (TramMode decided in the same constructor hook).</summary>
        internal static void OnDungeonCreated(DungeonRoom room)
        {
            _room = ReferenceEquals(TramMode.Active, room) ? room : null;
            _started = false;
            _over = false;
            _deaths = 0;
            _paid = 0;
            _armed.Clear();
            _paidFor.Clear();
            AppDomain.CurrentDomain.SetData(FairFightRoomKey, null);
            if (_room != null) Log.Info("This level is a deathmatch: only mimics will spawn");
        }

        /// <summary>The deathmatch level (from its creation: monsters are kept out from the first spawn).</summary>
        internal static bool IsOurs(IVroom room) => room != null && !ReferenceEquals(_room, null) && ReferenceEquals(room, _room);

        /// <summary>Takes part in this level: the host and the players who started it, the game's own test for who stays
        /// alive (anyone else joining mid-level is killed 3 s after loading in, to spectate: IVroom.ScheduleBargeInKill);
        /// fake players only when testing.</summary>
        static bool TakesPart(IVroom room, VPlayer p)
        {
            if (p == null) return false;
            if (p.IsDummy) return Settings.FakePlayers;
            if (p.IsHost) return true;
            try { return room.RoomManager.GetGameSessionInfo().FixedPlayerSteamIDs.ContainsKey(p.SteamID); }
            catch (Exception) { return true; }
        }

        internal static bool IsMimicMaster(int masterId)
        {
            if (_isMimic.TryGetValue(masterId, out bool yes)) return yes;
            try { yes = Hub.s.dataman.ExcelDataManager.GetMonsterInfo(masterId)?.IsMimic() ?? false; }
            catch (Exception) { yes = false; }
            _isMimic[masterId] = yes;
            return yes;
        }

        // ============================================================================================ spawning

        /// <summary>D6: before every spawn pass of our level: nothing but mimics.</summary>
        internal static void StripSpawns(DungeonRoom room)
        {
            room._normalMonsterThreatRemain = 0;
            room._normalMonsterThreatLimit = 0;
            if (room._specialMonsterSpawnGroups.Count > 0) room._specialMonsterSpawnGroups.Clear();
            if (room._groupSpawnDatas.Count > 0) room._groupSpawnDatas.Clear();
            foreach (var d in room._spawnedActorDatas.Values)
            {
                // Fixed monsters and traps of the map (spawned once at the start): marked as taken, never spawned.
                if (d != null && d.ActorID == 0 && d.MasterID != 0 &&
                    (d.MarkerType == MapMarkerType.Creature || d.MarkerType == MapMarkerType.SpecialMonster || d.MarkerType == MapMarkerType.GroupCreature) &&
                    !IsMimicMaster(d.MasterID))
                    d.SetActorID(-1);
            }
        }

        /// <summary>D7: the last guard: a non-mimic monster in our level is refused.</summary>
        internal static bool Refuse(IVroom room, int masterId)
        {
            if (!IsOurs(room) || IsMimicMaster(masterId)) return false;
            if (_refusedLogged.Add(masterId)) Log.Info($"Monster {masterId} kept out of the deathmatch");
            return true;
        }

        // ============================================================================================ start

        internal static void OnLevelStarted(DungeonRoom room)
        {
            if (!IsOurs(room)) return;
            _started = true;
            AppDomain.CurrentDomain.SetData(FairFightRoomKey, room);
            _reward = TramMode.StageMoney(room, Settings.RewardPct / 100f, 15);
            KillNonMimics(room);
            var players = TramMode.Players(room, aliveOnly: true);
            // Everyone came in at their own spot inside the building (TramScatter, D11); anyone still in the tram goes now.
            TramScatter.MoveStragglers(room, players);
            foreach (var p in players) Arm(room, p);
            foreach (var p in players)
                TramMode.Send(p, "dm", ("on", "1"), ("gold", _reward.ToString()));
            TramMode.Banner(room, null, "DEATHMATCH",
                $"No monsters here, only mimics. Everyone was lent a loaded double-barrel shotgun (it goes back at the end). Every death " +
                $"pays the team {_reward} gold, but the dead lose everything they carry. What's in the tram is safe.", TramMode.ColorHex, 10f);
            Log.Info($"Deathmatch started: {players.Count} player(s) armed, {_reward} gold per death");
        }

        /// <summary>A new hosting session (VWorld.RegisterCommand): item ids start over, so the lent ones are forgotten.</summary>
        internal static void OnNewSession()
        {
            _lent.Clear();
            _sweptRoom = null;
        }

        /// <summary>Anything but mimics that got in before the spawn rules (shouldn't happen) goes.</summary>
        static void KillNonMimics(IVroom room)
        {
            int n = 0;
            room.IterateAllMonster(a =>
            {
                if (a is VMonster m && m.IsAliveStatus() && !IsMimicMaster(m.MasterID))
                {
                    m.ForcedDying();
                    n++;
                }
            });
            if (n > 0) Log.Info($"{n} monster(s) removed from the deathmatch");
        }

        /// <summary>The upgraded shotgun, full durability, both barrels loaded: in their inventory, or at their feet.
        /// entering: the player object was just made (D12), before the room describes it to their game: no message of
        /// its own then (their game learns it with the rest), and not alive yet.</summary>
        static void Arm(DungeonRoom room, VPlayer p, bool entering = false)
        {
            if (p == null || (!entering && !p.IsAliveStatus()) || !TakesPart(room, p) || !_armed.Add(p.SteamID != 0 ? p.SteamID : (ulong)p.ObjectID)) return;
            var info = Hub.s.dataman.ExcelDataManager.GetItemInfo(ShotgunItem) as ItemEquipmentInfo;
            if (info == null)
            {
                Log.Error($"No item {ShotgunItem} in this game version: no shotguns");
                return;
            }
            var item = room.GetNewItemElement(ShotgunItem, isFake: false, itemCount: 1, durability: info.MaxDurability, gauge: Shells);
            if (item == null) return;
            _lent.Add(item.ItemID);
            var inv = p.InventoryControlUnit;
            if (inv != null && inv.HandleAddItem(item, out int slot, sync: !entering) == MsgErrorCode.Success)
            {
                if (!entering) inv.SelectSlotAfterAddItem(slot);
                if (Settings.VerboseLog) Log.Info($"{p.ActorName}: shotgun in slot {slot}");
                return;
            }
            int id = room.SpawnLootingObject(item, p.Position, p.IsIndoor, ReasonOfSpawn.Spawn);
            Log.Info($"{p.ActorName}: inventory full, shotgun at their feet ({(id != 0 ? "ok" : "failed")})");
        }

        /// <summary>D12: a player object was just made for a room, its inventory restored from their snapshot, and the room
        /// hasn't described it to their game yet. In the running deathmatch, one of the group back after a reconnect gets
        /// their shotgun if they had none; anywhere else, a lent shotgun they still hold goes. Their game then shows it
        /// right from the start (a change sent before it has the avatar would be lost on it).</summary>
        internal static void OnPlayerMade(VPlayer p)
        {
            var room = p?.VRoom;
            if (room == null) return;
            if (IsOurs(room))
            {
                if (_started && !_over) Arm(room as DungeonRoom, p, entering: true);
                return;
            }
            if (_lent.Count == 0) return;
            int n = TakeBackFrom(p, sync: false);
            if (n > 0) Log.Info($"{p.ActorName} came into {room.GetType().Name} holding {n} lent shotgun(s): taken back");
        }

        /// <summary>A player loading into the level: told the rules. Armed only if they take part (once per player per
        /// level; normally as they came in, D12): someone joining mid-level is about to be killed by the game to spectate.
        /// Anywhere else: what's lying around, and what they hold if D12 is off, a moment later (their game has the avatar
        /// by then, and sees the change).</summary>
        internal static void OnPlayerLoaded(IVroom room, VPlayer p)
        {
            if (p == null) return;
            if (!IsOurs(room))
            {
                if (_lent.Count == 0) return;
                room.AddEventTimer(() =>
                {
                    try { if (ReferenceEquals(room.FindPlayerByObjectID(p.ObjectID), p)) TakeBackElsewhere(room, p); }
                    catch (Exception e) { Log.ErrorOnce("takeback-late", "Taking a lent shotgun back failed: " + e); }
                }, 2000);
                return;
            }
            if (!_started || _over) return;
            Arm(room as DungeonRoom, p);
            TramMode.SendSoon(room, p, "dm", () => _over || !IsOurs(room) ? null : new[] { ("on", "1"), ("gold", _reward.ToString()) });
        }

        // ============================================================================================ deaths

        /// <summary>D8: false = the game clears what they carried instead of dropping it (our level's players).</summary>
        internal static bool KeepsInventoryOnDeath(VCreature c) => !(c is VPlayer p) || !IsOurs(c.VRoom) || !TakesPart(c.VRoom, p);

        /// <summary>A death pays the team, once per player per level, for a player who takes part (not the game's kill of
        /// someone joining mid-level; not a fake player unless testing). Once per player: a player back alive in the same
        /// level (the game restores one who reconnects within seconds, or a revive) can't be farmed for gold.</summary>
        internal static void OnPlayerDied(DungeonRoom room, VPlayer victim, int attackerId)
        {
            if (!_started || _over || !IsOurs(room) || !TakesPart(room, victim)) return;
            _deaths++;
            bool first = _paidFor.Add(victim.SteamID != 0 ? victim.SteamID : (ulong)victim.ObjectID);
            bool paid = first && TramMode.AddTeamMoney(_reward);
            if (paid) _paid += _reward;
            var attacker = room.FindActorByObjectID(attackerId);
            string by = attacker is VPlayer k && !ReferenceEquals(k, victim) ? "by " + k.ActorName
                : attacker is VMonster ? "by a mimic" : "";
            string gold = paid ? "+" + _reward + " gold for the team" : !first ? "no more gold for them in this level" : "no gold (no station)";
            TramMode.Banner(room, null, $"{victim.ActorName.ToUpperInvariant()} IS DOWN",
                $"Killed{(by.Length > 0 ? " " + by : "")}: {gold}. Everything they carried is gone.", TramMode.ColorHex, 6f);
            Log.Info($"{victim.ActorName} died {by}: {(paid ? "+" + _reward + " gold" : first ? "no maintenance room" : "already paid for this level")}, inventory lost");
        }

        /// <summary>The level ends (the tram left, or time ran out).</summary>
        internal static void OnLevelEnded(DungeonRoom room)
        {
            if (!_started || _over || !IsOurs(room)) return;
            _over = true;
            AppDomain.CurrentDomain.SetData(FairFightRoomKey, null);
            // Before the game packs what goes back with the tram (inventories, the tram's floor, its stash hangers):
            // that's later, once the result screen has shown (DungeonRoom.SetDungeonState's timer).
            int back = TakeBack(room);
            TramMode.SendRoom(room, "dm", ("on", "0"));
            TramMode.Banner(room, null, "DEATHMATCH OVER",
                (_deaths == 0 ? "Nobody died." : $"{_deaths} death(s): +{_paid} gold for the team.") + " The shotguns went back.", TramMode.ColorHex, 7f);
            Log.Info($"Deathmatch over: {_deaths} death(s), {_paid} gold, {back} lent shotgun(s) taken back");
        }

        // ============================================================================================ the lent shotguns

        static bool IsLent(ItemElement e) => e != null && e.ItemMasterID == ShotgunItem && _lent.Contains(e.ItemID);

        /// <summary>Every lent shotgun in this room: in anyone's hands, on the floor (the tram's too), on a stash hanger.</summary>
        static int TakeBack(IVroom room)
        {
            int n = 0;
            room.IterateAllPlayer(p => n += TakeBackFrom(p, sync: true));
            return n + TakeBackFromRoom(room);
        }

        static int TakeBackFrom(VPlayer p, bool sync)
        {
            var inv = p?.InventoryControlUnit;
            if (inv == null) return 0;
            var slots = new List<int>();
            foreach (var kv in inv.GetAllItemElements())
                if (IsLent(kv.Value)) slots.Add(kv.Key);
            foreach (int slot in slots)
            {
                try { inv.RemoveInvenItem(slot, needToNotiDestroy: false, sync: sync); }
                catch (Exception e) { Log.ErrorOnce("takeback-inv", $"Taking a shotgun back from {p.ActorName} failed: " + e); }
            }
            return slots.Count;
        }

        static int TakeBackFromRoom(IVroom room)
        {
            int n = 0;
            foreach (long id in _lent)
            {
                var lo = room.FindLootingObjectByItemID(id);
                if (lo == null || !IsLent(lo.GetItemElement())) continue;
                room.PendRemoveActor(lo.ObjectID);
                n++;
            }
            foreach (var kv in new List<KeyValuePair<int, ItemElement>>(room.Stashes))
                if (IsLent(kv.Value) && room.UnhangItem(kv.Key, out _)) n++;
            return n;
        }

        /// <summary>A player loaded into another room: their lent shotgun goes (if D12 missed it), and the room is checked once.</summary>
        static void TakeBackElsewhere(IVroom room, VPlayer p)
        {
            int carried = TakeBackFrom(p, sync: true), lying = 0;
            if (!ReferenceEquals(room, _sweptRoom))
            {
                _sweptRoom = room;
                lying = TakeBackFromRoom(room);
            }
            if (carried + lying > 0)
                Log.Info($"Lent shotguns taken back in {room.GetType().Name}: {carried} from {p.ActorName}, {lying} lying around");
        }
    }
}
