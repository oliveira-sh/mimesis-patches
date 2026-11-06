// MIMESIS BetterMimics - what mimics say, in whose voice, and when
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The game picks a mimic's lines on the host (MimicVoiceSpawner, about once a second per mimic, and as a reply when a
// player near it finishes a sentence). It already matches each recorded line's situation (players around, area, time,
// monsters, scrap...) to the mimic's. What gave mimics away, and what this changes:
//   - voice: in its default mood a mimic spoke with ANY player's recordings, so one mimic could sound like three people
//     in a minute. Now each mimic keeps one identity per level: the player its persona copies if it has one, else the
//     player farthest from it when it first speaks (the one least likely to be standing there); the persona the game
//     binds later, when it approaches players, is that same person. Radio lines (walkie-talkie, transmitter) still
//     use anyone's radio recordings; a voice whose player left is picked again.
//   - the original nearby: it stays quiet while the player it sounds like is within OriginalNearM of it (two of the
//     same voice in one place is a dead giveaway).
//   - talking over people: a mimic doesn't start a line of its own while a player near it is talking (answers come
//     when a player finishes a line: the game's own moment).
//   - replies: lines a player said right after someone else spoke are tagged as replies when they're recorded (on the
//     host); a mimic answering a player prefers those (and short lines), and a mimic speaking on its own prefers the
//     rest. If the preference leaves nothing that fits the situation, the game's own pick runs unchanged.
//   - timing: it answers 0.55-1.2 s after a player stops talking (the game's 0.2 s is quicker than people).
// Every change stays on the host and in the game's own voice playback: nothing new is sent to anyone.

using System;
using System.Collections.Generic;
using Mimic.Voice;
using Mimic.Voice.SpeechSystem;
using UnityEngine;

namespace BetterMimics
{
    static class Voice
    {
        const float OriginalNearM = 16f;
        const float TalkingNearM = 20f;
        const float ReplyWindowSec = 2.5f;       // another player's line ended at most this long before this one began
        const float ReplyShortSec = 2.2f;        // a short line works as an answer too
        const float StarterMinSec = 0.9f;        // too short to start talking with ("yeah", "ok")
        const int MinLines = 3;                  // a voice needs this many warmed-up lines to be a mimic's identity

        static readonly Dictionary<int, ulong> _identity = new Dictionary<int, ulong>();
        static readonly HashSet<long> _replies = new HashSet<long>();
        static readonly List<(float at, ulong who)> _recent = new List<(float at, ulong who)>();

        [ThreadStatic] static bool _retrying;
        [ThreadStatic] internal static MimicVoiceSpawner.MimicVoiceTrigger? Trigger; // the line being picked (V4), null = unknown
        [ThreadStatic] internal static bool RadioPick;   // a walkie-talkie or transmitter line (V6): anyone's radio lines, as the game

        /// <summary>A new level: mimic ids repeat between levels.</summary>
        internal static void Reset() => _identity.Clear();

        /// <summary>The voice a mimic speaks with (0 = none yet).</summary>
        internal static bool TryGetIdentity(int mimicActorId, out ulong steamId) => _identity.TryGetValue(mimicActorId, out steamId) && steamId != 0;

        // ---------------------------------------------------------------------------------------------- reply tags

        /// <summary>V1: a player's new line arrived on the host (after they finished saying it).</summary>
        internal static void OnLineArrived(ulong speaker, SpeechEvent e)
        {
            if (e == null) return;
            float now = Time.realtimeSinceStartup;
            float began = now - Mathf.Max(0f, e.Duration);
            bool reply = false;
            foreach (var r in _recent)
            {
                if (r.who == speaker) continue;
                if (r.at >= began - ReplyWindowSec && r.at <= began + 0.3f) { reply = true; break; }
            }
            if (reply)
            {
                if (_replies.Count >= 20000) _replies.Clear();   // a very long session: start over rather than grow forever
                _replies.Add(e.Id);
            }
            _recent.Add((now, speaker));
            _recent.RemoveAll(r => now - r.at > 30f);
            if (Settings.VerboseLog) Log.Info($"Line {e.Id} ({e.Duration:0.0} s) by {speaker}: {(reply ? "a reply" : "not a reply")}");
        }

        static bool IsReply(SpeechEvent e) => e != null && _replies.Contains(e.Id);

