// MIMESIS BetterMimics - the host's mimics: where they look, the scrap they pick up, the dances they join
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Runs on the host's server (main thread, VWorld's update), once per mimic tick (AIController.Update, Patches B1), five
// times a second. It only adds to the game's own behavior tree (BT) and never replaces it:
//   - gaze: a standing mimic turns to whoever talks, looks back at whoever stares at it, looks at the nearest player,
//     glances away now and then, or looks around. Sent as the game's own view-point message (ChangeViewPointSig, what a
//     player's game sends when they move the mouse), so every game shows it, modded or not. Not while the game's
//     trained movement model drives it (it turns the mimic itself), and its head goes back to a walking pose when it
//     walks.
//   - activities while the BT wanders (passive): go for scrap it spots, also on its way (a wandering mimic is nearly
//     always walking), stop to look around after a walk. A player's dance or emote it can join, and do one of its own,
//     also near players (search, checkfriendly...). While one runs, the BT waits (AIController.PauseTime, the game's
//     own pause); it never runs longer than ActivityMaxMs and stops as soon as a player comes close or the mimic gets
//     hurt, so the BT notices players as usual. Nothing runs while it's stunned or knocked down.
// The scrap a mimic picks up is real: it drops where the mimic dies (the game drops a monster's inventory), where the
// mimic puts it down a minute or two later, or where it takes up a weapon to fight (Patches B5). Scrap in the tram is
// never taken.

using System;
using System.Collections.Generic;
using Bifrost.ConstEnum;
using ReluProtocol.Enum;
using UnityEngine;

namespace BetterMimics
{
    static class Brain
    {
        const long ThinkMs = 200;                  // the BT's own update period
        const long ActivityMaxMs = 14000;
        const float PlayerTooCloseForActivity = 5f;
        const float LootSearchRange = 9f;
        const float LootReach = 1.3f;
        const float LootKeepAwayFromPlayers = 2.5f;
        const float GazeRange = 14f;
        const float EyeHeight = 1.5f, HeadHeight = 1.55f;
        const float MaxYawStep = 40f, MaxPitchStep = 14f;   // per think: ~200 deg/s, ~70 deg/s

        enum Act { None, GoLoot, Pick, Pause, Emote }

        sealed class Mind
        {
            public long NextThinkAt;
            public Act Act;
            public long ActUntil, ActStep;
            public int LootId;
            public Vector3 LootPos;
            public long NextLootAt, NextEmoteAt, NextPauseAt, NextSoloEmoteAt;
            public long CarryUntil;              // puts its real scrap down after this (0 = carries none)
            public int JoinedPlayerId;           // the player whose emote it joined
            public long JoinedStoppedAt;
            public bool WasMoving;
            public long LastHp, CalmAt;          // hurt: no activity before CalmAt (its BT reacts)
            public HashSet<int> Unreachable;     // scrap it found no way to (not tried again)
            // gaze
            public float Yaw, Pitch;             // where it looks now (pitch: + = down)
            public bool GazeInit;
            public bool PitchDirty;              // its head was turned by us: a walking pose again when it walks
            public int LookAtId;
            public long NextGlanceAt, GlanceUntil, NextIdleLookAt;
            public float GlanceYaw, GlancePitch;
        }

        static readonly Dictionary<VCreature, Mind> _minds = new Dictionary<VCreature, Mind>();
        static readonly List<VCreature> _scratch = new List<VCreature>();
        static readonly Dictionary<int, bool> _isMimicMaster = new Dictionary<int, bool>();
        static readonly System.Random _rng = new System.Random();
        static long _nextPruneAt;

        internal static long NowMs => Hub.s != null && Hub.s.timeutil != null ? Hub.s.timeutil.GetCurrentTickMilliSec() : 0L;

        /// <summary>A new level: the last one's mimics are gone.</summary>
        internal static void Reset()
        {
            _minds.Clear();
        }

        internal static bool IsMimic(VCreature c)
        {
            if (!(c is VMonster)) return false;
            int id = c.MasterID;
            if (_isMimicMaster.TryGetValue(id, out bool yes)) return yes;
            try { yes = Hub.s.dataman.ExcelDataManager.GetMonsterInfo(id)?.IsMimic() ?? false; }
            catch (Exception) { yes = false; }
            _isMimicMaster[id] = yes;
            return yes;
        }

