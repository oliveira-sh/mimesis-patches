// MIMESIS MimicControl - host side: the control state and everything the host does with it
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// HostPatches.cs only forwards the game's events here. Everything runs on the Unity main thread (room updates and
// player packets both run from Hub.FixedUpdate), so there is no locking. Clock: Control.NowMs, the server tick clock
// the game's own possession timers use.
//
// Rules of this file:
//   - Only players who said hello this death get anything non-vanilla; everyone else plays vanilla.
//   - A control always ends through the game's own possession end (OnCancelPossessing / OnCancelPossessed), so the
//     game's state, ours and every client stay in step; EndControl alone is only the last-resort cleanup.
//   - Never AIController.Reset() (it kills the AI for good) and never AIController.HandleChangeBT.
//   - Every reference is checked on use: object ids are per room, and objects die and leave between ticks.

using System;
using System.Collections.Generic;
using ReluProtocol;
using ReluProtocol.Enum;
using UnityEngine;

namespace MimicControl
{
    enum DeathKind { Other, SelfInflicted, BargeIn }

    /// <summary>One dead player, for this death only (a revive or the next death starts a new one).</summary>
    sealed class PlayerState
    {
        public VPlayer Player;
        /// <summary>VCreature._dyingTime of the death this state belongs to: if it changed, the player died again.</summary>
        public long DeathKey;
        public long DeathMs;
        public DeathKind Kind;
        /// <summary>Said hello this death: MimicControl's rules and control apply to this player.</summary>
        public bool Hello;
        /// <summary>A controlled possession ended this death ("one mimic per death").</summary>
        public bool UsedThisDeath;
        /// <summary>EndControl ran; the possessor-side end (H5) applies the cooldown policy.</summary>
        public bool CooldownPending;
        public Ctl Active;
        /// <summary>Last successful possession start (any, vanilla too): a hello that arrives just after it still takes
        /// the mimic over (e.g. the player's game learned from the lobby data that this host supports it a moment late).</summary>
        public long StartMs;
        public int StartMimicId;
    }

    /// <summary>One controlled mimic.</summary>
    sealed class Ctl
    {
        public VPlayer Possessor;
        public VMonster Mimic;
        public PlayerState State;
        public int PossessorId, MimicId;
        public long StartMs;
        public string EndReason;

        // input (from the possessor)
        public Vector3 Dir;
        public bool Run;
        public float Yaw = float.NaN;
        public bool HasSeq;
        public int LastSeq;
        /// <summary>Any packet (input or grab): the lease.</summary>
        public long LastPacketMs;
        /// <summary>Last input packet: a moving input older than Const.InputStaleMs stops the mimic.</summary>
        public long LastInputMs;
        /// <summary>Last input that asked to move: the idle turn waits Const.IdleTurnDelayMs after it.</summary>
        public long LastMoveInputMs;

        // movement driver
        public Vector3 LastTarget;
        public ActorMoveType LastMoveType;
        public long LastDriveMs, LastTurnMs, LastFailMs;

        // attack
        public long LastBusyMs, LastAttackMs;
    }

    static class HostControl
    {
        /// <summary>The movement driver runs at most this often from the tick (an input packet drives at once).</summary>
        const long DriveIntervalMs = 100;
        /// <summary>A new path when the straight-line end point moved this far (squared).</summary>
        const float RepathDistSqr = 0.25f * 0.25f;
        /// <summary>After a failed path, wait this long before trying the same target again (no log spam).</summary>
        const long RetryFailMs = 250;
        /// <summary>A hello this soon after a vanilla start of the same possession still takes the mimic over.</summary>
        const long LateHelloMs = 3000;
        /// <summary>Input sequence numbers this far behind the last one are reordered or duplicated packets.</summary>
        const int SeqReorderWindow = 8;
        /// <summary>Grab aim assist: a player this far above or below the mimic isn't grabbable (the hitbox is 2 m high).</summary>
        const float GrabMaxHeight = 2f;

        static readonly Dictionary<int, PlayerState> Players = new Dictionary<int, PlayerState>();
        static readonly Dictionary<int, Ctl> ByMimic = new Dictionary<int, Ctl>();
        /// <summary>Mimic id -> (mimic, end of its controlled grab's window): conta it adds until then is scaled (H11).</summary>
        static readonly Dictionary<int, GrabWindow> GrabWindows = new Dictionary<int, GrabWindow>();
        /// <summary>Player id -> last conta someone else gave them (living players too), for the Conta death rule.</summary>
        static readonly Dictionary<int, ForeignHit> ForeignConta = new Dictionary<int, ForeignHit>();
        static readonly List<int> Scratch = new List<int>();

        struct GrabWindow { public VMonster Mimic; public long EndMs; }
        struct ForeignHit { public VPlayer Player; public long Ms; }

        /// <summary>H7's fast path: most ticks no mimic is controlled.</summary>
        public static bool AnyControlled => ByMimic.Count > 0;

        // =====================================================================================================
        // state lookup
        // =====================================================================================================

        /// <summary>The player's state for their current death, or null. A state from an earlier death (a revive the
        /// Revive hook missed, a death H10 missed) is dropped here, releasing any control it still had.</summary>
        static PlayerState Find(VPlayer p)
        {
            if (p == null || !Players.TryGetValue(p.ObjectID, out var ps)) return null;
            if (ReferenceEquals(ps.Player, p) && ps.DeathKey == p._dyingTime && !p.IsAliveStatus()) return ps;
            Drop(ps, "stale player state");
            return null;
        }

