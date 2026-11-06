// MIMESIS Traitor - the game hooks. The logic is in Game.cs (host) and Client.cs; the tram's destinations in
// Common/TramMode.cs.
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every target below was checked against the EA 0.4.1 game code. Every body runs in try/catch: on an exception it logs
// once and the game carries on as it would without this patch.

using System;
using System.Collections.Generic;
using HarmonyLib;
using MimesisPatches;
using Mimic.Actors;
using ReluProtocol;
using ReluProtocol.Enum;

namespace Traitor
{
    /// <summary>T0-T5: who has the patch, the tram's destinations and the level's lifecycle (host).</summary>
    [HarmonyPatch]
    static class LevelPatches
    {
        // T0. The host's admin commands, registered as hosting starts (VWorld's constructor): the players' "I have the
        // patch" hello (TramMode). Destinations are only offered when everyone has it (checked again at the lever).
        [HarmonyPostfix, HarmonyPatch(typeof(VWorld), nameof(VWorld.RegisterCommand))]
        static void AfterRegisterCommand(VWorld __instance)
        {
            try { TramMode.RegisterCommand(__instance); }
            catch (Exception e) { Log.ErrorOnce("T0", "Registering the patch hello failed: " + e); }
        }

        // T1. The tram rolled its two destinations (start of a trip, or a reroll).
        [HarmonyPostfix, HarmonyPatch(typeof(VWaitingRoom), nameof(VWaitingRoom.RollDiceDungeon))]
        static void AfterRoll(VWaitingRoom __instance)
        {
            if (!TramMode.IsHost) return;
            try { TramMode.OnRoll(__instance); }
            catch (Exception e) { Log.ErrorOnce("T1", "Offering a traitor destination failed: " + e); }
        }

        // T2. The lever in the tram: is it leaving for the traitor destination?
        [HarmonyPostfix, HarmonyPatch(typeof(VWaitingRoom), nameof(VWaitingRoom.OnRequestStartGame))]
        static void AfterStartGame(VWaitingRoom __instance, MsgErrorCode __result, int forceDungeonMasterID)
        {
            if (!TramMode.IsHost) return;
            try { TramMode.OnStartGame(__instance, __result, forceDungeonMasterID); }
            catch (Exception e) { Log.ErrorOnce("T2", "Checking the destination failed: " + e); }
        }

        // T3. A new level.
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
        static void NewLevel(DungeonRoom __instance)
        {
            try
            {
                Game.Reset();
                TramMode.OnDungeonCreated(__instance);
            }
            catch (Exception e) { Log.ErrorOnce("T3", "New level failed: " + e); }
        }

        // T4. A room turns its AI on: the end of a level's arrival cutscene (GameActionEnableAI).
        [HarmonyPostfix, HarmonyPatch(typeof(IVroom), nameof(IVroom.EnableAIControl))]
        static void AfterEnableAi(IVroom __instance, bool enable)
        {
            try { TramMode.OnAiEnabled(__instance, enable); }
            catch (Exception e) { Log.ErrorOnce("T4", "Starting the traitor job failed: " + e); }
        }

        // T5. A player finished loading a room (the tram's tags; one of the group coming in late goes to their spot; a
        // reconnect's role).
        [HarmonyPostfix, HarmonyPatch(typeof(IVroom), nameof(IVroom.OnLevelLoadComplete))]
        static void AfterLoad(IVroom __instance, VPlayer player)
        {
            try
            {
                TramMode.OnPlayerLoaded(__instance, player);
                TramScatter.OnPlayerLoaded(__instance, player);
                Game.OnPlayerLoaded(__instance, player);
            }
            catch (Exception e) { Log.ErrorOnce("T5", "Sending a player's info failed: " + e); }
        }
    }

    /// <summary>T6-T9: monsters ignore the traitor (host).</summary>
    [HarmonyPatch]
    static class IgnorePatches
    {
        // T6. Every way a monster gains aggro (hit, sound, movement, sight, faction scan) asks this first.
        [HarmonyPostfix, HarmonyPatch(typeof(AIController), "CanHoldAggro")]
        static void AfterCanHoldAggro(VActor target, ref bool __result)
        {
            if (__result && Game.IsTraitor(target)) __result = false;
        }

        // T7. Every target pick by aggro (max aggro, nearest, farthest, random) asks this.
        [HarmonyPostfix, HarmonyPatch(typeof(AggroObject), nameof(AggroObject.CanTarget))]
        static void AfterCanTarget(AggroObject __instance, ref bool __result)
        {
            if (__result && Game.IsTraitor(__instance.Target)) __result = false;
        }

        // T8. Whether a skill can hit an actor (every attack's hit check). A monster's explosion or projectile checks on
        // its own object (FieldSkillObject, VProjectileObject): what counts is the monster that cast it. Traps and other
        // hazards of the level (no caster) still hit everyone.
        [HarmonyPostfix, HarmonyPatch(typeof(VActor), nameof(VActor.IsValidTarget), typeof(HitTargetType), typeof(VActor))]
        static void AfterIsValidTarget(VActor __instance, VActor actor, ref bool __result)
        {
            if (!__result || !Game.IsTraitor(actor)) return;
            VActor source = __instance is FieldSkillObject f ? f.CasterActor
                : __instance is VProjectileObject p ? p._parentActor
                : __instance;
            if (source is VMonster) __result = false;
        }

        // T9. A behavior tree setting its target directly (watching / moving players): never the traitor.
        [HarmonyPrefix, HarmonyPatch(typeof(BehaviorTreeState), nameof(BehaviorTreeState.SetTargetActor))]
        static bool BeforeSetTarget(BehaviorTreeState __instance, VCreature target)
        {
            return !(__instance.Self is VMonster && Game.IsTraitor(target));
        }
    }