        /// <summary>The mimic carries a real (not copied) item: the scrap it picked up.</summary>
        internal static bool CarriesRealItem(VCreature c)
        {
            var items = c?.InventoryControlUnit?.GetAllItemElements();
            if (items == null) return false;
            foreach (var kv in items)
                if (kv.Value != null && !kv.Value.IsFake && kv.Value.ItemMasterID != 0) return true;
            return false;
        }

        /// <summary>B1: every AI tick of every creature (after the game's own update).</summary>
        internal static void Tick(AIController ai)
        {
            var self = ai._self;
            if (self == null || !IsMimic(self)) return;
            long now = NowMs;
            if (now >= _nextPruneAt) Prune(now);
            if (!self.IsAliveStatus() || !(self.VRoom is DungeonRoom room))
            {
                _minds.Remove(self);
                return;
            }
            var mind = MindOf(self);
            if (now < mind.NextThinkAt)
            {
                if (mind.Act != Act.None) ai.PauseTime = Math.Max(ai.PauseTime, 400L);
                return;
            }
            mind.NextThinkAt = now + ThinkMs;
            if (ai.Terminated || ai.IsPaused)
            {
                EndActivity(ai, mind, "the level paused its AI");
                return;
            }
            // Stunned, knocked down, thrown...: its AI waits (the game pauses it), and so does all of this.
            if (self.AbnormalControlUnit != null && self.AbnormalControlUnit.IsAIPaused())
            {
                EndActivity(ai, mind, "it can't act");
                return;
            }
            // Hurt: whatever it was doing stops at once and its BT reacts (the BT doesn't run during an activity).
            long hp = self.StatControlUnit?.GetCurrentHP() ?? 0;
            if (mind.LastHp > 0 && hp < mind.LastHp)
            {
                mind.CalmAt = now + 8000;
                EndActivity(ai, mind, "it got hurt");
            }
            mind.LastHp = hp;

            string bt = ai.CurrentBTTemplateName ?? "";
            bool wandering = bt == "passive";
            bool social = wandering || bt == "search" || bt == "checkfriendly" || bt == "bewarefriendly";
            var move = self.MovementControlUnit;
            bool moving = move != null && move.IsMoving;
            bool calm = now >= mind.CalmAt;

            if (mind.Act != Act.None)
            {
                // An emote may go on while it deals with players; everything else only while it wanders. (The BT can't
                // change while an activity holds it: what ends one is in ContinueActivity.)
                if (mind.Act == Act.Emote ? !social : !wandering) EndActivity(ai, mind, "its behavior changed to " + bt);
                else ContinueActivity(ai, self, room, mind, now);
            }
            else if (!calm)
            {
                // Just hurt: its BT deals with it.
            }
            else if (wandering)
            {
                StartActivity(ai, self, room, mind, now, moving);
            }
            else if (social && Settings.Emotes)
            {
                // Near players a mimic is out of "passive" within seconds (the BT goes to search/checkfriendly at 15 m):
                // joining a dance and the odd emote of its own must work there, or they'd never happen.
                if (now >= mind.NextEmoteAt && TryJoinEmote(ai, self, room, mind, now)) { }
                else if (!moving && now >= mind.NextSoloEmoteAt && NearestPlayer(room, self.PositionVector, out _) < 10f)
                {
                    mind.NextSoloEmoteAt = now + Rand(40000, 90000);
                    if (_rng.NextDouble() < 0.25 && TryStartEmote(self, mind, now, PickRandomEmote(), 2000, 5000, 0))
                        Trace(self, "emotes on its own");
                }
            }
            if (mind.Act != Act.None) ai.PauseTime = Math.Max(ai.PauseTime, 400L);

            // Gaze, except while the game's trained movement model drives it (checkfriendly, bewarefriendly: it turns
            // the mimic itself, learned from players) or it's picking scrap up.
            if (Settings.Gaze && social && mind.Act != Act.GoLoot && mind.Act != Act.Pick && ai._dlMovementAgent == null) Gaze(self, room, mind, now);
            else mind.GazeInit = false;
            mind.WasMoving = moving;
        }

        // ================================================================================================ activities

