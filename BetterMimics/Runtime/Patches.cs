// MIMESIS BetterMimics - the game hooks (host only). The logic is in Brain.cs (body) and Voice.cs (voice).
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every target below was checked against the EA 0.4.1 game code. Each hook is its own class, applied on its own
// (Plugin.Init): a game update that changes one target turns off only the features that need it (Plugin's feature
// flags), never half a feature (Harmony doesn't undo the hooks of a class that failed halfway). Each body checks its
// setting and feature and runs in try/catch: a hook that throws inside the server's update would break the level for
// everyone, so on an exception it logs once and the game carries on as it would without this patch. On a player's game
// (not hosting) none of these run: AI and the voice picker only run on the host.

using System;
using System.Collections.Generic;
using HarmonyLib;
using Mimic.Voice;
using Mimic.Voice.SpeechSystem;
using ReluProtocol.Enum;
using UnityEngine;

namespace BetterMimics
{
    // ================================================================================================ body (Brain)

    /// <summary>B1. A creature's AI tick (VWorld's update, every server frame; the BT itself runs every 200 ms in it).</summary>
    [HarmonyPatch(typeof(AIController), nameof(AIController.Update))]
    static class TickHook
    {
        static void Postfix(AIController __instance)
        {
            if (!Settings.Enabled || !Plugin.BodyReady) return;
            try { Brain.Tick(__instance); }
            catch (Exception e) { Log.ErrorOnce("B1", "Mimic tick failed: " + e); }
        }
    }

    /// <summary>B2. Path movement (how the BT walks a monster). A wandering mimic walks: the BT flips between walking and
    /// running every few seconds (40 %), which no player does. It still runs for long trips and when it chases.</summary>
    [HarmonyPatch(typeof(MovementController), nameof(MovementController.PathMoveStart))]
    static class WalkHook
    {
        static void Prefix(MovementController __instance, Vector3 targetPos, ref ActorMoveType moveType)
        {
            if (!Settings.Enabled || !Settings.Walk || moveType != ActorMoveType.Run) return;
            try
            {
                var c = __instance._creature;
                if (c == null || !Brain.IsMimic(c)) return;
                string bt = c.AIControlUnit?.CurrentBTTemplateName;
                float d = new Vector2(targetPos.x - c.PositionVector.x, targetPos.z - c.PositionVector.z).magnitude;
                if ((bt == "passive" && d < 25f) || (bt == "search" && d < 14f)) moveType = ActorMoveType.Walk;
            }
            catch (Exception e) { Log.ErrorOnce("B2", "Walk/run choice failed: " + e); }
        }
    }

    /// <summary>B3. The BT copies a player's inventory (fake items) every 30 s; that would wipe the real scrap a mimic
    /// picked up. While it carries real scrap it keeps its own hands. (Not tied to the setting: scrap picked up before it
    /// was switched off must survive too.)</summary>
    [HarmonyPatch(typeof(AIController), nameof(AIController.CopyInventory))]
    static class CopyGuardHook
    {
        static bool Prefix(AIController __instance, ref bool __result)
        {
            try
            {
                if (!Brain.CarriesRealItem(__instance._self)) return true;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("B3", "Inventory copy check failed: " + e);
                return true;
            }
        }
    }

    /// <summary>B4. A new level: the last level's mimics and voices are gone (actor ids repeat).</summary>
    [HarmonyPatch(typeof(DungeonRoom), MethodType.Constructor, typeof(VRoomManager), typeof(long), typeof(IVRoomProperty))]
    static class LevelResetHook
    {
        static void Postfix()
        {
            try
            {
                Brain.Reset();
                Voice.Reset();
            }
            catch (Exception e) { Log.ErrorOnce("B4", "New level reset failed: " + e); }
        }
    }

    /// <summary>B5. The BT puts a fake item in a mimic's hand (SetFakeItem: a weapon as it starts a fight). The game
    /// clears the slot first, deleting what's there: real scrap is dropped at its feet instead. (Not tied to the setting
    /// either.)</summary>
    [HarmonyPatch(typeof(InventoryController), "ClearSlotForFakeItem")]
    static class FakeItemGuardHook
    {
        static void Prefix(InventoryController __instance, int slotIndex)
        {
            try { Brain.DropRealItemBeforeFake(__instance, slotIndex); }
            catch (Exception e) { Log.ErrorOnce("B5", "Saving a mimic's scrap from a fake item failed: " + e); }
        }
    }

    // ================================================================================================ voice (Voice)

    /// <summary>V1. A player's new line reaches the host (SpeechEventArchive.OnAddSpeechEvent, asServer: after the player
    /// finished saying it; the game then lets nearby mimics answer).</summary>
    [HarmonyPatch(typeof(SpeechEventArchive), "OnAddSpeechEvent")]
    static class LineTagHook
    {
        static void Prefix(SpeechEventArchive __instance, SpeechEvent speechEvent, bool asServer)
        {
            if (!asServer || !Settings.Enabled || !Settings.Voice) return;
            try { Voice.OnLineArrived(__instance.SteamID, speechEvent); }
            catch (Exception e) { Log.ErrorOnce("V1", "Tagging a line failed: " + e); }
        }
    }

    /// <summary>V2. The game's situation match for a mimic's line: narrowed to answer-like (or opening) lines; if that
    /// leaves nothing that fits, the game's own pick runs unchanged.</summary>
    [HarmonyPatch(typeof(SpeechEventAdditionalGameData), nameof(SpeechEventAdditionalGameData.PickBestMatch))]
    static class PickHook
    {
        static void Prefix(bool periodic, SpeechEventAdditionalGameData curGameData, ref List<(ulong playerSteamId, SpeechEvent evt)> allEvents,
            out List<(ulong playerSteamId, SpeechEvent evt)> __state)
        {
            __state = null;
            if (!Settings.Enabled || !Settings.Voice) return;
            try { Voice.BeforePick(periodic, curGameData, ref allEvents, out __state); }
            catch (Exception e) { Log.ErrorOnce("V2a", "Narrowing the lines failed: " + e); }
        }