        /// <summary>Find, or a new state for the current death (classified from what the player object still knows,
        /// in case H10 didn't see the death). Null for living players and outside levels.</summary>
        static PlayerState GetOrCreate(VPlayer p)
        {
            var ps = Find(p);
            if (ps != null) return ps;
            if (p == null || p.IsAliveStatus() || !(p.VRoom is DungeonRoom)) return null;
            // This runs after the dying animation (hello, possession start): the foreign-conta window is measured from
            // the death itself, not from now, or a grab that killed would be out of it.
            long deathMs = p._dyingTime > 0 ? p._dyingTime : Control.NowMs;
            ps = new PlayerState
            {
                Player = p,
                DeathKey = p._dyingTime,
                DeathMs = deathMs,
                Kind = Classify(p, p.ReasonOfDeath, 0, attackerKnown: false, forced: p.ReasonOfDeath == ReasonOfDeath.AdminKill, deathMs),
            };
            Players[p.ObjectID] = ps;
            return ps;
        }

        static void Drop(PlayerState ps, string reason)
        {
            if (ps == null) return;
            if (ps.Player != null && Players.TryGetValue(ps.Player.ObjectID, out var cur) && ReferenceEquals(cur, ps))
                Players.Remove(ps.Player.ObjectID);
            // Removed first: Release goes through the game's possession end, which comes back here (H5).
            if (ps.Active != null) Release(ps.Active, reason);
        }

        /// <summary>The control this player has, from the mimic map (the source of truth: it's what H7 acts on).</summary>
        static Ctl FindCtlByPossessor(VPlayer p)
        {
            if (p == null || ByMimic.Count == 0) return null;
            foreach (var ctl in ByMimic.Values)
                if (ReferenceEquals(ctl.Possessor, p)) return ctl;
            return null;
        }

        static Ctl FindCtlByMimic(VCreature mimic)
        {
            if (mimic == null || !ByMimic.TryGetValue(mimic.ObjectID, out var ctl)) return null;
            return ReferenceEquals(ctl.Mimic, mimic) ? ctl : null;
        }

        static bool IsRegistered(Ctl ctl) => ctl != null && ByMimic.TryGetValue(ctl.MimicId, out var cur) && ReferenceEquals(cur, ctl);

        /// <summary>The possession system's own definition of a mimic (PossessionController ctor: CreatureAIInfo.IsMimic).</summary>
        static bool IsMimic(VActor a) =>
            a is VMonster m && m.PossessionControlUnit != null && m.PossessionControlUnit.RoleType == PossessionRoleType.Possessed;

        static string Name(VPlayer p) => p == null ? "?" : $"{p.ObjectID} ({p.ActorName})";

        // =====================================================================================================
        // rules: every rule is a required possession cooldown, enforced by the game's own cooldown
        // =====================================================================================================

        /// <summary>The cooldown this player must have now: Protocol.BlockedCooltime = no more mimics this death.</summary>
        static long RequiredCooldown(PlayerState ps, long now)
        {
            if (!Settings.Enabled) return 0;
            if (ps.Kind == DeathKind.BargeIn) return Protocol.BlockedCooltime;
            if (Settings.OnePerDeath && ps.UsedThisDeath) return Protocol.BlockedCooltime;
            if (Settings.BlockSelfInflicted && ps.Kind == DeathKind.SelfInflicted) return Protocol.BlockedCooltime;
            return Math.Max(0L, ps.DeathMs + Settings.DeathDelayMs - now);
        }

        static string RuleText(PlayerState ps, long need)
        {
            if (need <= 0) return "can take a mimic now";
            if (!Protocol.IsBlocked(need)) return $"can take a mimic in {need / 1000.0:0.0} s (delay after death)";
            if (ps.Kind == DeathKind.BargeIn) return "no mimic: joined mid-level";
            if (Settings.OnePerDeath && ps.UsedThisDeath) return "no more mimics this death (one per death)";
            return "no mimic: self-inflicted death";
        }

        /// <summary>Raises the game's cooldown to what the rules need, and unblocks a player the rules no longer block
        /// (the host changed a setting, or turned control off). Returns the new cooldown.</summary>
        static long ApplyRule(PlayerState ps, PossessionController pc, long now)
        {
            long need = RequiredCooldown(ps, now);
            long cur = pc._leftPossessingCooltime;
            if (need > cur) pc._leftPossessingCooltime = need;
            // 1 ms, not 0: the game sends its "cooldown over" sync only when the countdown crosses 0.
            else if (Protocol.IsBlocked(cur) && !Protocol.IsBlocked(need)) pc._leftPossessingCooltime = Math.Max(need, 1L);
            return pc._leftPossessingCooltime;
        }

        // =====================================================================================================
        // death classification (H10). Conservative: only causes we can pin on the player themselves.
        // =====================================================================================================

        /// <summary>H10a / H10b: a player died (normal death with the sig, or forced death).</summary>
        public static void OnDeath(VPlayer p, ActorDyingSig sig, bool forced)
        {
            if (!(p.VRoom is DungeonRoom)) return;
            long now = Control.NowMs;
            if (Players.TryGetValue(p.ObjectID, out var old)) Drop(old, "died again");
            var reason = forced ? ReasonOfDeath.AdminKill : sig != null ? sig.reasonOfDeath : p.ReasonOfDeath;
            int attacker = sig != null ? sig.attackerActorID : 0;
            var ps = new PlayerState
            {
                Player = p,
                DeathKey = p._dyingTime,
                DeathMs = p._dyingTime > 0 ? p._dyingTime : now,
                Kind = Classify(p, reason, attacker, attackerKnown: true, forced, now),
            };
            Players[p.ObjectID] = ps;
            Log.Info($"Player {Name(p)} died ({reason}{(attacker != 0 ? $", by actor {attacker}" : "")}): " +
                     (ps.Kind == DeathKind.BargeIn ? "joined mid-level" : ps.Kind == DeathKind.SelfInflicted ? "self-inflicted" : "not self-inflicted"));
        }

