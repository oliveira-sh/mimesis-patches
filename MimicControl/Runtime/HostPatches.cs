// MIMESIS MimicControl - host side: the game hooks (H1-H12). The logic is in HostControl.cs.
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every target, parameter name and private field below was checked against the EA 0.4.0 game code.
// Rules for every body:
//   - first line: if (!Control.HostReady) return (true);  a half-applied host side must change nothing.
//   - the logic is in try/catch: a hook that throws inside game code can break the room's update for everyone. On an
//     exception: log once and fall back to vanilla.
//   - a cheap filter first: most of these run for every player move, every monster tick or every DOT tick.
//   - only players who said hello this death and mimics in the control map get anything non-vanilla.
//   - never answer a StartPossessingReq without a response (the player's game waits for it): to refuse, return an
//     error code and let the dispatcher send it.
// No hook targets a tiny method (Mono may inline those, and the patch would silently never run): the ones the critic
// warned about (PossessionController.OnDead, getters, HandleChangeBT, IVroom.RecordPlayerDeath...) are left alone.

using System;
using System.Collections.Generic;
using HarmonyLib;
using ReluProtocol.Enum;
using UnityEngine;

namespace MimicControl
{
    /// <summary>H1-H2: the possessor's control packets. Vanilla dead players never send these markers.</summary>
    [Core(Side.Host)]
    [HarmonyPatch]
    static class HostInputPatch
    {
        // H1. A player's MoveStartReq. Vanilla always sends targetActorID 0 (the only place the game builds one), so
        // anything else is ours: a hello (Protocol.HelloMarker) or control input (the controlled mimic's id). Ours are
        // answered Success with no reply and the original skipped (it would fail with ActorisDead and send an error
        // MoveStartRes, or teleport the sender); stale input for a mimic no longer controlled is swallowed too. So is
        // input from a living sender: the possessor's game keeps sending until it hears of a revive, and that input
        // would otherwise run as the revived player's own move (position 0,0,0: an error reply and a teleport snap).
        [HarmonyPrefix, HarmonyPatch(typeof(MovementController), nameof(MovementController.DirectMoveStart))]
        static bool MoveStart(MovementController __instance, MoveStartReq msg, ref MsgErrorCode __result)
        {
            if (!Control.HostReady) return true;
            if (msg == null || msg.targetActorID == 0) return true;
            try
            {
                if (!(__instance._creature is VPlayer p)) return true;
                if (Protocol.IsHello(msg, out int version)) HostControl.OnHello(p, version);
                else if (!p.IsAliveStatus() && Protocol.TryReadInput(msg, out var input)) HostControl.OnInput(p, input);
                __result = MsgErrorCode.Success;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("H1", "MoveStart: " + e);
                return true;
            }
        }

        // H2. A player's UseSkillReq naming another actor (vanilla always names the sender): the grab. Always Success
        // with no reply (the possessor sends it without a callback); only a dead sender's grab is acted on, one still in
        // flight when the possessor was revived is just swallowed. Anything else runs vanilla (a dead player's own
        // skill request keeps failing with ActorisDead).
        [HarmonyPrefix, HarmonyPatch(typeof(SkillController), nameof(SkillController.HandleUseSkillReq))]
        static bool UseSkill(SkillController __instance, UseSkillReq req, ref MsgErrorCode __result)
        {
            if (!Control.HostReady) return true;
            try
            {
                if (!(__instance._self is VPlayer p)) return true;
                if (!Protocol.TryReadAttack(req, p.ObjectID, out int mimicId, out float yaw)) return true;
                if (!p.IsAliveStatus()) HostControl.OnAttack(p, mimicId, yaw);
                __result = MsgErrorCode.Success;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("H2", "UseSkill: " + e);
                return true;
            }
        }
    }