        static void StartActivity(AIController ai, VCreature self, DungeonRoom room, Mind mind, long now, bool moving)
        {
            float nearest = NearestPlayer(room, self.PositionVector, out _);

            // Join a player's dance or emote (a person nearby dancing is the best moment to blend in).
            if (Settings.Emotes && now >= mind.NextEmoteAt && TryJoinEmote(ai, self, room, mind, now)) return;

            if (nearest < PlayerTooCloseForActivity) return;

            // Scrap around it, also while it walks: a wandering mimic is nearly always walking (the BT walks it from
            // point to point, mostly toward players), so it spots scrap on its way and goes for it, like a player.
            if (Settings.Loot && Plugin.LootReady && now >= mind.NextLootAt)
            {
                // Walking (about 3.5 m/s) it looks about every second, or it would pass scrap between two looks.
                mind.NextLootAt = now + (moving ? Rand(800, 1500) : Rand(2000, 4000));
                if (!CarriesRealItem(self) && TryStartLoot(self, room, mind, now)) return;
            }

            if (moving) return;

            // Put down the scrap it has carried for a while (where it stands, like a player making room).
            if (mind.CarryUntil != 0 && now >= mind.CarryUntil)
            {
                if (!PutDownRealItem(self))
                {
                    mind.CarryUntil = now + 5000; // couldn't (or it has none any more): another go a little later
                    if (!CarriesRealItem(self)) mind.CarryUntil = 0;
                    return;
                }
                mind.CarryUntil = 0;
                mind.NextLootAt = now + Rand(45000, 90000); // not the same piece again at once
                StartPause(mind, now, 900, 1600);
                return;
            }

            // A stop after a walk to look around (players stop and look; the BT walks from point to point).
            if (Settings.Gaze && mind.WasMoving && now >= mind.NextPauseAt && _rng.NextDouble() < 0.4)
            {
                mind.NextPauseAt = now + Rand(9000, 20000);
                StartPause(mind, now, 1500, 4500);
                Trace(self, "stops to look around");
                return;
            }

            // Now and then an emote of its own when players can see it (they wave, dance, point...).
            if (Settings.Emotes && now >= mind.NextSoloEmoteAt && nearest < 10f)
            {
                mind.NextSoloEmoteAt = now + Rand(40000, 90000);
                if (_rng.NextDouble() < 0.35 && TryStartEmote(self, mind, now, PickRandomEmote(), 2000, 5000, 0))
                    Trace(self, "emotes on its own");
            }
        }