        /// <summary>attackerKnown false: the death sig wasn't seen (H10 missing), so causes that need the attacker
        /// count as not self-inflicted. deathMs: when the player died (the foreign-conta window ends there).</summary>
        static DeathKind Classify(VPlayer p, ReasonOfDeath reason, int attackerId, bool attackerKnown, bool forced, long deathMs)
        {
            switch (reason)
            {
                case ReasonOfDeath.AdminKill:
                    // Barge-in: late joiners are force-killed 3 s after loading (IVroom.ScheduleBargeInKill, same test).
                    return forced && IsBargeIn(p) ? DeathKind.BargeIn : DeathKind.Other;
                case ReasonOfDeath.Fall:
                    // Jumping or walking off a ledge. A mimic grab ends in a knockback: a fall right after conta from
                    // someone else was probably that push, not the player.
                    return HadForeignConta(p, deathMs) ? DeathKind.Other : DeathKind.SelfInflicted;
                case ReasonOfDeath.Conta:
                    // Contamination the player built up themselves (time, running, items). A grab, a monster's conta
                    // attack or a teammate's sprayer within the last seconds makes it someone else's doing.
                    return HadForeignConta(p, deathMs) ? DeathKind.Other : DeathKind.SelfInflicted;
                case ReasonOfDeath.GameSystem:
                    // Instant-kill volumes and pits set HP to 0 with no attacker; an item used on yourself names you.
                    if (!attackerKnown) return DeathKind.Other;
                    return attackerId == 0 || ReferenceEquals(ResolveAttacker(p, attackerId), p) ? DeathKind.SelfInflicted : DeathKind.Other;
                case ReasonOfDeath.FieldSkill:
                case ReasonOfDeath.Skill:
                case ReasonOfDeath.DyingSkill:
                case ReasonOfDeath.Abnormal:
                    // Own explosive or thrown item. An attacker that's already gone is never counted against the player.
                    if (!attackerKnown || attackerId == 0) return DeathKind.Other;
                    return ReferenceEquals(ResolveAttacker(p, attackerId), p) ? DeathKind.SelfInflicted : DeathKind.Other;
                default:
                    return DeathKind.Other;
            }
        }

        static VActor ResolveAttacker(VPlayer p, int attackerId)
        {
            var a = p.VRoom.FindActorByObjectID(attackerId);
            // An explosion or gas cloud is its own actor; its caster is who threw it (as VCreature.OnDying resolves it).
            if (a is FieldSkillObject f && f.CasterActor != null) a = f.CasterActor;
            return a;
        }

        static bool IsBargeIn(VPlayer p)
        {
            try { return !p.IsHost && !p.VRoom.RoomManager.GetGameSessionInfo().FixedPlayerSteamIDs.ContainsKey(p.SteamID); }
            catch (Exception e) { Log.WarnOnce("barge-in", "Couldn't tell a barge-in death: " + e.Message); return false; }
        }

        static bool HadForeignConta(VPlayer p, long deathMs) =>
            ForeignConta.TryGetValue(p.ObjectID, out var hit) && ReferenceEquals(hit.Player, p) && deathMs - hit.Ms <= Const.ForeignContaWindowMs;

        static void MarkForeignConta(VPlayer victim, long now)
        {
            ForeignConta[victim.ObjectID] = new ForeignHit { Player = victim, Ms = now };
        }

        // =====================================================================================================
        // conta (H11, and the field-skill and projectile hooks): strength scaling and the foreign-conta record
        // =====================================================================================================

        /// <summary>H11: one abnormal conta tick (already filtered: Conta, Value > 0). Victim = the StatManager's owner.</summary>
        public static void OnContaTick(VCreature victim, ref ApplyMutableStatsAbnormalArgs args)
        {
            long now = Control.NowMs;
            // Recorded before the tick lands: a tick that kills runs the death (H10) inside the original.
            // A caster we can't name (0) counts as someone else: when unsure, don't call a death self-inflicted.
            if (victim is VPlayer vp && args.CasterActorID != vp.ObjectID && vp.VRoom is DungeonRoom)
                MarkForeignConta(vp, now);

            int pct = Settings.AttackStrengthPct;
            if (pct == 100 || GrabWindows.Count == 0 || !GrabWindows.TryGetValue(args.CasterActorID, out var w)) return;
            if (now > w.EndMs || w.Mimic == null || !ReferenceEquals(w.Mimic.VRoom, victim.VRoom)) return;
            long scaled = (long)Math.Round(args.Value * (pct / 100.0));
            Log.Trace($"Grab tick from mimic {args.CasterActorID} on {victim.ObjectID}: conta {args.Value} -> {scaled} ({pct}%)");
            args = new ApplyMutableStatsAbnormalArgs(args.CasterActorID, args.SyncID, args.StatType, args.ModifyType, scaled, args.SkillMasterID);
        }

        /// <summary>A field skill hit (gas cloud) that adds conta: every player it may hit got conta from someone else.
        /// The "cleaner" monster's round attack does this (field skill 81901), with no abnormal involved.</summary>
        public static void OnFieldContaHit(FieldSkillObject field, List<TargetInfo> targets)
        {
            var room = field.VRoom;
            if (!(room is DungeonRoom) || targets == null) return;
            long now = Control.NowMs;
            var caster = field.CasterActor;
            foreach (var t in targets)
            {
                if (t == null) continue;
                var pl = room.FindPlayerByObjectID(t.targetObjectID);
                if (pl != null && !ReferenceEquals(pl, caster) && pl.IsAliveStatus()) MarkForeignConta(pl, now);
            }
        }

        /// <summary>H11c: a projectile hit that adds conta (already filtered: Conta, value > 0), e.g. a teammate's
        /// dirty-water sprayer. Every player it may hit except the shooter got conta from someone else: spraying
        /// yourself stays self-inflicted. A shooter we can't name counts as someone else, as in H11.</summary>
        public static void OnProjectileContaHit(VProjectileObject projectile, List<int> hitIds)
        {
            var room = projectile.VRoom;
            if (!(room is DungeonRoom)) return;
            long now = Control.NowMs;
            var shooter = projectile._parentActor;
            foreach (int id in hitIds)
            {
                var pl = room.FindPlayerByObjectID(id);
                if (pl != null && !ReferenceEquals(pl, shooter) && pl.IsAliveStatus()) MarkForeignConta(pl, now);
            }
        }