    /// <summary>H3-H6: possession start, the possessor's timer, and both ends of a possession.</summary>
    [Core(Side.Host)]
    [HarmonyPatch]
    static class HostPossessionPatch
    {
        // H3a. Start gate, only for players who said hello this death: the death rules (normally already enforced by the
        // cooldown set at hello) and the host-side distance (a living player within POSSESSION_DISTANCE + tolerance of
        // the mimic; the game checks it only on the possessor's side). A refusal returns the error code: the
        // dispatcher's HandleError sends the StartPossessingRes.
        [HarmonyPrefix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.HandleStartPossessing))]
        static bool StartGate(PossessionController __instance, int targetActorId, ref MsgErrorCode __result)
        {
            if (!Control.HostReady) return true;
            try
            {
                if (!(__instance._self is VPlayer p)) return true;
                var refusal = HostControl.CheckStart(p, __instance, targetActorId);
                if (refusal == MsgErrorCode.Success) return true;
                __result = refusal;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("H3a", "StartGate: " + e);
                return true;
            }
        }

        // H3b. Right after a successful start (StartPossessingRes and StartPossessingSig are already sent, so the ack
        // reaches the possessor after them): a hello'd player takes the mimic over. Also runs after a refusal (Harmony
        // runs postfixes even when a prefix skipped the original): __result is the error then.
        [HarmonyPostfix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.HandleStartPossessing))]
        static void StartTakeOver(PossessionController __instance, int targetActorId, MsgErrorCode __result)
        {
            if (!Control.HostReady || __result != MsgErrorCode.Success) return;
            try
            {
                if (__instance._self is VPlayer p) HostControl.OnStarted(p, __instance, targetActorId);
            }
            catch (Exception e) { Log.ErrorOnce("H3b", "StartTakeOver: " + e); }
        }

        // H4. The possession controller's per-tick update (possessors only while Dead, mimics only while alive). For a
        // controlling possessor: no time limit (deltaTime = 0) and _leftPossessingTime == Protocol.ControlMagic, so the
        // game's per-second possessor sync is our heartbeat ack; if the mimic is gone, the possession ends. For a dead
        // hello'd player: the death rules, re-asserted on the game's cooldown every tick. Mimics run unchanged.
        [HarmonyPrefix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.Update))]
        static bool PossessorUpdate(PossessionController __instance, ref long deltaTime)
        {
            if (!Control.HostReady) return true;
            if (__instance.RoleType != PossessionRoleType.Possessor) return true;
            try
            {
                if (!(__instance._self is VPlayer p)) return true;
                return HostControl.TickPossessor(p, __instance, ref deltaTime);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("H4", "PossessorUpdate: " + e);
                return true;
            }
        }

        // H5. Possessor side of every possession end (release, mimic death, revive, timeout, silence, disconnect). The
        // prefix keeps whether it was possessing; the postfix runs after the game set its 40 s cooldown and sent
        // CancelPossessingSig: it ends the control if still active and applies the cooldown policy
        // (HostControl.OnPossessorCleared).
        [HarmonyPrefix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.ClearPossessingStateInternal))]
        static void BeforeClearPossessing(PossessionController __instance, out bool __state)
        {
            __state = __instance._isPossessing;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.ClearPossessingStateInternal))]
        static void AfterClearPossessing(PossessionController __instance, bool __state)
        {
            if (!Control.HostReady || !__state) return;
            try
            {
                if (__instance._self is VPlayer p) HostControl.OnPossessorCleared(p, __instance);
            }
            catch (Exception e) { Log.ErrorOnce("H5", "AfterClearPossessing: " + e); }
        }

        // H6. Mimic side of every possession end. A controlled mimic is given back to its AI (if still alive).
        [HarmonyPrefix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.ClearPossessedStateInternal))]
        static void BeforeClearPossessed(PossessionController __instance, out bool __state)
        {
            __state = __instance._isPossessed;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PossessionController), nameof(PossessionController.ClearPossessedStateInternal))]
        static void AfterClearPossessed(PossessionController __instance, bool __state)
        {
            if (!Control.HostReady || !__state || !HostControl.AnyControlled) return;
            try
            {
                HostControl.OnMimicCleared(__instance._self);
            }
            catch (Exception e) { Log.ErrorOnce("H6", "AfterClearPossessed: " + e); }
        }
    }

    /// <summary>H7: a controlled mimic's brain. Skips the AI and runs the possessor's input instead.</summary>
    [Core(Side.Host)]
    [HarmonyPatch(typeof(AIController), nameof(AIController.Update))]
    static class HostAiPatch
    {
        // Runs every room tick for every living monster, before the original's own pause checks (so a controlled mimic
        // is released when the level pauses or ends). Controlled mimic: HostControl.TickMimic (watchdog, lease, busy
        // tracking, movement) and the whole brain is skipped (BT, aggro, horn). Any other monster: vanilla.
        static bool Prefix(AIController __instance)
        {
            if (!Control.HostReady || !HostControl.AnyControlled) return true;
            try
            {
                return !HostControl.TickMimic(__instance._self);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("H7", "AI tick of a controlled mimic: " + e);
                try { HostControl.FailSafe(__instance._self, e); }
                catch (Exception e2) { Log.ErrorOnce("H7-failsafe", "FailSafe: " + e2); }
                return true;
            }
        }
    }

    /// <summary>H8-H9: vanilla leaks that would leave a controlled mimic frozen. Not core: H7's watchdog also releases
    /// it, one tick later.</summary>
    [HarmonyPatch]
    static class HostLeakPatch
    {
        // H8. VCreature.Revive resets the possessor's PossessionController without telling the mimic (vanilla bug, hit by
        // the Revive patch). Before that: end the control the normal way, and forget the death (the next one starts
        // fresh). VPlayer.Revive calls this base method.
        [HarmonyPrefix, HarmonyPatch(typeof(VCreature), nameof(VCreature.Revive))]
        static void BeforeRevive(VCreature __instance)
        {
            if (!Control.HostReady) return;
            try
            {
                if (__instance is VPlayer p && p.LifeCycle == VCreatureLifeCycle.Dead) HostControl.OnRevive(p);
            }
            catch (Exception e) { Log.ErrorOnce("H8", "BeforeRevive: " + e); }
        }

        // H9. A player leaving the room (disconnect, room change) never ends their possession in vanilla.
        [HarmonyPrefix, HarmonyPatch(typeof(IVroom), nameof(IVroom.RemovePlayer))]
        static void BeforeRemovePlayer(IVroom __instance, int actorID)
        {
            if (!Control.HostReady) return;
            try
            {
                HostControl.OnRemovePlayer(__instance, actorID);
            }
            catch (Exception e) { Log.ErrorOnce("H9", "BeforeRemovePlayer: " + e); }
        }
    }

    /// <summary>H10: how a player died, for the death rules. Not core: without it a death is classified later from what
    /// the player object still knows (no attacker: fewer deaths count as self-inflicted).</summary>
    [HarmonyPatch]
    static class HostDeathPatch
    {
        // H10a. Every normal death (VPlayer.OnDying calls this base method): sig.reasonOfDeath / sig.attackerActorID.
        [HarmonyPostfix, HarmonyPatch(typeof(VCreature), nameof(VCreature.OnDying))]
        static void AfterDying(VCreature __instance, ActorDyingSig sig)
        {
            if (!Control.HostReady) return;
            try
            {
                if (__instance is VPlayer p) HostControl.OnDeath(p, sig, forced: false);
            }
            catch (Exception e) { Log.ErrorOnce("H10a", "AfterDying: " + e); }
        }

        // H10b. Forced deaths (AdminKill): barge-in late joiners (killed 3 s after loading), admin kill, kill-all.
        [HarmonyPostfix, HarmonyPatch(typeof(VCreature), nameof(VCreature.OnForceDying))]
        static void AfterForceDying(VCreature __instance)
        {
            if (!Control.HostReady) return;
            try
            {
                if (__instance is VPlayer p) HostControl.OnDeath(p, null, forced: true);
            }
            catch (Exception e) { Log.ErrorOnce("H10b", "AfterForceDying: " + e); }
        }
    }

    /// <summary>H11: Attack strength, and the "conta from someone else" record for the self-inflicted rule. Not core:
    /// without it grabs are full strength and fewer Conta deaths are told apart.</summary>
    [HarmonyPatch]
    static class HostStrengthPatch
    {
        // Every abnormal mutable-stat tick on a creature (the grab's conta DOT 30003 lands here with the grabbing mimic
        // as CasterActorID). Overloaded private method: the argument type picks this overload. A prefix, because a tick
        // that kills runs the death inside the original. The args' fields are readonly: a scaled copy replaces them.
        [HarmonyPrefix, HarmonyPatch(typeof(StatManager), nameof(StatManager.OnChangeAbnormalStat), new[] { typeof(ApplyMutableStatsAbnormalArgs) })]
        static void ScaleConta(StatManager __instance, ref ApplyMutableStatsAbnormalArgs mutableArgs)
        {
            if (!Control.HostReady) return;
            if (mutableArgs == null || mutableArgs.StatType != MutableStatType.Conta || mutableArgs.Value <= 0) return;
            try
            {
                var victim = __instance._self;
                if (victim != null) HostControl.OnContaTick(victim, ref mutableArgs);
            }
            catch (Exception e) { Log.ErrorOnce("H11", "ScaleConta: " + e); }
        }
    }

    /// <summary>H11b: conta from a field skill (the "cleaner" monster's gas round attack adds conta directly, with no
    /// abnormal). Not core: without it such a Conta death could count as self-inflicted.</summary>
    [HarmonyPatch]
    static class HostFieldContaPatch
    {
        // A prefix, like H11: a hit that kills runs the death inside the original. FieldSkillObject.Update calls it.
        [HarmonyPrefix, HarmonyPatch(typeof(FieldSkillObject), nameof(FieldSkillObject.OnHit),
            new[] { typeof(FieldSkillHitInputData), typeof(IsDamageImmuned), typeof(SkillPushDelegate) })]
        static void BeforeFieldHit(FieldSkillObject __instance, FieldSkillHitInputData hitInfo)
        {
            if (!Control.HostReady) return;
            var seq = hitInfo?.SkillSequenceInfo;
            if (seq == null || seq.MutableStatType != MutableStatType.Conta || seq.MutableValue <= 0) return;
            try
            {
                HostControl.OnFieldContaHit(__instance, hitInfo.Targets);
            }
            catch (Exception e) { Log.ErrorOnce("H11b", "BeforeFieldHit: " + e); }
        }
    }

    /// <summary>H11c: conta from a projectile (a teammate's dirty-water sprayer, skill 11006 -> projectile 50104, adds
    /// +700 conta per hit to allies directly, with no abnormal and no field skill). Not core: without it a death from a
    /// teammate's spraying could count as self-inflicted.</summary>
    [HarmonyPatch]
    static class HostProjectileContaPatch
    {
        // A prefix, like H11: a hit that kills runs the death inside the original. The projectile's own update calls it
        // (HandleRaycastProj / HandlePhysicsProj).
        [HarmonyPrefix, HarmonyPatch(typeof(VProjectileObject), nameof(VProjectileObject.OnHit),
            new[] { typeof(List<int>), typeof(Vector3), typeof(Vector3), typeof(DamageCauseType), typeof(IsDamageImmuned), typeof(SkillPushDelegate) })]
        static void BeforeProjectileHit(VProjectileObject __instance, List<int> hitTargetActorIDs)
        {
            if (!Control.HostReady || hitTargetActorIDs == null || hitTargetActorIDs.Count == 0) return;
            try
            {
                var info = __instance._projectileInfo;
                if (info == null || info.MutableStatType != MutableStatType.Conta || info.MutableValue <= 0) return;
                HostControl.OnProjectileContaHit(__instance, hitTargetActorIDs);
            }
            catch (Exception e) { Log.ErrorOnce("H11c", "BeforeProjectileHit: " + e); }
        }
    }

    /// <summary>H12: a new level starts with no control state (players and mimics are new objects).</summary>
    [HarmonyPatch]
    static class HostLevelPatch
    {
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
        static void NewLevel()
        {
            if (!Control.HostReady) return;
            try { HostControl.Reset(); }
            catch (Exception e) { Log.ErrorOnce("H12", "NewLevel: " + e); }
        }
    }
}
