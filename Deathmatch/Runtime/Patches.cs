// MIMESIS Deathmatch - the game hooks. The logic is in Game.cs (host) and Client.cs; the tram's destinations in
// Common/TramMode.cs.
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every target below was checked against the EA 0.4.1 game code. Every body runs in try/catch: on an exception it logs
// once and the game carries on as it would without this patch.

using System;
using HarmonyLib;
using MimesisPatches;
using Mimic.Actors;
using ReluProtocol;
using ReluProtocol.Enum;

namespace Deathmatch
{
    /// <summary>D0-D5: who has the patch, the tram's destinations and the level's lifecycle (host).</summary>
    [HarmonyPatch]
    static class LevelPatches
    {
        // D0. The host's admin commands, registered as hosting starts (VWorld's constructor): the players' "I have the
        // patch" hello (TramMode). Destinations are only offered when everyone has it (checked again at the lever). A new
        // session: item ids start over, the lent shotguns of the last one are forgotten.
        [HarmonyPostfix, HarmonyPatch(typeof(VWorld), nameof(VWorld.RegisterCommand))]
        static void AfterRegisterCommand(VWorld __instance)
        {
            try
            {
                TramMode.RegisterCommand(__instance);
                Game.OnNewSession();
            }
            catch (Exception e) { Log.ErrorOnce("D0", "Registering the patch hello failed: " + e); }
        }

        // D1. The tram rolled its two destinations (start of a trip, or a reroll).
        [HarmonyPostfix, HarmonyPatch(typeof(VWaitingRoom), nameof(VWaitingRoom.RollDiceDungeon))]
        static void AfterRoll(VWaitingRoom __instance)
        {
            if (!TramMode.IsHost) return;
            try { TramMode.OnRoll(__instance); }
            catch (Exception e) { Log.ErrorOnce("D1", "Offering a deathmatch destination failed: " + e); }
        }

        // D2. The lever in the tram: is it leaving for the deathmatch destination?
        [HarmonyPostfix, HarmonyPatch(typeof(VWaitingRoom), nameof(VWaitingRoom.OnRequestStartGame))]
        static void AfterStartGame(VWaitingRoom __instance, MsgErrorCode __result, int forceDungeonMasterID)
        {
            if (!TramMode.IsHost) return;
            try { TramMode.OnStartGame(__instance, __result, forceDungeonMasterID); }
            catch (Exception e) { Log.ErrorOnce("D2", "Checking the destination failed: " + e); }
        }

        // D3. A new level (before any spawn: the spawn rules hold from the start).
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
        static void NewLevel(DungeonRoom __instance)
        {
            try
            {
                TramMode.OnDungeonCreated(__instance);
                Game.OnDungeonCreated(__instance);
            }
            catch (Exception e) { Log.ErrorOnce("D3", "New level failed: " + e); }
        }

        // D4. A room turns its AI on: the end of a level's arrival cutscene (GameActionEnableAI).
        [HarmonyPostfix, HarmonyPatch(typeof(IVroom), nameof(IVroom.EnableAIControl))]
        static void AfterEnableAi(IVroom __instance, bool enable)
        {
            try { TramMode.OnAiEnabled(__instance, enable); }
            catch (Exception e) { Log.ErrorOnce("D4", "Starting the deathmatch failed: " + e); }
        }

        // D5. A player finished loading a room (the tram's tags; the rules; one of the group coming in late goes to their
        // spot; a lent shotgun that got out).
        [HarmonyPostfix, HarmonyPatch(typeof(IVroom), nameof(IVroom.OnLevelLoadComplete))]
        static void AfterLoad(IVroom __instance, VPlayer player)
        {
            try
            {
                TramMode.OnPlayerLoaded(__instance, player);
                TramScatter.OnPlayerLoaded(__instance, player);
                Game.OnPlayerLoaded(__instance, player);
            }
            catch (Exception e) { Log.ErrorOnce("D5", "Sending a player's info failed: " + e); }
        }
    }