        static void AddGrabWindow(VMonster mimic, long endMs, long now)
        {
            if (GrabWindows.Count > 0)
            {
                Scratch.Clear();
                foreach (var kv in GrabWindows) if (kv.Value.EndMs < now) Scratch.Add(kv.Key);
                foreach (var id in Scratch) GrabWindows.Remove(id);
            }
            GrabWindows[mimic.ObjectID] = new GrabWindow { Mimic = mimic, EndMs = endMs };
        }

        // =====================================================================================================
        // hello and possession start (H1 hello, H3)
        // =====================================================================================================

        /// <summary>H1: "I run MimicControl" from a player (sent once per death and right before every possession).</summary>
        public static void OnHello(VPlayer p, int version)
        {
            if (version != Const.ProtocolVersion)
            {
                Log.WarnOnce("hello-version-" + version, $"Player {Name(p)} runs MimicControl protocol {version}, this host {Const.ProtocolVersion}: their possessions stay as in the game. Update both to the same version.");
                return;
            }
            if (!Settings.Enabled || p.IsAliveStatus() || !(p.VRoom is DungeonRoom))
            {
                Log.Trace($"Hello from {Name(p)} ignored (control {(Settings.Enabled ? "on" : "off")}, {(p.IsAliveStatus() ? "alive" : "dead")}, room {p.VRoom?.GetType().Name})");
                return;
            }
            var ps = GetOrCreate(p);
            var pc = p.PossessionControlUnit;
            if (ps == null || pc == null) return;
            long now = Control.NowMs;
            bool first = !ps.Hello;
            ps.Hello = true;

            if (pc._isPossessing)
            {
                // Hellos are reliable and the one sent before StartPossessingReq arrives first, but a hello can still come
                // just after a start: the player's game only sends one once it sees the host's lobby data (read 1/s).
                if (ps.Active == null && pc._possessingTargetActorID == ps.StartMimicId && now - ps.StartMs <= LateHelloMs)
                {
                    long need = RequiredCooldown(ps, now);
                    var target = p.VRoom.FindActorByObjectID(ps.StartMimicId);
                    if (need > 0) Log.Info($"Late hello from {Name(p)}: possession stays as in the game ({RuleText(ps, need)})");
                    else if (target == null || !NearLivingPlayer(p.VRoom, target)) Log.Info($"Late hello from {Name(p)}: possession stays as in the game (no living player near mimic {ps.StartMimicId})");
                    else TryTakeOver(ps, pc, ps.StartMimicId, "late hello");
                }
                return;
            }

            long cd = ApplyRule(ps, pc, now);
            // Tells the player's game the rule right away (its gauge and its own possession gate); the game re-sends it
            // every second while it counts down.
            p.SendToMe(new SyncPossessorInfoSig
            {
                possessorActorID = p.ObjectID,
                possessorLeftPossessingTime = pc._leftPossessingTime,
                possessorLeftPossessingCooltime = cd,
                possessedActorID = 0,
            });
            if (first) Log.Info($"Hello from {Name(p)}: MimicControl rules apply this death; {RuleText(ps, RequiredCooldown(ps, now))}");
        }

        /// <summary>H3a: refuse a possession start the host's rules don't allow (hello'd players only). Success = let
        /// the game decide.</summary>
        public static MsgErrorCode CheckStart(VPlayer p, PossessionController pc, int targetActorId)
        {
            if (!Settings.Enabled || !(p.VRoom is DungeonRoom room)) return MsgErrorCode.Success;
            var ps = Find(p);
            if (ps == null || !ps.Hello || pc._isPossessing || p.LifeCycle != VCreatureLifeCycle.Dead) return MsgErrorCode.Success;
            long now = Control.NowMs;
            long need = RequiredCooldown(ps, now);
            if (need > 0)
            {
                ApplyRule(ps, pc, now);
                Log.Info($"Possession refused for {Name(p)}: {RuleText(ps, need)}");
                return MsgErrorCode.PossessionInCooltime;
            }
            var target = room.FindActorByObjectID(targetActorId);
            if (target == null) return MsgErrorCode.Success; // the game answers ActorNotFound
            // The game checks the distance only on the possessor's side: mimic within POSSESSION_DISTANCE of the player
            // they watch. Any living player will do here, with some slack for movement during the round trip.
            if (!NearLivingPlayer(room, target))
            {
                Log.Info($"Possession refused for {Name(p)}: mimic {targetActorId} isn't near any living player");
                return MsgErrorCode.TargetCannotBePossessed;
            }
            return MsgErrorCode.Success;
        }

        static bool NearLivingPlayer(IVroom room, VActor target)
        {
            float max = Hub.s.dataman.ExcelDataManager.Consts.C_PossessionDistance + Const.PossessDistanceTolerance;
            float maxSqr = max * max;
            var pos = target.PositionVector;
            foreach (var other in room._vPlayerDict.Values)
                if (other != null && other.IsAliveStatus() && (other.PositionVector - pos).sqrMagnitude <= maxSqr) return true;
            return false;
        }

        /// <summary>H3b: the game started a possession (its Res and Sig are sent). Hello'd player: take the mimic over.</summary>
        public static void OnStarted(VPlayer p, PossessionController pc, int targetActorId)
        {
            var ps = GetOrCreate(p);
            if (ps == null) return;
            ps.StartMs = Control.NowMs;
            ps.StartMimicId = targetActorId;
            if (!ps.Hello)
            {
                Log.Trace($"Possession of {targetActorId} by {Name(p)} stays as in the game (no hello)");
                return;
            }
            TryTakeOver(ps, pc, targetActorId, "start");
        }

