// MIMESIS Hallucinations - living player: shows the hallucinations sent to them and reports how they end
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Everything here is local to this game: nothing is sent but the End report, no game state changes, nobody else sees
// anything. At most one hallucination at a time (the host checks too). Its end is reported exactly once, whatever ends
// it (FakeMimic calls back here). The player's own attacks are real: the server handles them as usual; this game only
// checks, locally, whether one of them would have reached the fake, and makes it vanish.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Bifrost.Cooked;
using HarmonyLib;
using Mimic.Actors;
using Mimic.Animation;
using ReluProtocol;
using UnityEngine;

namespace Hallucinations
{
    static class Victim
    {
        /// <summary>A player attack that might reach the fake, checked at the skill's hit time.</summary>
        struct HitCheck
        {
            public float Due;           // Time.time
            public float MaxRange;
            public bool Ranged;
            public int SkillId;
        }

        static FakeMimic _active;
        static int _activeId;
        static readonly HashSet<int> _reported = new HashSet<int>();
        static readonly HashSet<PuppetScript> _fakes = new HashSet<PuppetScript>();
        static readonly List<HitCheck> _checks = new List<HitCheck>();

        /// <summary>Every frame (ClientDriver): pending hit checks of the player's own attacks; drop a fake destroyed behind
        /// our back (level unload).</summary>
        internal static void Tick()
        {
            if (ReferenceEquals(_active, null)) return;
            if (_active == null)
            {
                // Destroyed without going through Vanish (its level's BG root went away): report it once.
                End(_activeId, Outcome.Cancelled);
                _active = null;
                _activeId = 0;
                _checks.Clear();
                return;
            }
            SendPose();
            if (_checks.Count == 0) return;
            float now = Time.time;
            for (int i = _checks.Count - 1; i >= 0; i--)
            {
                var c = _checks[i];
                if (now < c.Due) continue;
                _checks.RemoveAt(i);
                if (_active == null || _active.IsVanishing) continue;
                if (Reaches(_active, c))
                {
                    Log.Info($"Hallucination #{_activeId}: your attack (skill {c.SkillId}) hit it");
                    _active.OnHitByPlayer();
                    _checks.Clear();
                    return;
                }
            }
        }