        /// <summary>V2 prefix: narrows the candidates to replies (answering) or to non-replies (speaking first). Only for
        /// lines it says on its own or as an answer: a line for something happening (it got hit, it attacks) is the
        /// game's own situation match.</summary>
        internal static void BeforePick(bool periodic, SpeechEventAdditionalGameData curGameData, ref List<(ulong playerSteamId, SpeechEvent evt)> allEvents,
            out List<(ulong playerSteamId, SpeechEvent evt)> original)
        {
            original = null;
            if (_retrying || allEvents == null || allEvents.Count < 2) return;
            if (Trigger != MimicVoiceSpawner.MimicVoiceTrigger.Periodic && Trigger != MimicVoiceSpawner.MimicVoiceTrigger.Reply) return;
            if (curGameData?.IncomingEventStart != null && curGameData.IncomingEventStart.Count > 0) return;
            var keep = new List<(ulong playerSteamId, SpeechEvent evt)>(allEvents.Count);
            foreach (var x in allEvents)
            {
                if (x.evt == null) continue;
                bool fits = periodic
                    ? !IsReply(x.evt) && x.evt.Duration >= StarterMinSec
                    : IsReply(x.evt) || x.evt.Duration <= ReplyShortSec;
                if (fits) keep.Add(x);
            }
            if (keep.Count < (periodic ? 3 : 1) || keep.Count == allEvents.Count) return;
            original = allEvents;
            allEvents = keep;
        }

        /// <summary>V2 postfix: nothing in the narrowed list fit the situation: the game's own pick, unchanged.</summary>
        internal static bool RetryUnfiltered(MimicVoiceSpawner.MimicContext context, List<(ulong playerSteamId, SpeechEvent evt)> original,
            SpeechEventAdditionalGameData curGameData, bool periodic, int pickCount, float playTimeIntervalRandom,
            out SpeechEvent speechEvent, out ulong mimickingPlayerSteamId, out string pickReason)
        {
            _retrying = true;
            try
            {
                return SpeechEventAdditionalGameData.PickBestMatch(context, original, curGameData, periodic, pickCount, playTimeIntervalRandom,
                    out speechEvent, out mimickingPlayerSteamId, out pickReason);
            }
            finally { _retrying = false; }
        }

        // ---------------------------------------------------------------------------------------------- identity

        /// <summary>V3 postfix of VoiceManager.GetAllSpeechEventArchives: one voice per mimic. Radio lines (walkie-talkie,
        /// transmitter) keep the game's list: those need a player's recording made with that radio, anyone's.</summary>
        internal static void LockIdentity(VoiceManager vm, BTVoiceRule rule, Vector3 position, MimicVoiceSpawner.MimicContext ctx,
            ref ulong steamIdByRule, ref List<SpeechEventArchive> result)
        {
            if (ctx == null || RadioPick || rule == BTVoiceRule.Phone) return;
            var all = vm.speechEventArchives;
            if (all == null || all.Count == 0) return;
            var players = ServerPlayers();
            if (rule == BTVoiceRule.Persona)
            {
                // The game's persona voice: the same person (V7 binds the persona to the identity). Quiet near them too.
                if (TryGetPersona(ctx.MimicActorID, out ulong persona) && OriginalNear(players, persona, position)) result = new List<SpeechEventArchive>();
                return;
            }
            if (!_identity.TryGetValue(ctx.MimicActorID, out ulong id) || !Usable(vm, all, id))
            {
                // The player its persona copies, if it has one already (one person for the whole level) and enough lines,
                // else a pick. A voice whose player left (their recordings can't be played any more) is picked again.
                id = TryGetPersona(ctx.MimicActorID, out ulong persona) && Usable(vm, all, persona) ? persona : PickIdentity(vm, all, players, position);
                if (id == 0) return; // nobody with enough lines yet: the game's own pick
                _identity[ctx.MimicActorID] = id;
                if (Settings.VerboseLog) Log.Info($"Mimic #{ctx.MimicActorID} speaks as {NameOf(players, id)} from now on");
            }
            var list = new List<SpeechEventArchive>(1);
            foreach (var a in all)
                if (a != null && a.SteamID == id && Pickable(vm, a)) list.Add(a);
            if (OriginalNear(players, id, position)) list.Clear(); // the real one is right here: keep quiet
            result = list;
            steamIdByRule = id;
        }