        static void ContinueActivity(AIController ai, VCreature self, DungeonRoom room, Mind mind, long now)
        {
            if (now >= mind.ActUntil + (mind.Act == Act.Pick ? 2000 : 0))
            {
                EndActivity(ai, mind, mind.Act + " timed out");
                return;
            }
            var move = self.MovementControlUnit;
            switch (mind.Act)
            {
                case Act.GoLoot:
                {
                    var lo = room.FindLootingObjectByObjectID(mind.LootId);
                    if (lo == null || lo.Assigned || !lo.IsAliveStatus())
                    {
                        EndActivity(ai, mind, "the scrap is gone");
                        return;
                    }
                    if (NearestPlayer(room, self.PositionVector, out _) < 3f)
                    {
                        EndActivity(ai, mind, "a player came close");
                        return;
                    }
                    if (Flat(lo.PositionVector - self.PositionVector).magnitude <= LootReach)
                    {
                        move?.StopMove();
                        mind.Act = Act.Pick;
                        mind.ActStep = now + 450;
                        if (Settings.Gaze) Look(self, mind, YawTo(self.PositionVector, lo.PositionVector), 42f, force: true); // looks down at it
                        return;
                    }
                    if (move != null && !move.IsMoving)
                    {
                        // Stopped short (the path ended near it): one more go, else give up.
                        Vector3 nav = Hub.s.vworld.FindNearestPoly(lo.PositionVector, 1.2f);
                        if (nav == NavMeshConstants.INVALID_POSITION || move.PathMoveStart(nav, ActorMoveType.Walk) == PathMoveResult.Fail)
                            EndActivity(ai, mind, "no way to the scrap");
                    }
                    return;
                }
                case Act.Pick:
                {
                    if (now < mind.ActStep) return;
                    var lo = room.FindLootingObjectByObjectID(mind.LootId);
                    if (lo != null && Flat(lo.PositionVector - self.PositionVector).magnitude > LootReach + 0.5f)
                    {
                        EndActivity(ai, mind, "the scrap is out of reach"); // within arm's reach, or not at all
                        return;
                    }
                    if (lo != null && TryPickUp(self, room, lo))
                    {
                        mind.CarryUntil = now + Rand(45000, 120000);
                        Trace(self, $"picked up {ItemName(lo)}");
                        StartPause(mind, now, 700, 1300);
                        if (Settings.Gaze) mind.Pitch = 42f; // looks back up from the floor (gaze), from where it looked
                    }
                    else EndActivity(ai, mind, "couldn't pick it up");
                    return;
                }
                case Act.Pause:
                    if (now >= mind.ActUntil || NearestPlayer(room, self.PositionVector, out _) < PlayerTooCloseForActivity - 1f)
                        EndActivity(ai, mind, null);
                    return;
                case Act.Emote:
                {
                    var emo = self.EmotionControlUnit;
                    if (emo == null || emo.PlayedEmotionMasterID == 0) { EndActivity(ai, mind, null); return; }
                    // Someone else walks right up to it: it stops, and its BT deals with them.
                    if (PlayerWithin(room, self.PositionVector, 2.5f, mind.JoinedPlayerId)) { EndActivity(ai, mind, "a player came up to it"); return; }
                    if (mind.JoinedPlayerId != 0)
                    {
                        // Stops a moment after the player it joined stops.
                        var p = room.FindPlayerByObjectID(mind.JoinedPlayerId);
                        bool theyStopped = p == null || !p.IsAliveStatus() || p.EmotionControlUnit == null || p.EmotionControlUnit.PlayedEmotionMasterID == 0;
                        if (theyStopped && mind.JoinedStoppedAt == 0) mind.JoinedStoppedAt = now + Rand(400, 1200);
                        if (mind.JoinedStoppedAt != 0 && now >= mind.JoinedStoppedAt) { EndActivity(ai, mind, null); return; }
                    }
                    if (now >= mind.ActUntil) EndActivity(ai, mind, null);
                    return;
                }
            }
        }

        static void EndActivity(AIController ai, Mind mind, string why)
        {
            if (mind.Act == Act.None) return;
            var self = ai._self;
            if (mind.Act == Act.Emote)
            {
                try { self?.EmotionControlUnit?.OnCancelEmotion(); }
                catch (Exception) { /* it may be gone */ }
            }
            if (mind.Act == Act.GoLoot)
            {
                try { self?.MovementControlUnit?.StopMove(); }
                catch (Exception) { /* it may be gone */ }
            }
            if (why != null && self != null) Trace(self, $"stops {mind.Act}: {why}");
            mind.Act = Act.None;
            mind.JoinedPlayerId = 0;
            mind.JoinedStoppedAt = 0;
            ai.PauseTime = 0;
        }

        static void StartPause(Mind mind, long now, int minMs, int maxMs)
        {
            mind.Act = Act.Pause;
            mind.ActUntil = now + Rand(minMs, maxMs);
            mind.NextIdleLookAt = now; // looks around at once
        }

        // ---------------------------------------------------------------------------------------------- scrap