        static void TryTakeOver(PlayerState ps, PossessionController pc, int mimicId, string how)
        {
            var p = ps.Player;
            string why = null;
            var room = p.VRoom;
            var target = room?.FindActorByObjectID(mimicId);
            var mimic = target as VMonster;
            if (!Settings.Enabled) why = "control is off";
            else if (!(room is DungeonRoom)) why = "not in a level";
            else if (!room.EnableAIController || room.IsSessionEnding) why = "the level is paused or ending";
            else if (!pc._isPossessing || pc._possessingTargetActorID != mimicId) why = "not possessing it";
            else if (!IsMimic(target) || mimic == null) why = "not a mimic";
            else if (!mimic.IsAliveStatus()) why = "the mimic isn't alive";
            else if (mimic.AIControlUnit == null || mimic.MovementControlUnit == null || mimic.SkillControlUnit == null) why = "the mimic has no AI or movement";
            if (why != null)
            {
                Log.Info($"Possession of {mimicId} by {Name(p)} stays as in the game: {why}");
                return;
            }

            // Leftovers that would make two controls of one mimic or one player (shouldn't happen).
            var stale = FindCtlByMimic(mimic);
            if (stale != null) EndControl(stale, "replaced");
            if (ps.Active != null) EndControl(ps.Active, "replaced");

            long now = Control.NowMs;
            var ctl = new Ctl
            {
                Possessor = p, Mimic = mimic, State = ps, PossessorId = p.ObjectID, MimicId = mimicId,
                StartMs = now, LastPacketMs = now, LastInputMs = now, LastMoveInputMs = now,
                LastDriveMs = now, LastTurnMs = now,
                // Free to grab at once: the attack cooldown counts from the end of a grab, and an AI grab still running
                // at takeover keeps LastBusyMs fresh (TickMimic), so the cooldown then starts at its end.
                LastBusyMs = long.MinValue / 2, LastAttackMs = long.MinValue / 2,
                LastTarget = mimic.PositionVector, LastMoveType = ActorMoveType.None,
            };
            ByMimic[mimicId] = ctl;
            ps.Active = ctl;
            try
            {
                // Stop the brain: its moves, its voice/comm channel and BT stacks (Flush), the deep-learning agents and
                // the horn imitation. A running grab is left alone: possessing the grabber mustn't rescue the victim.
                var ai = mimic.AIControlUnit;
                ai.Flush();
                ai.ToggleDLAgent(false);
                ai._imitateHorn.Stop();
                // No time limit while controlled (H4 keeps it there), and the value doubles as our ack: the game re-sends
                // it to the possessor every second.
                pc._leftPossessingTime = Protocol.ControlMagic;
                p.SendToMe(Protocol.Ack(p.ObjectID, mimicId));
                Log.Info($"Player {Name(p)} controls mimic {mimicId} ({how})");
            }
            catch (Exception e)
            {
                // Roll back to a vanilla possession (12 s, AI running) rather than ending it: our bug mustn't cost the
                // player their mimic ("one per death").
                Log.Error($"Takeover of mimic {mimicId} by {Name(p)} failed; the possession stays as in the game: {e}");
                if (IsRegistered(ctl)) ByMimic.Remove(mimicId);
                if (ReferenceEquals(ps.Active, ctl)) ps.Active = null;
                try
                {
                    if (pc._leftPossessingTime == Protocol.ControlMagic)
                        pc._leftPossessingTime = Hub.s.dataman.ExcelDataManager.Consts.C_PossessionDuration;
                    var ai = mimic.AIControlUnit;
                    ai?.Flush();
                    ai?.ResetAggro();
                }
                catch (Exception e2) { Log.ErrorOnce("takeover-rollback", "Rolling back a takeover failed: " + e2); }
            }
        }

        // =====================================================================================================
        // input (H1, H2)
        // =====================================================================================================

        static Ctl ActiveCtl(VPlayer p)
        {
            var ctl = FindCtlByPossessor(p);
            return ctl != null && ReferenceEquals(ctl.State, Find(p)) ? ctl : null;
        }

        /// <summary>H1: a control input packet from a dead player.</summary>
        public static void OnInput(VPlayer p, ControlInput input)
        {
            var ctl = ActiveCtl(p);
            if (ctl == null || ctl.MimicId != input.MimicId)
            {
                Log.Trace($"Input #{input.Seq} from {Name(p)} for mimic {input.MimicId} ignored: not controlling it");
                return;
            }
            if (ctl.HasSeq && input.Seq != 1)
            {
                // Input packets are unreliable: drop one that's older than the last (overtaken) or a duplicate. A sender
                // that starts counting again (1) or jumps far back restarted its counter.
                int d = unchecked(input.Seq - ctl.LastSeq);
                if (d <= 0 && d > -SeqReorderWindow) return;
            }
            ctl.HasSeq = true;
            ctl.LastSeq = input.Seq;
            long now = Control.NowMs;
            ctl.LastPacketMs = ctl.LastInputMs = now;
            ctl.Dir = input.Dir;
            ctl.Run = input.Run;
            if (!float.IsNaN(input.Yaw)) ctl.Yaw = input.Yaw;
            if (input.Moving) ctl.LastMoveInputMs = now;
            Log.Trace($"Input #{input.Seq} mimic {ctl.MimicId}: {(input.Moving ? input.Run ? "run" : "walk" : "idle")} dir ({input.Dir.x:0.00}, {input.Dir.z:0.00}) yaw {input.Yaw:0}");
            Drive(ctl, now, force: true);
        }

