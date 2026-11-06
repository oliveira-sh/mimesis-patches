// MIMESIS Hallucinations - host side: the game hooks (H1-H4). The logic is in HostLogic.cs and TalkRadiation.cs.
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every target, parameter name and private field below was checked against the EA 0.4.1 game code. Rules for every
// body:
//   - first line: if (!Control.<Part>Ready) return;  a half-applied part must change nothing.
//   - the logic is in try/catch: a hook that throws inside game code can break the room's update for everyone. On an
//     exception: log once and carry on vanilla.
//   - a cheap filter first: H2 runs every room tick for every dead player.
// Possession itself is vanilla again: nothing here changes it. H2 only observes a possession to add talk radiation.

using System;
using HarmonyLib;
using ReluProtocol.Enum;

namespace Hallucinations
{
    /// <summary>H1: our admin command. Without it this host never answers a hello, so every player stays vanilla.</summary>
    [Core(Part.Host)]
    [HarmonyPatch]
    static class HostCommandPatch
    {
        // H1. VWorld's constructor -> RegisterDispatcher -> RegisterAdminProtocol -> RegisterCommand fills
        // AdminCommandFuncs (private Dictionary<string, AdminCommandFunc>). The admin dispatcher looks a command up there
        // on every AdminCommandReq (exact key) and calls it on the room thread with the sending VPlayer; it then sends
        // AdminCommandRes(our return code) to the sender, which every client drops. A new VWorld is created each time
        // this game starts hosting (Hub.CreateVWorld), long after Plugin.Init patched this.
        [HarmonyPostfix, HarmonyPatch(typeof(VWorld), nameof(VWorld.RegisterCommand))]
        static void AfterRegisterCommand(VWorld __instance)
        {
            try { __instance.AdminCommandFuncs[Const.Command] = HostLogic.OnCommand; }
            catch (Exception e) { Log.ErrorOnce("H1", "Registering the command failed: " + e); }
        }
    }

    /// <summary>H2: talk radiation. Not needed by hallucinations: if it fails, only talk radiation is off.</summary>
    [Core(Part.Talk)]
    [HarmonyPatch(typeof(PossessionController), nameof(PossessionController.Update))]
    static class HostTalkPatch
    {
        // H2. The possession controller's per-tick update. It only runs while CanUpdate(): for a Possessor that means the
        // player is Dead. deltaTime is the room tick in ms. A postfix: the vanilla update may have just ended the
        // possession (time up -> OnCancelPossessing), so _isPossessing is read after it. Observe only: possession stays
        // exactly vanilla.
        static void Postfix(PossessionController __instance, long deltaTime)
        {
            if (!Control.TalkReady) return;
            if (__instance.RoleType != PossessionRoleType.Possessor || !__instance._isPossessing) return;
            try { TalkRadiation.Tick(__instance, deltaTime); }
            catch (Exception e) { Log.ErrorOnce("H2", "Talk radiation tick failed: " + e); }
        }
    }

    /// <summary>H3-H5: per-player and per-level cleanup, and fake players joining. Not core: all host state is keyed by
    /// VPlayer objects, which a new level replaces anyway; this just drops them on time.</summary>
    [HarmonyPatch]
    static class HostLifecyclePatch
    {
        // H3. A player leaving the room (disconnect, room change). The only overload; runs before the player is disposed.
        [HarmonyPrefix, HarmonyPatch(typeof(IVroom), nameof(IVroom.RemovePlayer))]
        // Each part cleans up by its own readiness (talk radiation works without the Host part), each in its own
        // try/catch so one failing never skips the other.
        static void BeforeRemovePlayer(IVroom __instance, int actorID)
        {
            if (Control.HostReady)
            {
                try { HostLogic.OnRemovePlayer(__instance, actorID); }
                catch (Exception e) { Log.ErrorOnce("H3", "BeforeRemovePlayer: " + e); }
            }
            if (Control.TalkReady)
            {
                try { TalkRadiation.OnRemovePlayer(__instance, actorID); }
                catch (Exception e) { Log.ErrorOnce("H3-talk", "BeforeRemovePlayer (talk): " + e); }
            }
        }

        // H5. A player joining a room. Only fake players matter (the others say hello): with Debug keys on they can be
        // sent hallucinations, so the target lists change.
        [HarmonyPostfix, HarmonyPatch(typeof(IVroom), nameof(IVroom.AddPlayer))]
        static void AfterAddPlayer(IVroom __instance, VPlayer player, MsgErrorCode __result)
        {
            if (!Control.HostReady || __result != MsgErrorCode.Success || player == null || !player.IsDummy) return;
            try { HostLogic.OnAddPlayer(__instance, player); }
            catch (Exception e) { Log.ErrorOnce("H5", "AfterAddPlayer: " + e); }
        }

        // H4. A new level: players, mimics and hallucinations of the last one are gone (new VPlayer objects per room).
        [HarmonyPostfix, HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
        static void NewLevel()
        {
            if (Control.HostReady)
            {
                try { HostLogic.Reset(); }
                catch (Exception e) { Log.ErrorOnce("H4", "NewLevel: " + e); }
            }
            if (Control.TalkReady)
            {
                try { TalkRadiation.Reset(); }
                catch (Exception e) { Log.ErrorOnce("H4-talk", "NewLevel (talk): " + e); }
            }
        }
    }
}