        static bool TryStartLoot(VCreature self, DungeonRoom room, Mind mind, long now)
        {
            List<VLootingObject> near;
            try { near = room.GetLootingObjectsInRange(self.PositionVector, 0f, LootSearchRange); }
            catch (Exception) { return false; }
            VLootingObject best = null;
            Vector3 bestNav = default;
            float bestD = float.MaxValue;
            foreach (var lo in near)
            {
                if (lo == null || !lo.IsAliveStatus() || lo.IsFake() || lo.Assigned) continue;
                var item = lo.GetItemElement();
                if (item == null || item.IsFake || item.ItemType != ItemType.Miscellany) continue;
                if (room.CheckStartingArea(lo)) continue;                      // never from the tram
                if (Mathf.Abs(lo.PositionVector.y - self.PositionVector.y) > 1.6f) continue;
                if (NearestPlayer(room, lo.PositionVector, out _) < LootKeepAwayFromPlayers) continue;
                if (mind.Unreachable != null && mind.Unreachable.Contains(lo.ObjectID)) continue;
                Vector3 nav = Hub.s.vworld.FindNearestPoly(lo.PositionVector, 1.2f);
                if (nav == NavMeshConstants.INVALID_POSITION) continue;
                float d = Vector3.Distance(self.PositionVector, lo.PositionVector);
                if (d < bestD) { bestD = d; best = lo; bestNav = nav; }
            }
            if (best == null) return false;
            var move = self.MovementControlUnit;
            if (move == null) return false;
            if (bestD > LootReach)
            {
                // Only scrap it can walk to: a path that fails would stop the BT's own walk for nothing.
                if (!Hub.s.vworld.IsReachable(self.PositionVector, bestNav))
                {
                    if (mind.Unreachable == null) mind.Unreachable = new HashSet<int>();
                    mind.Unreachable.Add(best.ObjectID);
                    Trace(self, $"sees {ItemName(best)} {bestD:0.0} m away, no way there");
                    return false;
                }
                if (move.PathMoveStart(bestNav, ActorMoveType.Walk) == PathMoveResult.Fail) return false;
                mind.Act = Act.GoLoot;
            }
            else
            {
                // Within arm's reach: it stops, looks down at it and picks it up.
                move.StopMove();
                mind.Act = Act.Pick;
                if (Settings.Gaze) Look(self, mind, YawTo(self.PositionVector, best.PositionVector), 42f, force: true);
            }
            mind.ActUntil = now + ActivityMaxMs;
            mind.ActStep = now + 450;
            mind.LootId = best.ObjectID;
            mind.LootPos = best.PositionVector;
            Trace(self, $"goes for {ItemName(best)} {bestD:0.0} m away");
            return true;
        }

        /// <summary>What a player's pickup does (VPlayer.HandleGrapLootingObject): the item goes to its inventory, in its
        /// hand, and the looting object leaves the world.</summary>
        static bool TryPickUp(VCreature self, DungeonRoom room, VLootingObject lo)
        {
            var inv = self.InventoryControlUnit;
            if (inv == null || lo.Assigned || lo.IsFake()) return false;
            var item = lo.GetItemElement();
            if (item == null) return false;
            if (inv.CheckCanAddItem(item) != MsgErrorCode.Success)
            {
                inv.SetHandEmptyByAI(); // drops a copied (fake) item from its hand to make room
                if (inv.CheckCanAddItem(item) != MsgErrorCode.Success) return false;
            }
            lo.SetAssigned();
            if (inv.HandleAddItem(item, out int slot, sync: true, byLooting: true) != MsgErrorCode.Success)
            {
                lo.ResetAssigned();
                return false;
            }
            room.PendRemoveActor(lo.ObjectID);
            if (slot != inv.CurrentInventorySlot) inv.HandleChangeActiveInvenSlot(slot);
            self.EmotionControlUnit?.OnLooting();
            return true;
        }

        /// <summary>Drops the real item it carries, from its own slot (the game's own release: it lands at its feet).
        /// True when it did.</summary>
        static bool PutDownRealItem(VCreature self)
        {
            var inv = self.InventoryControlUnit;
            var items = inv?.GetAllItemElements();
            if (items == null) return false;
            foreach (var kv in items)
            {
                if (kv.Value == null || kv.Value.IsFake || kv.Value.ItemMasterID == 0) continue;
                var res = self.HandleReleaseItem(0, kv.Key);
                Trace(self, $"puts its scrap down ({res})");
                return res == MsgErrorCode.Success;
            }
            return false;
        }

        /// <summary>B5: the BT is about to put a fake item (a weapon, as it starts a fight) in a slot holding the real
        /// scrap it picked up; the game would delete what's there. It drops the scrap at its feet instead.</summary>
        internal static void DropRealItemBeforeFake(InventoryController inv, int slot)
        {
            var self = inv?._self;
            if (self == null || !IsMimic(self)) return;
            if (!inv._inventorySlots.TryGetValue(slot, out var item) || item == null || item.IsFake || item.ItemMasterID == 0) return;
            var res = self.HandleReleaseItem(0, slot, releaseByCC: true);
            if (_minds.TryGetValue(self, out var mind))
            {
                mind.CarryUntil = 0;
                mind.NextLootAt = NowMs + Rand(45000, 90000); // not the same piece again at once
            }
            Trace(self, $"drops its scrap to take a weapon ({res})");
        }