        /// <summary>H2: a grab request from a dead player.</summary>
        public static void OnAttack(VPlayer p, int mimicId, float yaw)
        {
            var ctl = ActiveCtl(p);
            if (ctl == null || ctl.MimicId != mimicId)
            {
                Log.Trace($"Grab from {Name(p)} for mimic {mimicId} ignored: not controlling it");
                return;
            }
            long now = Control.NowMs;
            ctl.LastPacketMs = now;
            var mimic = ctl.Mimic;
            var room = mimic.VRoom;
            var sc = mimic.SkillControlUnit;
            var mc = mimic.MovementControlUnit;
            var ac = mimic.AttachControlUnit;
            string why = null;
            if (!room.EnableAIController || room.IsSessionEnding) why = "the level is paused or ending";
            else if (!mimic.IsAliveStatus()) why = "the mimic isn't alive";
            // The game's AI skill call checks neither the skill slots nor CanAction: the host does. Always skill 300,
            // whatever the packet says (a monster may use any skill id, e.g. the alpha mimic's suicide bomb).
            else if (sc == null || mc == null || !sc.GetSkillSlots().Contains(Const.GrabSkillId)) why = "this mimic can't grab";
            else if (sc.IsUsingSkillNow()) why = "already grabbing";
            else if (ac != null && ac.IsAttaching()) why = "holding someone";
            else if (mimic.CanAction(VActorActionType.Skill) != MsgErrorCode.Success) why = "it can't act now";
            else if (now < ctl.LastBusyMs + Settings.AttackCooldownMs) why = $"cooldown, {(ctl.LastBusyMs + Settings.AttackCooldownMs - now) / 1000.0:0.0} s left";
            else if (now < ctl.LastAttackMs + Const.MinAttackIntervalMs) why = "too soon";
            if (why != null)
            {
                Log.Trace($"Grab by mimic {mimicId} refused: {why}");
                return;
            }

            if (!float.IsNaN(yaw)) ctl.Yaw = yaw;
            float aim = float.IsNaN(yaw) ? mimic.Position.yaw : yaw;
            var target = PickGrabTarget(mimic, aim);
            // No one in reach: grab where the player looks (a whiff still plays the whole grab, which limits spam).
            if (target == null && !float.IsNaN(yaw)) mc.TurnAngle(Norm180(yaw), mimic.Position.pitch);
            long syncId = 0;
            // The exact call the mimic's behaviour tree makes (MMBehaviorTree/UseSkill): vanilla clients see a normal grab.
            bool ok = sc.UseSkillReqByAI(target, null, Const.GrabSkillId, ref syncId, SkillFlags.None, null, target != null);
            if (!ok)
            {
                Log.Trace($"Grab by mimic {mimicId} refused by the game");
                return;
            }
            ctl.LastAttackMs = ctl.LastBusyMs = ctl.LastTurnMs = now;
            AddGrabWindow(mimic, now + Const.GrabWindowMs, now);
            Log.Trace($"Mimic {mimicId} grabs " + (target != null ? $"at player {Name(target)}" : $"at nothing (yaw {aim:0})"));
        }

        /// <summary>Aim assist: the living player in reach closest to where the possessor aims.</summary>
        static VPlayer PickGrabTarget(VMonster mimic, float yaw)
        {
            VPlayer best = null;
            float bestScore = float.MaxValue;
            var pos = mimic.PositionVector;
            var world = Hub.s != null ? Hub.s.vworld : null;
            foreach (var pl in mimic.VRoom._vPlayerDict.Values)
            {
                if (pl == null || !pl.IsAliveStatus()) continue;
                if ((pl.UntargetReason & (UntargetReason.Normal | UntargetReason.Invisible)) != 0) continue;
                var d = pl.PositionVector - pos;
                if (Mathf.Abs(d.y) > GrabMaxHeight) continue;
                float dist = Mathf.Sqrt(d.x * d.x + d.z * d.z);
                if (dist > Const.GrabReach) continue;
                float off = dist < 0.01f ? 0f : Mathf.Abs(Mathf.DeltaAngle(yaw, Misc.GetDirectionAngle(pos, pl.PositionVector)));
                if (off > Const.GrabAimAngle) continue;
                // The grab's own hit check skips players behind a wall (SkillContext.CanHitCandidate, same test): aiming
                // at one would turn the mimic away from a player in reach and waste the grab.
                if (world != null && world.IsFullyBlockedByWall(pos, pl.PositionVector, mimic.HitCollisionRadius, pl.HitCollisionRadius)) continue;
                float score = dist + off / 30f;
                if (score < bestScore) { best = pl; bestScore = score; }
            }
            return best;
        }

        static float Norm180(float deg) => Mathf.DeltaAngle(0f, deg);

        // =====================================================================================================
        // movement driver: the AI's own charge pattern, a path move to a straight-line end point
        // =====================================================================================================

        static void Drive(Ctl ctl, long now, bool force)
        {
            if (!force && now - ctl.LastDriveMs < DriveIntervalMs) return;
            ctl.LastDriveMs = now;
            var mimic = ctl.Mimic;
            var mc = mimic.MovementControlUnit;
            if (mc == null) return;
            // Casting (the grab), stunned, held: the game stops moves itself then, and a grabbing mimic must not turn.
            if (mimic.CanAction(VActorActionType.Move) != MsgErrorCode.Success) return;

            bool fresh = now - ctl.LastInputMs <= Const.InputStaleMs;
            if (!fresh || ctl.Dir.sqrMagnitude <= Protocol.MinDirSqr)
            {
                if (mc.IsMoving)
                {
                    mc.StopMove();
                    Log.Trace($"Mimic {ctl.MimicId} stops ({(fresh ? "idle" : "input stale")})");
                }
                IdleTurn(ctl, mc, now);
                return;
            }

            float look = ctl.Run ? Const.RunLookahead : Const.WalkLookahead;
            var pos = mimic.PositionVector;
            // Navmesh line in the input direction, 0.3 m short of walls: no clipping, no walking off ledges.
            var end = Hub.s.vworld.GetStraightLineEndPoint(pos, ctl.Dir, look);
            var type = ctl.Run ? ActorMoveType.Run : ActorMoveType.Walk;
            if (!NavMeshConstants.IsValid(end))
            {
                // A wall right ahead: stand, facing where the player pushes.
                if (mc.IsMoving) mc.StopMove();
                if (now - ctl.LastTurnMs >= Const.TurnIntervalMs)
                {
                    float want = Mathf.Atan2(ctl.Dir.x, ctl.Dir.z) * Mathf.Rad2Deg;
                    if (Mathf.Abs(Mathf.DeltaAngle(mimic.Position.yaw, want)) > Const.IdleTurnMinAngle)
                    {
                        ctl.LastTurnMs = now;
                        mc.TurnAngle(want, mimic.Position.pitch);
                    }
                }
                return;
            }
            bool repath = !mc.IsMoving || type != ctl.LastMoveType
                          || (end - ctl.LastTarget).sqrMagnitude > RepathDistSqr
                          || (pos - ctl.LastTarget).sqrMagnitude < look * look * 0.25f;
            if (!repath) return;
            if (ctl.LastFailMs != 0 && now - ctl.LastFailMs < RetryFailMs && (end - ctl.LastTarget).sqrMagnitude <= RepathDistSqr) return;
            // PathMoveStart doesn't sprint (no stamina) and its MoveStartSig carries the move type, so every client
            // animates walk/run at the right speed.
            var result = mc.PathMoveStart(end, type, calcAngle: true, fixLookAt: false);
            ctl.LastTarget = end;
            ctl.LastMoveType = type;
            ctl.LastFailMs = result == PathMoveResult.Fail ? now : 0;
            Log.Trace($"Mimic {ctl.MimicId} {(ctl.Run ? "runs" : "walks")} to ({end.x:0.0}, {end.y:0.0}, {end.z:0.0}): {result}");
        }

