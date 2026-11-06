// MIMESIS Poltergeist - when something happens, and what
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every frame on this player's game. In a level, alive, while it's being played (not loading, not on the results
// screen): after a quiet first stretch from the start of play, one haunting
// at a random time around Settings.MeanIntervalSec apart, picked from what's on and what fits right now:
//   Whisper   the game's own radiation whisper: a teammate's recorded voice, whispered 2-5 m from you
//   Ambient   the game's own radiation ambience: an eerie sound around you
//   Flicker   the lights around you flicker (Flicker.cs)
//   Stalker / Runner / Behind   a figure only you see (Apparition.cs)
// Never while a monster is close (a real threat must never look like a haunting, and the other way round), never two at
// once, and nothing during a deathmatch (the Deathmatch patch asks for quiet: a fight between players stays fair). Nothing is sent to the host or the other players: it's all on your screen and in your ears, so it works in any
// game, modded host or not.

using System;
using System.Collections.Generic;
using Mimic.Actors;
using ReluReplay.Shared;
using UnityEngine;

namespace Poltergeist
{
    static class Haunt
    {
        internal enum Kind { Whisper, Ambient, Flicker, Stalker, Runner, Behind }

        const float FirstQuietSec = 40f;
        const float MonsterNearM = 15f;

        static GamePlayScene _scene;
        static float _nextAt;
        static readonly System.Random _rng = new System.Random();

        /// <summary>Another patch asks for quiet (AppDomain data "MimesisPatches.NoHauntings" = true): the Deathmatch
        /// patch while a deathmatch is on, so a fight between players stays fair (no figure to shoot at, no lights going
        /// out for one player only).</summary>
        internal static bool Paused => AppDomain.CurrentDomain.GetData("MimesisPatches.NoHauntings") is bool b && b;

        /// <summary>What happened last (for the page and tests).</summary>
        internal static string LastEvent = "";
        internal static int Count;

        internal static void Tick()
        {
            // Off: nothing at all, unless Debug mode is on, for what the debug window starts (nothing on its own, below).
            if ((!Settings.Enabled && !Settings.Debug) || ReplaySharedData.IsReplayPlayMode)
            {
                Flicker.Restore();
                return;
            }
            var scene = Hub.Main as GamePlayScene;
            if (scene == null)
            {
                if (!ReferenceEquals(_scene, null)) LeaveLevel();
                return;
            }
            if (!ReferenceEquals(scene, _scene))
            {
                LeaveLevel();
                _scene = scene;
            }
            // Not playing yet (loading, the arrival) or any more (the results screen, the exit), or another patch asks for
            // quiet: nothing (what's on now ends), and the quiet first stretch starts over.
            if (!scene.IsGameLoopRunning || Paused)
            {
                Flicker.Restore();
                if (Paused)
                {
                    try { Apparition.Active?.Vanish(quiet: true); } catch (Exception) { /* gone already */ }
                }
                _nextAt = Time.time + FirstQuietSec + (float)_rng.NextDouble() * 25f;
                return;
            }
            Flicker.Tick();
            if (Apparition.Active != null || Flicker.Running) return;
            if (!Settings.Enabled)
            {
                _nextAt = Time.time + FirstQuietSec; // off, Debug mode on: only what the window starts
                return;
            }
            if (Time.time < _nextAt) return;
            var avatar = scene.GetMyAvatar();
            if (avatar == null || avatar.dead) return;
            float mean = Settings.MeanIntervalSec;
            _nextAt = Time.time + mean * (0.6f + (float)_rng.NextDouble() * 0.8f);
            if (MonsterNear(scene, avatar.transform.position))
            {
                _nextAt = Time.time + 8f; // later, once it's quiet
                return;
            }
            var order = Candidates(scene);
            foreach (var k in order)
                if (Fire(k, scene, avatar)) return;
            if (Settings.VerboseLog) Log.Info("Nothing fitted this time");
        }

        static void LeaveLevel()
        {
            Flicker.Restore();
            try { Apparition.Active?.Vanish(quiet: true); } catch (Exception) { /* gone with the level */ }
            _scene = null;
        }

        /// <summary>Seconds until the next haunting on its own (0 = due, once it fits). Meaningless while the level isn't
        /// being played or a deathmatch asks for quiet (it starts over every frame then).</summary>
        internal static float NextInSec => Mathf.Max(0f, _nextAt - Time.time);

        /// <summary>Whatever is on now ends: the figure goes, the lights come back (the debug window, before the next one).</summary>
        internal static void StopAll()
        {
            try { Apparition.Active?.Vanish(quiet: true); } catch (Exception) { /* gone already */ }
            Flicker.Restore();
        }

        /// <summary>The kinds that are on, in a random order weighted toward the quieter ones.</summary>
        internal static List<Kind> Candidates(GamePlayScene scene)
        {
            var bag = new List<(Kind k, int w)>();
            if (Settings.Sounds) { bag.Add((Kind.Whisper, 22)); bag.Add((Kind.Ambient, 18)); }
            if (Settings.Flicker && !scene.IsBlackout) bag.Add((Kind.Flicker, 18));
            if (Settings.Apparitions) { bag.Add((Kind.Stalker, 14)); bag.Add((Kind.Runner, 10)); }
            if (Settings.Jumpscares) bag.Add((Kind.Behind, 12));
            var order = new List<Kind>();
            while (bag.Count > 0)
            {
                int total = 0;
                foreach (var x in bag) total += x.w;
                int r = _rng.Next(total);
                for (int i = 0; i < bag.Count; i++)
                {
                    if (r < bag[i].w)
                    {
                        order.Add(bag[i].k);
                        bag.RemoveAt(i);
                        break;
                    }
                    r -= bag[i].w;
                }
            }
            return order;
        }

        /// <summary>Starts one haunting now (also the test entry point). False when it doesn't fit here.</summary>
        internal static bool Fire(Kind kind, GamePlayScene scene, ProtoActor avatar) => Fire(kind, scene, avatar, out _);

        /// <summary>Fire, with why it didn't fit (the debug window shows it).</summary>
        internal static bool Fire(Kind kind, GamePlayScene scene, ProtoActor avatar, out string why)
        {
            bool ok = false;
            string detail = "";
            if (Paused)
            {
                why = "a deathmatch is on: no hauntings there";
                if (Settings.VerboseLog) Log.Info($"{kind}: not now (another patch asked for quiet: a deathmatch)");
                return false;
            }
            try
            {
                switch (kind)
                {
                    case Kind.Whisper:
                    case Kind.Ambient:
                        ok = PlaySymptom(avatar, kind == Kind.Whisper, out detail);
                        break;
                    case Kind.Flicker:
                        ok = Flicker.Start(avatar.transform.position);
                        if (!ok) detail = "no lights near";
                        break;
                    case Kind.Stalker:
                        ok = TryStalker(scene, avatar);
                        break;
                    case Kind.Runner:
                        ok = TryRunner(scene, avatar);
                        break;
                    case Kind.Behind:
                        ok = TryBehind(scene, avatar);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("fire-" + kind, $"{kind} failed: " + e);
                ok = false;
                detail = "it failed: " + e.Message;
            }
            if (ok)
            {
                Count++;
                LastEvent = kind.ToString();
                if (Settings.VerboseLog) Log.Info($"Haunting: {kind}");
            }
            else
            {
                if (detail.Length == 0)
                    detail = kind == Kind.Behind ? "no open floor right behind you" : kind == Kind.Stalker || kind == Kind.Runner ? "no open floor in view" : "not here";
                if (Settings.VerboseLog) Log.Info($"{kind} didn't fit ({detail})");
            }
            why = detail;
            return ok;
        }

        /// <summary>The game's own radiation whisper (a teammate's recorded voice, 2-5 m from you) or ambience, played as
        /// its ProtoActor.TryStartScheduledParanoiaSymptom does, minus its last step: that one also tells the host (for
        /// the host's replay). Here it stays on this game.</summary>
        static bool PlaySymptom(ProtoActor avatar, bool whisper, out string why)
        {
            why = "";
            if (!avatar._isLiveParanoiaPresentationAllowed) { why = "not now"; return false; }
            if (whisper)
            {
                var vm = Hub.s?.voiceman;
                var preset = Hub.s?.tableman?.voiceEffect?.GetPresetByName("VoiceWhisper");
                if (vm == null || preset == null) { why = "no whisper voice effect"; return false; }
                if (!vm.HasHalucinationVoice()) { why = "no recorded voices yet"; return false; }
                Vector3 at = Misc.GetRandomPositionInRange(avatar.transform.position, 2.0, 5.0);
                if (!vm.TryPrepareParanoiaHalucinationAtPoint(at, preset, out var playback) || playback == null) { why = "nothing to whisper"; return false; }
                if (!playback.TryPlay()) { why = "it didn't play"; return false; }
                return true;
            }
            var main = Hub.s?.pdata?.main;
            if (main == null || !main.TryPrepareParanoiaSfx(out string sfx, out Vector3 pos)) { why = "no sound here"; return false; }
            if (!main.TryPlayParanoiaSfx(sfx, pos)) { why = "it didn't play"; return false; }
            return true;
        }

        /// <summary>Test entry point: by name, in the current level.</summary>
        internal static bool Trigger(string kind)
        {
            var scene = Hub.Main as GamePlayScene;
            var avatar = scene != null ? scene.GetMyAvatar() : null;
            if (avatar == null || !Enum.TryParse(kind, true, out Kind k)) return false;
            return Fire(k, scene, avatar);
        }

        // ---------------------------------------------------------------------------------------------- placing figures

        static bool TryStalker(GamePlayScene scene, ProtoActor avatar)
        {
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 fwd = Flat(cam.transform.forward);
            for (int i = 0; i < 24; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, Rand(-32f, 32f), 0f) * fwd;
                if (!TrySpot(scene, avatar, cam.transform.position + dir * Rand(12f, 22f), cam.transform.position, out Vector3 spot)) continue;
                var a = Apparition.Create(scene, Apparition.Kind.Stalker, spot, YawTo(spot, cam.transform.position), Rand(8f, 15f));
                return a != null;
            }
            return false;
        }

        static bool TryRunner(GamePlayScene scene, ProtoActor avatar)
        {
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 fwd = Flat(cam.transform.forward);
            for (int i = 0; i < 24; i++)
            {
                float side = _rng.NextDouble() < 0.5 ? -1f : 1f;
                Vector3 dir = Quaternion.Euler(0f, side * Rand(35f, 60f), 0f) * fwd;
                Vector3 from = cam.transform.position + dir * Rand(9f, 16f);
                if (!TrySpot(scene, avatar, from, cam.transform.position, out Vector3 spot)) continue;
                // Across your view, away from the center.
                Vector3 run = Vector3.Cross(Vector3.up, Flat(spot - cam.transform.position)).normalized * side;
                float runSec = Rand(0.9f, 1.4f);
                Vector3 end = spot + run * (7f * runSec);
                if (!OnNav(end, out _) || !PhysicsUtility.CheckBlockByWall(spot + Vector3.up, end + Vector3.up)) continue;
                var a = Apparition.Create(scene, Apparition.Kind.Runner, spot, YawTo(spot, end), runSec, run);
                return a != null;
            }
            return false;
        }

        static bool TryBehind(GamePlayScene scene, ProtoActor avatar)
        {
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 back = -Flat(cam.transform.forward);
            Vector3 me = avatar.transform.position;
            for (int i = 0; i < 24; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, Rand(-30f, 30f), 0f) * back;
                Vector3 q = me + dir * Rand(2.2f, 3.0f);
                if (!OnNav(q, out Vector3 n) || Mathf.Abs(n.y - me.y) > 1f) continue;
                if (!PhysicsUtility.CheckBlockByWall(me + Vector3.up * 1.5f, n + Vector3.up * 1.5f)) continue;
                if (PlayerNear(scene, n, 1.5f)) continue;
                var a = Apparition.Create(scene, Apparition.Kind.Behind, n, YawTo(n, me), Rand(8f, 12f));
                return a != null;
            }
            return false;
        }

        /// <summary>A navmesh spot near q you can see from eye, not on top of a real player.</summary>
        static bool TrySpot(GamePlayScene scene, ProtoActor avatar, Vector3 q, Vector3 eye, out Vector3 spot)
        {
            spot = default;
            if (!OnNav(q, out Vector3 n)) return false;
            if (Mathf.Abs(n.y - avatar.transform.position.y) > 4f) return false;
            if (!PhysicsUtility.CheckBlockByWall(eye, n + Vector3.up * 1.5f)) return false;
            if (PlayerNear(scene, n, 3f)) return false;
            spot = n;
            return true;
        }

        static bool OnNav(Vector3 q, out Vector3 n)
        {
            n = default;
            var nav = Hub.s?.navman;
            if (nav == null) return false;
            try { n = nav.GetNearestPointOnNavMesh(q, 2f); }
            catch (Exception) { return false; }
            return NavMeshConstants.IsValid(n);
        }

        internal static bool PlayerNear(GamePlayScene scene, Vector3 pos, float range)
        {
            foreach (var a in scene.GetProtoActorMap().Values)
                if (a != null && a.ActorType == ReluProtocol.Enum.ActorType.Player && !a.AmIAvatar() && Vector3.Distance(a.transform.position, pos) < range)
                    return true;
            return false;
        }

        internal static bool MonsterNear(GamePlayScene scene, Vector3 pos)
        {
            foreach (var a in scene.GetProtoActorMap().Values)
                if (a != null && !a.dead && a.ActorType == ReluProtocol.Enum.ActorType.Monster && Vector3.Distance(a.transform.position, pos) < MonsterNearM)
                    return true;
            return false;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
        }

        static float YawTo(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            d.y = 0f;
            return d.sqrMagnitude < 0.0001f ? 0f : Quaternion.LookRotation(d).eulerAngles.y;
        }

        static float Rand(float min, float max) => (float)(min + _rng.NextDouble() * (max - min));
    }

    sealed class HauntDriver : MonoBehaviour
    {
        void Update()
        {
            try { Haunt.Tick(); }
            catch (Exception e) { Log.ErrorOnce("tick", "Haunting update failed: " + e); }
        }
    }
}