        static string ItemName(VLootingObject lo)
        {
            try { return "item " + lo.GetItemElement().ItemMasterID; }
            catch (Exception) { return "scrap"; }
        }

        // ---------------------------------------------------------------------------------------------- emotes

        static bool TryJoinEmote(AIController ai, VCreature self, DungeonRoom room, Mind mind, long now)
        {
            VPlayer dancer = null;
            float best = 10f;
            Vector3 eye = self.PositionVector + Vector3.up * EyeHeight;
            room.IterateAllPlayer(p =>
            {
                if (p == null || !p.IsAliveStatus() || p.EmotionControlUnit == null) return;
                int id = p.EmotionControlUnit.PlayedEmotionMasterID;
                if (id == 0 || now - p.EmotionControlUnit.EmotionStartTime < 600) return;
                float d = Vector3.Distance(self.PositionVector, p.PositionVector);
                if (d >= best) return;
                if (!CanSee(eye, p.PositionVector + Vector3.up * HeadHeight)) return;
                best = d;
                dancer = p;
            });
            if (dancer == null) return false;
            mind.NextEmoteAt = now + Rand(12000, 25000);
            if (_rng.NextDouble() > 0.7) // not every time
            {
                Trace(self, $"sees {dancer.ActorName} dancing, doesn't join this time");
                return false;
            }
            int theirs = dancer.EmotionControlUnit.PlayedEmotionMasterID;
            int mine = theirs;
            try
            {
                if (Hub.s.tableman.emote.TryGetRandomEmoteInSameGroup(theirs, out var e) && e != null && e.emoteMasterID != 0)
                    mine = e.emoteMasterID;
            }
            catch (Exception) { /* same emote */ }
            Look(self, mind, YawTo(self.PositionVector, dancer.PositionVector), 0f, force: true);
            if (!TryStartEmote(self, mind, now, mine, 3000, 6000, dancer.ObjectID)) return false;
            Trace(self, $"joins {dancer.ActorName}'s emote ({theirs} -> {mine})");
            return true;
        }

        static int PickRandomEmote()
        {
            try { return Hub.s.tableman.emote.GetRandomEmoteMasterID(); }
            catch (Exception) { return 0; }
        }

        static bool TryStartEmote(VCreature self, Mind mind, long now, int emoteId, int minMs, int maxMs, int joinedPlayerId)
        {
            if (emoteId == 0 || self.EmotionControlUnit == null) return false;
            if (self.EmotionControlUnit.PlayedEmotionMasterID != 0) return false; // already at one (its BT answers emotes too)
            // Not with someone else right next to it (the emote would stop at once: ContinueActivity's rule).
            if (self.VRoom != null && PlayerWithin(self.VRoom, self.PositionVector, 2.5f, joinedPlayerId)) return false;
            self.MovementControlUnit?.StopMove();
            var res = self.EmotionControlUnit.HandleEmotion(emoteId, self.Position, 0);
            if (res != MsgErrorCode.Success)
            {
                Trace(self, $"can't emote ({res})");
                return false;
            }
            mind.Act = Act.Emote;
            mind.ActUntil = now + Rand(minMs, maxMs);
            mind.JoinedPlayerId = joinedPlayerId;
            mind.JoinedStoppedAt = 0;
            return true;
        }

        // ================================================================================================ gaze