        static void Postfix(MimicVoiceSpawner.MimicContext context, SpeechEventAdditionalGameData curGameData, bool periodic, int pickCount,
            float playTimeIntervalRandom, ref SpeechEvent speechEvent, ref ulong mimickingPlayerSteamId, ref string pickReason,
            ref bool __result, List<(ulong playerSteamId, SpeechEvent evt)> __state)
        {
            if (__result || __state == null) return;
            try
            {
                __result = Voice.RetryUnfiltered(context, __state, curGameData, periodic, pickCount, playTimeIntervalRandom,
                    out speechEvent, out mimickingPlayerSteamId, out pickReason);
            }
            catch (Exception e) { Log.ErrorOnce("V2b", "Retrying the game's own pick failed: " + e); }
        }
    }

    /// <summary>V3. The recordings a mimic may use (by its voice rule). One identity per mimic (needs V6, V7 and B4:
    /// Plugin.VoiceReady).</summary>
    [HarmonyPatch(typeof(VoiceManager), nameof(VoiceManager.GetAllSpeechEventArchives))]
    static class ArchivesHook
    {
        static void Postfix(VoiceManager __instance, BTVoiceRule rule, Vector3 position, MimicVoiceSpawner.MimicContext prevContext,
            ref ulong MimickingPlayerSteamIDByVoiceRule, ref List<SpeechEventArchive> __result)
        {
            if (!Settings.Enabled || !Settings.Voice || !Plugin.VoiceReady) return;
            try { Voice.LockIdentity(__instance, rule, position, prevContext, ref MimickingPlayerSteamIDByVoiceRule, ref __result); }
            catch (Exception e) { Log.ErrorOnce("V3", "Keeping a mimic's voice failed: " + e); }
        }
    }

    /// <summary>V4. A mimic is about to say something: which kind of line (V2 narrows only its own lines and answers),
    /// and a line of its own waits while someone near it talks. An answer doesn't: the game only prepares it as a
    /// player finishes a line, while that player can still read as talking (push-to-talk held, the voice's fade-out),
    /// and a dropped answer is never tried again. A line for being hit or attacking comes at once.</summary>
    [HarmonyPatch(typeof(MimicVoiceSpawner), "TryPrepareVoiceByContext")]
    static class PrepareHook
    {
        static bool Prefix(MimicVoiceSpawner.MimicContext context, MimicVoiceSpawner.MimicVoiceTrigger trigger,
            ref MimicVoiceSpawner.PreparedMimicVoiceSpawn preparedVoice, ref bool __result)
        {
            Voice.Trigger = trigger;
            if (!Settings.Enabled || !Settings.Voice || context == null || trigger != MimicVoiceSpawner.MimicVoiceTrigger.Periodic) return true;
            try
            {
                if (!Voice.SomeoneTalkingNear(context.MimicActorID)) return true;
                preparedVoice = default;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("V4", "Turn-taking check failed: " + e);
                return true;
            }
        }

        static void Postfix() => Voice.Trigger = null;
    }

    /// <summary>V5. A mimic answering a player: after a human pause, not 0.2 s. The game's delay is shared with the lines
    /// for being hit or attacking: it is set back right after (the reply's coroutine took its value already).</summary>
    [HarmonyPatch(typeof(VoiceManager), nameof(VoiceManager.TrySpawnMimicReply))]
    static class ReplyDelayHook
    {
        static void Prefix(VoiceManager __instance, out float __state)
        {
            __state = -1f;
            if (!Settings.Enabled || !Settings.Voice) return;
            try
            {
                __state = __instance.MimicVoiceDelay;
                __instance.MimicVoiceDelay = UnityEngine.Random.Range(0.55f, 1.2f);
            }
            catch (Exception e) { Log.ErrorOnce("V5", "Reply delay failed: " + e); }
        }

        static void Postfix(VoiceManager __instance, float __state)
        {
            if (__state >= 0f) __instance.MimicVoiceDelay = __state;
        }
    }

    /// <summary>V6. A mimic's line is picked: radio lines (walkie-talkie, transmitter) need a recording made with that
    /// radio, anyone's (V3 doesn't narrow them to the mimic's own voice).</summary>
    [HarmonyPatch(typeof(MimicVoiceSpawner), nameof(MimicVoiceSpawner.TryPickHeuristicSpeechEvent))]
    static class RadioPickHook
    {
        static void Prefix(SpeechType_Area speechType_Area) =>
            Voice.RadioPick = speechType_Area == SpeechType_Area.Transmitter || speechType_Area == SpeechType_Area.Walkietalkie;

        static void Postfix() => Voice.RadioPick = false;
    }

    /// <summary>V7. The game binds a persona to a mimic as it starts approaching players (whose habits it copies, and
    /// whose voice it then uses): the person it already sounds like, so it never changes voice.</summary>
    [HarmonyPatch(typeof(DLAgent.DLPlayerPersonaManager), nameof(DLAgent.DLPlayerPersonaManager.BindMimic))]
    static class PersonaHook
    {
        static void Prefix(VCreature mimic, ref ulong steamID)
        {
            if (!Settings.Enabled || !Settings.Voice || !Plugin.VoiceReady) return;
            try { Voice.OnBindPersona(mimic, ref steamID); }
            catch (Exception e) { Log.ErrorOnce("V7", "Binding a persona failed: " + e); }
        }
    }
}