    /// <summary>D12: a player made for a room (host). Its own class: without it (a game update), a returning player's
    /// shotgun comes when they load, and a lent one that got out goes a moment after (D5).</summary>
    [HarmonyPatch]
    static class PlayerPatches
    {
        // D12. VPlayer.InitDefaultParam: the inventory was just restored from the player's snapshot, and the room hasn't
        // described the player to their game yet: the shotgun goes in (one of the group back in the deathmatch) or out
        // (anywhere else) here, so their game shows it right from the start.
        [HarmonyPostfix, HarmonyPatch(typeof(VPlayer), "InitDefaultParam")]
        static void AfterInit(VPlayer __instance)
        {
            try { Game.OnPlayerMade(__instance); }
            catch (Exception e) { Log.ErrorOnce("D12", "Checking a player's lent shotgun failed: " + e); }
        }
    }

    /// <summary>D6-D10: the deathmatch's rules (host).</summary>
    [HarmonyPatch]
    static class RulePatches
    {
        // D6. The level's spawn pass (every server frame): no monsters but mimics.
        [HarmonyPrefix, HarmonyPatch(typeof(DungeonRoom), nameof(DungeonRoom.ManageSpawnData))]
        static void BeforeSpawns(DungeonRoom __instance)
        {
            if (!Game.IsOurs(__instance)) return;
            try { Game.StripSpawns(__instance); }
            catch (Exception e) { Log.ErrorOnce("D6", "Keeping monsters out failed: " + e); }
        }

        // D7. Every monster's creation (spawn points, reserved spawns, game actions).
        [HarmonyPrefix, HarmonyPatch(typeof(IVroom), nameof(IVroom.CreateMonster))]
        static bool BeforeCreateMonster(IVroom __instance, int masterID, ref VMonster __result)
        {
            try
            {
                if (!Game.Refuse(__instance, masterID)) return true;
                __result = null;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("D7", "The monster check failed: " + e);
                return true;
            }
        }

        // D8. Whether a creature's inventory drops where it dies: not for a player in the deathmatch (the game then
        // clears it: their belongings are lost).
        [HarmonyPostfix, HarmonyPatch(typeof(VCreature), nameof(VCreature.DropInventoryOnDead), MethodType.Getter)]
        static void AfterDropInventory(VCreature __instance, ref bool __result)
        {
            if (!__result) return;
            try { if (!Game.KeepsInventoryOnDeath(__instance)) __result = false; }
            catch (Exception e) { Log.ErrorOnce("D8", "The death drop check failed: " + e); }
        }

        // D9. A death in a level.
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), nameof(DungeonRoom.OnActorEvent))]
        static void AfterActorEvent(DungeonRoom __instance, VActorEventArgs args)
        {
            if (!(args is GameActorDeadEventArgs dead) || !(dead.Victim is VPlayer victim)) return;
            try { Game.OnPlayerDied(__instance, victim, dead.AttackerActorID); }
            catch (Exception e) { Log.ErrorOnce("D9", "A death in the deathmatch failed: " + e); }
        }

        // D10. The level ends (success or failure).
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), "SetDungeonState")]
        static void AfterSetState(DungeonRoom __instance, DungeonState state)
        {
            if (state != DungeonState.Success && state != DungeonState.Failed) return;
            try { Game.OnLevelEnded(__instance); }
            catch (Exception e) { Log.ErrorOnce("D10", "Ending the deathmatch failed: " + e); }
        }
    }

    /// <summary>D11: where everyone starts (host). Its own class: without it (a game update) everyone is moved to their
    /// spot as the level starts instead (TramScatter.MoveStragglers).</summary>
    [HarmonyPatch]
    static class StartPatches
    {
        // D11. The game picks where a player entering a room appears (for a level: a seat in the tram). In the
        // deathmatch before it started: their own spot inside the building (TramScatter). Traitor hooks the same method;
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
                Log.ErrorOnce("D11", "Picking a start spot failed: " + e);
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
        // decided again now. Traitor hooks it too; the second call changes nothing.
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

        // C2. A destination card on the tram's screen: the deathmatch destination is marked.
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_NextMap), nameof(UIPrefab_NextMap.ApplyMap))]
        static void AfterApplyMap(UIPrefab_NextMap __instance, int dungeonMasterId, bool isJackpot, bool weatherRainy)
        {
            try { TramMode.OnCardApplied(__instance, dungeonMasterId, isJackpot, weatherRainy); }
            catch (Exception e) { Log.ErrorOnce("C2", "Marking a destination failed: " + e); }
        }
    }
}