        static void Gaze(VCreature self, DungeonRoom room, Mind mind, long now)
        {
            var move = self.MovementControlUnit;
            if (move == null) return;
            if (move.IsMoving)
            {
                // Walking: the path owns the facing. Look ahead, a little down (people watch their step; the game's own
                // walking pose is 15-20 degrees), once, if its head was turned by us.
                mind.GazeInit = false;
                if (mind.PitchDirty)
                {
                    mind.PitchDirty = false;
                    move.TurnAngle(self.Position.yaw, Mathf.Repeat(RandF(14f, 20f), 360f));
                }
                return;
            }
            var emo = self.EmotionControlUnit;
            if (emo != null && emo.PlayedEmotionMasterID != 0 && mind.Act != Act.Emote) return;
            if (!mind.GazeInit)
            {
                mind.GazeInit = true;
                mind.Yaw = self.Position.yaw;
                mind.Pitch = SignedPitch(self.Position.pitch);
            }

            Vector3 eye = self.PositionVector + Vector3.up * EyeHeight;
            VPlayer best = null;
            float bestScore = 0f;
            room.IterateAllPlayer(p =>
            {
                if (p == null || !p.IsAliveStatus() || p.IsIndoor != self.IsIndoor) return;
                Vector3 head = p.PositionVector + Vector3.up * HeadHeight;
                float d = Vector3.Distance(eye, head);
                if (d > GazeRange) return;
                if (!CanSee(eye, head)) return;
                float score = GazeRange + 1f - d;                // nearer is more interesting
                if (IsSpeaking(p)) score += 20f;                 // people turn to whoever talks
                if (IsLookingAt(p, eye, 12f)) score += 10f;      // ...and look back at whoever stares at them
                if (p.ObjectID == mind.LookAtId) score += 3f;    // and don't flick between two people
                if (score > bestScore) { bestScore = score; best = p; }
            });

            float wantYaw, wantPitch;
            if (best != null)
            {
                if (best.ObjectID != mind.LookAtId)
                {
                    mind.LookAtId = best.ObjectID;
                    mind.GlanceUntil = 0;
                    mind.NextGlanceAt = now + Rand(3000, 7000);
                }
                bool engaged = IsSpeaking(best) || IsLookingAt(best, eye, 12f);
                if (!engaged && now >= mind.NextGlanceAt && mind.GlanceUntil == 0 && _rng.NextDouble() < 0.35)
                {
                    mind.GlanceUntil = now + Rand(800, 2000);
                    mind.GlanceYaw = mind.Yaw + RandF(-55f, 55f);
                    mind.GlancePitch = RandF(-8f, 14f);
                }
                if (mind.GlanceUntil != 0 && now < mind.GlanceUntil && !engaged)
                {
                    wantYaw = mind.GlanceYaw;
                    wantPitch = mind.GlancePitch;
                }
                else
                {
                    if (mind.GlanceUntil != 0) { mind.GlanceUntil = 0; mind.NextGlanceAt = now + Rand(3000, 7000); }
                    Vector3 head = best.PositionVector + Vector3.up * HeadHeight;
                    wantYaw = YawTo(eye, head);
                    wantPitch = PitchTo(eye, head);
                }
            }
            else
            {
                mind.LookAtId = 0;
                if (now >= mind.NextIdleLookAt)
                {
                    mind.NextIdleLookAt = now + Rand(1800, 5000);
                    mind.GlanceYaw = self.Position.yaw + RandF(-70f, 70f);
                    mind.GlancePitch = RandF(-10f, 12f);
                }
                wantYaw = mind.GlanceYaw;
                wantPitch = mind.GlancePitch;
            }
            Look(self, mind, wantYaw, wantPitch, force: false);
        }

        /// <summary>Turns toward (yaw, pitch) at a human speed and sends it like a player's view change.</summary>
        static void Look(VCreature self, Mind mind, float yaw, float pitch, bool force)
        {
            var move = self.MovementControlUnit;
            if (move == null) return;
            if (!mind.GazeInit || force)
            {
                mind.GazeInit = true;
                if (force) { mind.Yaw = yaw; mind.Pitch = pitch; }
                else { mind.Yaw = self.Position.yaw; mind.Pitch = SignedPitch(self.Position.pitch); }
            }
            float dy = Mathf.Clamp(Mathf.DeltaAngle(mind.Yaw, yaw), -MaxYawStep, MaxYawStep);
            float dp = Mathf.Clamp(pitch - mind.Pitch, -MaxPitchStep, MaxPitchStep);
            if (!force)
            {
                mind.Yaw = Mathf.Repeat(mind.Yaw + dy, 360f);
                mind.Pitch = Mathf.Clamp(mind.Pitch + dp, -40f, 60f);
            }
            float sentYaw = self.Position.yaw, sentPitch = SignedPitch(self.Position.pitch);
            if (!force && Mathf.Abs(Mathf.DeltaAngle(sentYaw, mind.Yaw)) < 1.5f && Mathf.Abs(sentPitch - mind.Pitch) < 1.5f) return;
            move.TurnAngle(mind.Yaw, Mathf.Repeat(mind.Pitch, 360f));
            mind.PitchDirty = true;
        }