        /// <summary>Standing still: the mimic turns to where the possessor's camera looks (not while walking: the
        /// mimic faces where it walks, like the game's own moves).</summary>
        static void IdleTurn(Ctl ctl, MovementController mc, long now)
        {
            if (float.IsNaN(ctl.Yaw)) return;
            if (now - ctl.LastMoveInputMs < Const.IdleTurnDelayMs || now - ctl.LastTurnMs < Const.TurnIntervalMs) return;
            var mimic = ctl.Mimic;
            if (Mathf.Abs(Mathf.DeltaAngle(mimic.Position.yaw, ctl.Yaw)) <= Const.IdleTurnMinAngle) return;
            ctl.LastTurnMs = now;
            mc.TurnAngle(Norm180(ctl.Yaw), mimic.Position.pitch);
        }

        // =====================================================================================================
        // per tick (H7 for the mimic, H4 for the possessor)
        // =====================================================================================================

        /// <summary>H7: the controlled mimic's tick, instead of its AI. False if this mimic isn't controlled.</summary>
        public static bool TickMimic(VCreature self)
        {
            var ctl = FindCtlByMimic(self);
            if (ctl == null) return false;
            long now = Control.NowMs;
            var mimic = ctl.Mimic;
            var p = ctl.Possessor;
            var room = mimic.VRoom;
            var ppc = p.PossessionControlUnit;
            var mpc = mimic.PossessionControlUnit;
            // Watchdog: both sides of the possession must still say this player possesses this mimic. Catches every end
            // the hooks missed (a revive the Revive hook didn't see, a player removed some other way...).
            if (!ReferenceEquals(room.FindPlayerByObjectID(ctl.PossessorId), p) || p.LifeCycle != VCreatureLifeCycle.Dead
                || ppc == null || !ppc._isPossessing || ppc._possessingTargetActorID != ctl.MimicId
                || mpc == null || !mpc._isPossessed || mpc._possessorActorID != ctl.PossessorId)
            {
                Release(ctl, "the possession ended without notice");
                return true;
            }
            if (!Settings.Enabled) { Release(ctl, "control turned off"); return true; }
            if (!room.EnableAIController || room.IsSessionEnding) { Release(ctl, "the level is ending"); return true; }
            if (now - ctl.LastPacketMs > Const.LeaseMs) { Release(ctl, $"no packets from the player for {Const.LeaseMs / 1000} s"); return true; }

            var sc = mimic.SkillControlUnit;
            var ac = mimic.AttachControlUnit;
            // The attack cooldown counts from the end of the grab (skill 4.27 s, hold 3.8 s).
            if ((sc != null && sc.IsUsingSkillNow()) || (ac != null && ac.IsAttaching())) ctl.LastBusyMs = now;
            Drive(ctl, now, force: false);
            return true;
        }

        /// <summary>H4: the possessor's tick (runs only while they're Dead). Returns whether the game's update runs.</summary>
        public static bool TickPossessor(VPlayer p, PossessionController pc, ref long deltaTime)
        {
            if (Players.Count == 0 && ByMimic.Count == 0) return true;
            var ctl = FindCtlByPossessor(p);
            if (pc._isPossessing)
            {
                if (ctl == null) return true; // a vanilla possession: the game's 12 s
                if (ctl.MimicId != pc._possessingTargetActorID)
                {
                    // Our control is of another mimic than the one possessed (shouldn't happen): drop ours only.
                    Release(ctl, "possessing another mimic");
                    return true;
                }
                var target = p.VRoom.FindActorByObjectID(pc._possessingTargetActorID);
                if (!ReferenceEquals(target, ctl.Mimic) || !ctl.Mimic.IsAliveStatus())
                {
                    // The mimic was removed without dying (the game's only recovery was the 12 s timer, gone here).
                    ctl.EndReason = ctl.EndReason ?? "the mimic is gone";
                    pc.OnCancelPossessing();
                    if (IsRegistered(ctl)) Release(ctl, "the mimic is gone");
                    return false;
                }
                deltaTime = 0;
                pc._leftPossessingTime = Protocol.ControlMagic;
                return true;
            }
            if (ctl != null) Release(ctl, "the possession ended without notice");
            var ps = Find(p);
            if (ps == null) return true;
            ps.CooldownPending = false; // H5 missed it; the rule below re-applies "one per death"
            if (ps.Hello) ApplyRule(ps, pc, Control.NowMs);
            return true;
        }

        // =====================================================================================================
        // ends of a possession (H5, H6) and of a control
        // =====================================================================================================