        /// <summary>V7: the game binds a persona (whose habits the mimic copies) when it starts approaching players: the
        /// person whose voice it already has, so it never changes voice.</summary>
        internal static void OnBindPersona(VCreature mimic, ref ulong steamId)
        {
            if (mimic == null || !TryGetIdentity(mimic.ObjectID, out ulong id) || id == steamId) return;
            var vm = Hub.s?.voiceman;
            if (vm == null || !Usable(vm, vm.speechEventArchives, id))
            {
                _identity.Remove(mimic.ObjectID); // that voice is gone (they left): the game's persona, and its voice from now on
                return;
            }
            if (Settings.VerboseLog) Log.Info($"Mimic #{mimic.ObjectID} copies the habits of the player it sounds like ({id}, not {steamId})");
            steamId = id;
        }

        static bool TryGetPersona(int mimicActorId, out ulong steamId)
        {
            steamId = 0;
            try { return Hub.s.vworld.PersonaManager.TryGetBoundSteamID(mimicActorId, out steamId) && steamId != 0; }
            catch (Exception) { return false; }
        }

        static bool OriginalNear(Dictionary<ulong, VPlayer> players, ulong id, Vector3 position) =>
            players.TryGetValue(id, out var original) && original.IsAliveStatus() && Vector3.Distance(original.PositionVector, position) < OriginalNearM;

        static bool Pickable(VoiceManager vm, SpeechEventArchive a)
        {
            try { return vm.IsArchivePickable(a); }
            catch (Exception) { return false; }
        }

        static ulong PickIdentity(VoiceManager vm, List<SpeechEventArchive> all, Dictionary<ulong, VPlayer> players, Vector3 pos)
        {
            ulong best = 0;
            float bestScore = float.MinValue;
            foreach (var a in all)
            {
                if (a == null || a.SteamID == 0) continue;
                if (!Usable(vm, a)) continue;
                float score;
                if (players.TryGetValue(a.SteamID, out var p))
                    score = Vector3.Distance(p.PositionVector, pos) + (p.IsAliveStatus() ? 0f : -5f);
                else score = 50f; // not in this level (left behind, or in the tram): far away by definition
                score += UnityEngine.Random.Range(0f, 6f); // two mimics in one place don't always pick the same person
                if (score > bestScore) { bestScore = score; best = a.SteamID; }
            }
            return best;
        }

        /// <summary>A voice a mimic can speak with: that player's recordings can be played (they're still in the game) and
        /// there are enough of them (the game's own minimum to mimic someone is a few warmed-up lines).</summary>
        static bool Usable(VoiceManager vm, List<SpeechEventArchive> all, ulong id)
        {
            if (id == 0 || all == null) return false;
            foreach (var a in all) if (a != null && a.SteamID == id && Usable(vm, a)) return true;
            return false;
        }

        static bool Usable(VoiceManager vm, SpeechEventArchive a)
        {
            try { return a.SteamID != 0 && vm.IsArchivePickable(a) && a.WarmedUpCount >= MinLines; }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------------------------------------- turn taking

        /// <summary>V4: someone near the mimic is talking right now (a mimic waits for a pause, like people).</summary>
        internal static bool SomeoneTalkingNear(int mimicActorId)
        {
            var main = Hub.Main;
            var actor = main != null ? main.GetActorByActorID(mimicActorId) : null;
            if (actor == null) return false;
            Vector3 pos = actor.transform.position;
            foreach (var p in ServerPlayers().Values)
            {
                if (!p.IsAliveStatus() || Vector3.Distance(p.PositionVector, pos) > TalkingNearM) continue;
                try
                {
                    if (!string.IsNullOrEmpty(p._voiceUID) && Hub.s.voiceman.TryGetVoiceAmplitude(p._voiceUID, out _, out bool speaking) && speaking)
                        return true;
                }
                catch (Exception) { /* not talking */ }
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------- helpers

        /// <summary>The players of the host's current level, by Steam id.</summary>
        static Dictionary<ulong, VPlayer> ServerPlayers()
        {
            var map = new Dictionary<ulong, VPlayer>();
            var rooms = Hub.s?.vworld?.VRoomManager?._vrooms;
            if (rooms == null) return map;
            foreach (var r in rooms.Values)
            {
                if (!(r is DungeonRoom)) continue;
                r.IterateAllPlayer(p =>
                {
                    if (p != null && p.SteamID != 0 && !map.ContainsKey(p.SteamID)) map[p.SteamID] = p;
                });
            }
            return map;
        }

        static string NameOf(Dictionary<ulong, VPlayer> players, ulong id) =>
            players.TryGetValue(id, out var p) ? p.ActorName : id.ToString();
    }
}