    /// <summary>T10-T13: the job's events (host).</summary>
    [HarmonyPatch]
    static class JobPatches
    {
        // T10. A death in a level.
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), nameof(DungeonRoom.OnActorEvent))]
        static void AfterActorEvent(DungeonRoom __instance, VActorEventArgs args)
        {
            if (!(args is GameActorDeadEventArgs dead) || !(dead.Victim is VPlayer victim)) return;
            try { Game.OnPlayerDied(__instance, victim, dead.AttackerActorID); }
            catch (Exception e) { Log.ErrorOnce("T10", "A death in the traitor job failed: " + e); }
        }

        // T11. The level ends (success or failure).
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), "SetDungeonState")]
        static void AfterSetState(DungeonRoom __instance, DungeonState state)
        {
            if (state != DungeonState.Success && state != DungeonState.Failed) return;
            try { Game.OnLevelEnded(__instance); }
            catch (Exception e) { Log.ErrorOnce("T11", "Ending the traitor job failed: " + e); }
        }

        // T12. The tram's lever in the level: locked for a while. The game asks this before a lever may move to a state
        // (the tram's own "not enough money" check): refusing the move that ends the level (DUNGEON_END) with
        // NotEnoughCurrency makes the lever's game ask for Idle on its own, so it springs back and nobody is left
        // holding it, as in the tram. The move is refused before anything changes: no state, no "tram started" toast.
        [HarmonyPostfix, HarmonyPatch(typeof(IVroom), "ValidateMoveToMaintenanceCurrency")]
        static void AfterLeverCheck(IVroom __instance, StateLevelObjectInfo levelObject, int fromState, int toState, ref MsgErrorCode __result)
        {
            if (__result != MsgErrorCode.Success || !(__instance is DungeonRoom room) || levelObject == null) return;
            try
            {
                var actions = levelObject.GetGameActions(fromState, toState);
                if (actions == null) return;
                bool leaves = false;
                foreach (var a in actions)
                    if (a is GameActionDungeonEnd || a is GameActionMoveToWaitingRoom) leaves = true;
                if (leaves && !Game.CanLeave(room)) __result = MsgErrorCode.NotEnoughCurrency;
            }
            catch (Exception e) { Log.ErrorOnce("T12", "The lever check failed: " + e); }
        }

        // T13. A player leaving a room.
        [HarmonyPrefix, HarmonyPatch(typeof(IVroom), nameof(IVroom.RemovePlayer))]
        static void BeforeRemovePlayer(IVroom __instance, int actorID)
        {
            try { Game.OnPlayerLeft(__instance, actorID); }
            catch (Exception e) { Log.ErrorOnce("T13", "A player leaving failed: " + e); }
        }
    }

    /// <summary>T14: where everyone starts (host). Its own class: without it (a game update) everyone is moved to their
    /// spot as the level starts instead (TramScatter.MoveStragglers).</summary>
    [HarmonyPatch]
    static class StartPatches
    {
        // T14. The game picks where a player entering a room appears (for a level: a seat in the tram). In the traitor
        // level before it started: their own spot inside the building (TramScatter). Deathmatch hooks the same method;
        // only the patch whose level it is answers, the other leaves the result alone.
        [HarmonyPrefix, HarmonyPatch(typeof(IVroom), "GetPlayerStartPoint")]
        static bool BeforeStartPoint(IVroom __instance, ref SpawnPointData __result)
        {
            try
            {
                var spot = TramScatter.Next(__instance);
                if (spot == null) return true;
                __result = spot;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("T14", "Picking a start spot failed: " + e);
                return true;
            }
        }
    }

    /// <summary>C3: every player's game draws the level as from where they start (inside the building). Its own class:
    /// without it, the patch's driver does the same a moment later (TramMode.ClientTick).</summary>
    [HarmonyPatch]
    static class IndoorPatches
    {
        // C3. Your avatar was just placed where the host put it (the game decided indoors or outdoors before it existed):
        // decided again now. Deathmatch hooks it too; the second call changes nothing.
        [HarmonyPostfix, HarmonyPatch(typeof(GamePlayScene), nameof(GamePlayScene.ResolveLevelLoadCompleteRes))]
        static void AfterLevelLoad(GamePlayScene __instance, bool __result)
        {
            if (!__result) return;
            try { TramMode.RecheckIndoor(__instance); }
            catch (Exception e) { Log.ErrorOnce("C3", "Checking whether you're indoors failed: " + e); }
        }
    }

    /// <summary>C1-C2: every player's game.</summary>
    [HarmonyPatch]
    static class ClientPatches
    {
        // C1. Messages from the host (ProtoActor's DebugInfoSig handler; ours never reach the game).
        [HarmonyPrefix, HarmonyPatch(typeof(ProtoActor), nameof(ProtoActor.OnDebugInfoSig))]
        static bool BeforeDebugInfo(ProtoActor __instance, DebugInfoSig sig)
        {
            try { return !TramMode.OnDebugInfo(__instance, sig); }
            catch (Exception e)
            {
                Log.ErrorOnce("C1", "Reading a message from the host failed: " + e);
                return true;
            }
        }

        // C2. A destination card on the tram's screen: the traitor destination is marked.
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_NextMap), nameof(UIPrefab_NextMap.ApplyMap))]
        static void AfterApplyMap(UIPrefab_NextMap __instance, int dungeonMasterId, bool isJackpot, bool weatherRainy)
        {
            try { TramMode.OnCardApplied(__instance, dungeonMasterId, isJackpot, weatherRainy); }
            catch (Exception e) { Log.ErrorOnce("C2", "Marking a destination failed: " + e); }
        }
    }
}