        /// <summary>H5: the possessor side of a possession end, after the game set its 40 s cooldown and told everyone.</summary>
        public static void OnPossessorCleared(VPlayer p, PossessionController pc)
        {
            var ctl = FindCtlByPossessor(p);
            if (ctl != null) EndControl(ctl, "the possession ended");
            var ps = Find(p);
            if (ps == null || !ps.CooldownPending) return;
            ps.CooldownPending = false;
            if (!Settings.Enabled) return; // control is off: the game's cooldown stays
            // Not 0: the release path sends EndPossessingSig after this, and the possessor's game sets its own 40 s on
            // it; only the game's "cooldown over" sync, sent when the countdown crosses 0 on a later tick, undoes that.
            long now = Control.NowMs;
            pc._leftPossessingCooltime = Math.Max(RequiredCooldown(ps, now), 1L);
            Log.Info($"Player {Name(p)}: {RuleText(ps, RequiredCooldown(ps, now))}");
        }

        /// <summary>H6: the mimic side of a possession end (release, death, silence).</summary>
        public static void OnMimicCleared(VCreature mimic)
        {
            var ctl = FindCtlByMimic(mimic);
            if (ctl != null) EndControl(ctl, mimic.IsAliveStatus() ? "the possession ended" : "the mimic died");
        }

        /// <summary>Ends a control through the game's possession end (both sides, every client told), then cleans up.</summary>
        static void Release(Ctl ctl, string reason)
        {
            if (!IsRegistered(ctl)) return;
            ctl.EndReason = ctl.EndReason ?? reason;
            try
            {
                var ppc = ctl.Possessor.PossessionControlUnit;
                if (ppc != null && ppc._isPossessing && ppc._possessingTargetActorID == ctl.MimicId) ppc.OnCancelPossessing();
            }
            catch (Exception e) { Log.ErrorOnce("release-possessor", "Ending the possession (player side) failed: " + e); }
            try
            {
                // Still flagged when the player side was already gone (revived, removed). Only the mimic side here:
                // OnCancelPossessed would also clear whatever that player possesses now.
                var mpc = ctl.Mimic.PossessionControlUnit;
                if (mpc != null && mpc._isPossessed && mpc._possessorActorID == ctl.PossessorId) mpc.ClearPossessedStateInternal();
            }
            catch (Exception e) { Log.ErrorOnce("release-mimic", "Ending the possession (mimic side) failed: " + e); }
            EndControl(ctl, reason);
        }

        /// <summary>H7 threw for this mimic: give it back (through the game's possession end) rather than leave a
        /// half-controlled mimic. The feature fails closed.</summary>
        public static void FailSafe(VCreature mimic, Exception e)
        {
            var ctl = FindCtlByMimic(mimic);
            if (ctl == null) return;
            ctl.EndReason = "error: " + e.GetType().Name;
            try { Release(ctl, ctl.EndReason); }
            catch (Exception e2) { Log.ErrorOnce("failsafe", "Releasing a mimic after an error failed: " + e2); EndControl(ctl, ctl.EndReason); }
        }

        /// <summary>Forgets a control and gives the mimic back to its AI. Idempotent.</summary>
        static void EndControl(Ctl ctl, string reason)
        {
            if (!IsRegistered(ctl)) return;
            ByMimic.Remove(ctl.MimicId);
            var ps = ctl.State;
            if (ps != null && ReferenceEquals(ps.Active, ctl))
            {
                ps.Active = null;
                ps.UsedThisDeath = true;
                ps.CooldownPending = true;
            }
            var mimic = ctl.Mimic;
            if (mimic.IsAliveStatus())
            {
                try
                {
                    // The behaviour tree restarts from its root; aggro gathered while controlled is dropped so it doesn't
                    // snap-attack whoever hit it meanwhile.
                    mimic.MovementControlUnit?.StopMove();
                    var ai = mimic.AIControlUnit;
                    ai?.Flush();
                    ai?.ResetAggro();
                }
                catch (Exception e) { Log.ErrorOnce("resume-ai", "Resuming a mimic's AI failed: " + e); }
            }
            Log.Info($"Player {Name(ctl.Possessor)} no longer controls mimic {ctl.MimicId}: {ctl.EndReason ?? reason} (after {(Control.NowMs - ctl.StartMs) / 1000.0:0.0} s)");
        }

        // =====================================================================================================
        // leaks (H8, H9) and level start (H12)
        // =====================================================================================================

        /// <summary>H8: a dead player is about to be revived. The game would reset their possession silently, leaving
        /// the mimic possessed (and here frozen) forever.</summary>
        public static void OnRevive(VPlayer p)
        {
            var ctl = FindCtlByPossessor(p);
            if (ctl != null)
            {
                ctl.EndReason = "revived";
                Release(ctl, "revived");
            }
            if (Players.TryGetValue(p.ObjectID, out var ps) && ReferenceEquals(ps.Player, p)) Drop(ps, "revived");
        }

        /// <summary>H9: a player leaves the room (disconnect, room change). The game never ends their possession.</summary>
        public static void OnRemovePlayer(IVroom room, int actorID)
        {
            var p = room.FindPlayerByObjectID(actorID);
            if (p == null) return;
            var ctl = FindCtlByPossessor(p);
            if (ctl != null)
            {
                ctl.EndReason = "left the level";
                Release(ctl, "left the level");
            }
            if (Players.TryGetValue(actorID, out var ps) && ReferenceEquals(ps.Player, p)) Drop(ps, "left the level");
            if (ForeignConta.TryGetValue(actorID, out var hit) && ReferenceEquals(hit.Player, p)) ForeignConta.Remove(actorID);
        }

        /// <summary>H12: a new level: new players and mimics, nothing carries over.</summary>
        public static void Reset()
        {
            if (ByMimic.Count > 0) Log.Warn($"New level with {ByMimic.Count} mimic(s) still marked controlled; forgetting them");
            Players.Clear();
            ByMimic.Clear();
            GrabWindows.Clear();
            ForeignConta.Clear();
        }
    }
}