        // ================================================================================================ helpers

        static bool IsSpeaking(VPlayer p)
        {
            try
            {
                return !string.IsNullOrEmpty(p._voiceUID) && Hub.s.voiceman != null &&
                       Hub.s.voiceman.TryGetVoiceAmplitude(p._voiceUID, out _, out bool speaking) && speaking;
            }
            catch (Exception) { return false; }
        }

        /// <summary>p's view (yaw, pitch) points at the point within maxAngle degrees.</summary>
        static bool IsLookingAt(VPlayer p, Vector3 point, float maxAngle)
        {
            Vector3 head = p.PositionVector + Vector3.up * HeadHeight;
            Vector3 to = point - head;
            if (to.sqrMagnitude < 0.01f) return false;
            Vector3 fwd = Quaternion.Euler(SignedPitch(p.Position.pitch), p.Position.yaw, 0f) * Vector3.forward;
            return Vector3.Angle(fwd, to) <= maxAngle;
        }

        /// <summary>A living player other than exceptId within range.</summary>
        static bool PlayerWithin(IVroom room, Vector3 pos, float range, int exceptId)
        {
            bool found = false;
            room.IterateAllPlayer(p =>
            {
                if (!found && p != null && p.ObjectID != exceptId && p.IsAliveStatus() && Vector3.Distance(pos, p.PositionVector) < range) found = true;
            });
            return found;
        }

        static float NearestPlayer(IVroom room, Vector3 pos, out VPlayer nearest)
        {
            float best = float.MaxValue;
            VPlayer found = null;
            room.IterateAllPlayer(p =>
            {
                if (p == null || !p.IsAliveStatus()) return;
                float d = Vector3.Distance(pos, p.PositionVector);
                if (d < best) { best = d; found = p; }
            });
            nearest = found;
            return best;
        }

        static Mind MindOf(VCreature c)
        {
            if (!_minds.TryGetValue(c, out var m))
            {
                long now = NowMs;
                m = new Mind
                {
                    NextLootAt = now + Rand(500, 1500),   // a first look soon: a new mimic often walks off at once
                    NextEmoteAt = now + Rand(2000, 6000),
                    NextSoloEmoteAt = now + Rand(30000, 70000),
                    NextPauseAt = now + Rand(3000, 9000),
                };
                _minds[c] = m;
            }
            return m;
        }

        static void Prune(long now)
        {
            _nextPruneAt = now + 10000;
            _scratch.Clear();
            foreach (var kv in _minds)
                if (kv.Key == null || !kv.Key.IsAliveStatus() || kv.Key.VRoom == null) _scratch.Add(kv.Key);
            foreach (var c in _scratch) _minds.Remove(c);
            _scratch.Clear();
        }

        /// <summary>No wall between (PhysicsUtility.CheckBlockByWall is true when the line is clear). The line stops
        /// short of the target, so their own body can't count as a wall.</summary>
        static bool CanSee(Vector3 eye, Vector3 target)
        {
            Vector3 d = target - eye;
            float len = d.magnitude;
            return len < 0.8f || PhysicsUtility.CheckBlockByWall(eye, eye + d * ((len - 0.5f) / len));
        }

        static float SignedPitch(float wire) => Mathf.DeltaAngle(0f, wire);
        static float YawTo(Vector3 from, Vector3 to)
        {
            Vector3 d = Flat(to - from);
            return d.sqrMagnitude < 0.0001f ? 0f : Quaternion.LookRotation(d).eulerAngles.y;
        }
        static float PitchTo(Vector3 eye, Vector3 target)
        {
            Vector3 d = target - eye;
            float flat = Flat(d).magnitude;
            return Mathf.Clamp(-Mathf.Atan2(d.y, Mathf.Max(0.1f, flat)) * Mathf.Rad2Deg, -40f, 60f);
        }
        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
        static long Rand(int min, int max) => _rng.Next(min, max);
        static float RandF(float min, float max) => (float)(min + _rng.NextDouble() * (max - min));

        static void Trace(VCreature self, string what)
        {
            if (Settings.VerboseLog) Log.Info($"Mimic #{self.ObjectID}: {what}");
        }
    }
}