        /// <summary>The host says: show hallucination m.Id now.</summary>
        internal static void OnSpawn(in HostMsg m)
        {
            if (m.Id == 0) return;
            if (!ReferenceEquals(_active, null) && _active == null)
            {
                // Destroyed behind our back this very frame (Tick hasn't seen it yet).
                End(_activeId, Outcome.Cancelled);
                _active = null;
                _activeId = 0;
            }
            if (_reported.Contains(m.Id) || (_active != null && _activeId == m.Id))
            {
                Log.Trace($"Hallucination #{m.Id} again: ignored");
                return;
            }
            var scene = Hub.Main as GamePlayScene;
            var avatar = scene != null ? scene.GetMyAvatar() : null;
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            // Not while the level is over (exit cutscene, results), dead, spectating or watching a replay.
            if (scene == null || !scene.IsGameLogicRunning || avatar == null || avatar.dead || (cm != null && cm.IsSpectatorMode) ||
                Control.IsReplay)
            {
                End(m.Id, Outcome.Cancelled);
                return;
            }
            if (_active != null)
            {
                End(m.Id, Outcome.Busy);
                return;
            }

            Vector3 pos;
            float yaw;
            string where;
            bool navOk;
            try
            {
                if (!Placement.TryFind(scene, avatar, m.Seed, out pos, out yaw, out where, out navOk))
                {
                    Log.Info($"Hallucination #{m.Id}: no place out of your view for it");
                    End(m.Id, Outcome.NoSpot);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("placement", "Finding a place for a hallucination failed: " + e);
                End(m.Id, Outcome.Error);
                return;
            }

            // Steered by the dead player who sent it (m.Ctl), or on its own (a debug self-spawn).
            var mode = m.Ctl != 0 ? FakeMimic.Mode.Remote : FakeMimic.Mode.Ai;
            var fake = FakeMimic.Create(scene, m.Id, m.Weapon, m.Seed, m.TtlMs, pos, yaw, navOk, mode, OnFakeEnded,
                onAttack: f => ClientNet.Send(Protocol.Attack(f.Id, f.transform.eulerAngles.y)),
                onTakeOver: f => ClientNet.Send(Protocol.Release(f.Id)));
            if (fake == null)
            {
                End(m.Id, Outcome.Error);
                return;
            }
            _active = fake;
            _activeId = m.Id;
            _checks.Clear();
            _seq = 0;
            _nextPoseAt = 0f;
            // Where it appeared: the host shows it to the dead players from here (and its dead player steers it from here).
            ClientNet.Send(Protocol.Placed(m.Id, pos, yaw));
            float dist = Vector3.Distance(pos, avatar.transform.position);
            Log.Info($"Hallucination #{m.Id} ({fake.Description}{(m.Debug ? ", debug" : "")}{(m.Ctl != 0 ? $", steered by {Name(m.Ctl)}" : "")}) appears {dist:0.0} m away ({where})");
        }

        static int _seq;
        static float _nextPoseAt;

        /// <summary>While it runs on its own (Ai), this game moves it: its poses go to the host for the dead players who
        /// see it.</summary>
        static void SendPose()
        {
            if (_active == null || _active.IsVanishing || _active.CurrentMode != FakeMimic.Mode.Ai) return;
            float now = Time.unscaledTime;
            if (now < _nextPoseAt) return;
            var pose = _active.CurrentPose;
            _nextPoseAt = now + (pose.Move == MoveKind.Idle ? Const.IdleSendInterval : Const.MoveSendInterval);
            ClientNet.Send(Protocol.Drive(_activeId, pose, ++_seq));
        }

        /// <summary>The host forwards a pose from the dead player who steers our hallucination.</summary>
        internal static bool OnDrive(in HostMsg m)
        {
            if (_active == null || m.Id != _activeId) return false;
            if (_active.CurrentMode == FakeMimic.Mode.Remote) _active.ApplyRemote(m.Pose);
            return true;
        }

        /// <summary>The dead player who steers it attacks: it plays here, and this game decides whether it lands.</summary>
        internal static bool OnAttack(in HostMsg m)
        {
            if (_active == null || m.Id != _activeId) return false;
            if (_active.CurrentMode == FakeMimic.Mode.Remote) _active.AttackNow(m.Pose.Yaw);
            return true;
        }

        /// <summary>The dead player let go (or left): it runs on its own from now.</summary>
        internal static bool OnRelease(in HostMsg m)
        {
            if (_active == null || m.Id != _activeId) return false;
            if (_active.CurrentMode == FakeMimic.Mode.Remote)
            {
                _active.SetMode(FakeMimic.Mode.Ai);
                Log.Info($"Hallucination #{m.Id}: its dead player let go; it goes on its own");
            }
            return true;
        }

        static string Name(int actorId)
        {
            try
            {
                var main = Hub.Main;
                var a = main != null ? main.GetActorByActorID(actorId) : null;
                if (a != null) return a.nickName;
            }
            catch (Exception) { /* name only */ }
            return "#" + actorId;
        }

        /// <summary>FakeMimic calls this exactly once when it ends (any reason).</summary>
        internal static void OnFakeEnded(FakeMimic fake, Outcome outcome)
        {
            if (ReferenceEquals(fake, null)) return;
            End(fake.Id, outcome);
            if (ReferenceEquals(_active, fake))
            {
                _active = null;
                _activeId = 0;
                _checks.Clear();
            }
        }

        /// <summary>C5: our own avatar's attack was confirmed by the server (its UseSkillSig): check, at the skill's hit
        /// time, whether it would have reached the fake.</summary>
        internal static void OnAvatarSkill(ProtoActor avatar, int skillMasterId)
        {
            if (_active == null || _active.IsVanishing || avatar == null) return;
            var data = Hub.s != null && Hub.s.dataman != null ? Hub.s.dataman.ExcelDataManager : null;
            var info = data != null ? data.GetSkillInfo(skillMasterId) : null;
            if (info == null || info.MaxRange <= 0f) return; // not an attack

            float delay = 0.25f;
            try
            {
                var player = data.GetPlayerInfo(avatar.playerMasterID);
                string fpp = player != null ? player.FppPuppetName : null;
                var anim = !string.IsNullOrEmpty(fpp) && !string.IsNullOrEmpty(info.SkillAnimationState)
                    ? Hub.s.dataman.AnimNotiManager.GetSkillAnimInfo(fpp, info.SkillAnimationState) : null;
                if (anim != null)
                {
                    foreach (var hit in anim.GetAllData()) { delay = (float)hit.Key; break; } // sorted: the first hit check
                }
            }
            catch (Exception e) { Log.WarnOnce("skill-anim", "Couldn't read your attack's timing (using 0.25 s): " + e.Message); }
            bool ranged = ProjectileSlots(info) > 0;
            if (ranged) delay += 0.25f; // a thrown thing flies a moment
            _checks.Add(new HitCheck
            {
                Due = Time.time + Mathf.Clamp(delay, 0f, 2f),
                MaxRange = info.MaxRange,
                Ranged = ranged,
                SkillId = skillMasterId,
            });
            Log.Trace($"Your attack (skill {skillMasterId}{(ranged ? ", ranged" : "")}): checking it against the hallucination in {delay:0.00} s");
        }

        /// <summary>C6: a field skill appeared in the world. An explosion within 4 m of the fake, or a short impact (paint
        /// ball, spray, lightning) within 1.5 m, ends it. Lasting fields (gas, sprinklers, level hazards that show up when
        /// they come into view) don't.</summary>
        internal static void OnFieldSkill(FieldSkillObjectInfo info)
        {
            if (_active == null || _active.IsVanishing || info == null || info.position == null) return;
            Vector3 at = info.position.pos;
            if ((_active.Chest - at).sqrMagnitude > 25f) return; // cheap first: nothing counts beyond 5 m
            float reach = FieldSkillReach(info.fieldSkillMasterID, info.fieldSkillIndex, out string prefab);
            if (reach <= 0f || (_active.Chest - at).sqrMagnitude > reach * reach) return;
            Log.Info($"Hallucination #{_activeId}: caught by {(string.IsNullOrEmpty(prefab) ? "a field skill" : prefab)}");
            _active.OnHitByPlayer();
        }

        static FieldInfo _fieldMembers;
        static bool _fieldMembersLooked;

        /// <summary>How close a field skill must be to end the fake (0 = never). FieldSkillInfo.FieldSkillMemberInfos is an
        /// ImmutableDictionary: read through the non-generic IDictionary (no System.Collections.Immutable reference).</summary>
        static float FieldSkillReach(int masterId, int index, out string prefab)
        {
            prefab = null;
            try
            {
                var data = Hub.s.dataman.ExcelDataManager.GetFieldSkillData(masterId);
                if (data == null) return 0f;
                if (!_fieldMembersLooked)
                {
                    _fieldMembersLooked = true;
                    _fieldMembers = typeof(FieldSkillInfo).GetField("FieldSkillMemberInfos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (!(_fieldMembers?.GetValue(data) is IDictionary members) || !members.Contains(index)) return 0f;
                if (!(members[index] is FieldSkillMemberInfo member)) return 0f;
                prefab = member.PrefabName;
                if (!string.IsNullOrEmpty(prefab) && prefab.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0) return 4f;
                if (!string.IsNullOrEmpty(prefab) && member.Duration > 0 && member.Duration <= 1000) return 1.5f;
                return 0f;
            }
            catch (Exception e)
            {
                Log.WarnOnce("field-skill", "Couldn't read a field skill's data: " + e.Message);
                return 0f;
            }
        }

        /// <summary>Is this puppet one of our fakes (FakeMimic's animation-event fixups)? Registered until its GameObject is
        /// destroyed: despawn clip events still arrive after it vanished.</summary>
        internal static bool IsFake(PuppetScript puppet) => _fakes.Count != 0 && puppet != null && _fakes.Contains(puppet);

        internal static void RegisterPuppet(PuppetScript puppet)
        {
            if (!ReferenceEquals(puppet, null)) _fakes.Add(puppet);
        }

        internal static void UnregisterPuppet(PuppetScript puppet)
        {
            if (ReferenceEquals(puppet, null)) return;
            _fakes.Remove(puppet);
            // Destroyed objects compare equal to null in Unity: also sweep any that the set still holds.
            if (_fakes.Count > 0) _fakes.RemoveWhere(p => p == null);
        }

        /// <summary>A new level (or left one): the fake goes at once (reported as cut short), checks are dropped.</summary>
        internal static void Reset(bool cancelled)
        {
            var fake = _active;
            int id = _activeId;
            _active = null;
            _activeId = 0;
            _checks.Clear();
            if (fake != null) fake.Cancel(cancelled ? "level left" : "reset");
            else if (id != 0) End(id, Outcome.Cancelled);
            _reported.Clear();
            if (_fakes.Count > 0) _fakes.RemoveWhere(p => p == null);
        }

        /// <summary>Tells the host how hallucination id ended, once per id.</summary>
        static void End(int id, Outcome outcome)
        {
            if (id == 0 || !_reported.Add(id)) return;
            Report(id, outcome);
        }

        /// <summary>Tells the host how hallucination id ended (once per id: the caller makes sure).</summary>
        internal static void Report(int id, Outcome outcome)
        {
            if (id == 0) return;
            if (ClientNet.Send(Protocol.End(id, outcome))) Log.Info($"Hallucination #{id} ended: {outcome}");
        }

        // ---------------------------------------------------------------- the player's attack vs the fake (local only)

        static bool Reaches(FakeMimic fake, in HitCheck c)
        {
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
            Vector3 chest = fake.Chest;
            Vector3 to = chest - eye;
            if (c.Ranged)
            {
                float along = Vector3.Dot(to, fwd);
                if (along < 0f || along > 30f) return false;
                if ((to - fwd * along).magnitude > 0.7f) return false;
            }
            else
            {
                float reach = c.MaxRange + 0.6f;
                if (to.magnitude > reach) return false;
                if (Vector3.Angle(fwd, to) > (c.MaxRange > 5f ? 25f : 50f)) return false;
            }
            return PhysicsUtility.CheckBlockByWall(eye, chest) || PhysicsUtility.CheckBlockByWall(eye, fake.Head);
        }

        static FieldInfo _projectileSlots;
        static bool _projectileSlotsLooked;

        /// <summary>SkillInfo.SkillProjectileSlotDictionary.Count, read without referencing System.Collections.Immutable
        /// (ImmutableDictionary implements the non-generic ICollection).</summary>
        static int ProjectileSlots(SkillInfo info)
        {
            try
            {
                if (!_projectileSlotsLooked)
                {
                    _projectileSlotsLooked = true;
                    _projectileSlots = typeof(SkillInfo).GetField("SkillProjectileSlotDictionary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return _projectileSlots != null && _projectileSlots.GetValue(info) is ICollection col ? col.Count : 0;
            }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>C5: the player's own attacks, to let them hit the fake. Not core: without it the fake still vanishes when
    /// its attack lands or its lifetime runs out.</summary>
    [HarmonyPatch]
    static class VictimAttackPatch
    {
        // C5. ProtoActor's [PacketHandler] for UseSkillSig (private; called by reflection for every actor's confirmed
        // skill, ours included: the avatar's own puppet animation starts from it). Only our own avatar matters here.
        [HarmonyPostfix, HarmonyPatch(typeof(ProtoActor), nameof(ProtoActor.ResolvePacket_UseSkillSig))]
        static void AfterUseSkillSig(ProtoActor __instance, UseSkillSig sig)
        {
            if (!Control.ClientReady || sig == null) return;
            try
            {
                if (__instance != null && __instance.AmIAvatar()) Victim.OnAvatarSkill(__instance, sig.skillMasterID);
            }
            catch (Exception e) { Log.ErrorOnce("C5", "Checking your attack against the hallucination failed: " + e); }
        }
    }

    /// <summary>C6: blasts near the fake (the player's real bomb, a mimic's...). Not core: without it only direct hits,
    /// touching it, its own attack and its lifetime end it.</summary>
    [HarmonyPatch]
    static class VictimFieldSkillPatch
    {
        // C6. Every field skill the server spawns in this game's view (FieldSkillActor), with its world position.
        [HarmonyPostfix, HarmonyPatch(typeof(GameMainBase), nameof(GameMainBase.SpawnFieldSkill))]
        static void AfterSpawnFieldSkill(FieldSkillObjectInfo fieldSkillObjectInfo)
        {
            if (!Control.ClientReady || fieldSkillObjectInfo == null) return;
            try { Victim.OnFieldSkill(fieldSkillObjectInfo); }
            catch (Exception e) { Log.ErrorOnce("C6", "Checking a blast against the hallucination failed: " + e); }
        }
    }
}
