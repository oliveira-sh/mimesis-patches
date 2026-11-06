// MIMESIS Poltergeist - the figures only you see
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// A bare puppet: the game's own prefab_IllusionMimicPuppet (the body players and mimics share; the vanilla ghost mimic's
// puppet), under the level's BG root, with NO ProtoActor: no game system sees it (server, other players, crosshair,
// interaction), so it can't hurt, block or be hit. It vanishes with the ghost mimic's own despawn (dissolve +
// FX_Mimic_Idle_Despawn). Three kinds:
//   Stalker  stands far away in view, staring at you; gone the moment you look straight at it or walk up to it.
//   Runner   runs across the edge of your view and is gone.
//   Behind   waits right behind you (and keeps up while you don't see it); when you turn around it lunges (the mimic's
//            grab) with the game's own local hit feedback (sound, screen shake), and is gone. No damage, nothing sent
//            anywhere.
// Any of them goes the moment a real monster comes close, and when the level ends.

using System;
using System.Collections.Generic;
using Bifrost.ConstEnum;
using HarmonyLib;
using Mimic.Actors;
using Mimic.Animation;
using ReluProtocol.Enum;
using Unity.Cinemachine;
using UnityEngine;

namespace Poltergeist
{
    sealed class Apparition : MonoBehaviour
    {
        const string PuppetId = "prefab_IllusionMimicPuppet";
        const string FallbackPuppetId = "prefab_MimicPuppet";
        const int ActorTypeIllusion = 10;          // the illusion mimic's Lifetime states (its despawn dissolve)
        const string DespawnVfx = "FX_Mimic_Idle_Despawn";
        const int GrabSkill = 7900;                // "Skill.FakeGrapple": the mimic's strangle lunge
        const int HitEffect = 40002;               // SkillTargetEffectData: a grab's hit feedback on you
        const float WalkSpeed = 3.5f, RunSpeed = 7f;

        internal enum Kind { Stalker, Runner, Behind }

        static readonly int ActorTypeParam = Animator.StringToHash("ActorType");
        static readonly HashSet<PuppetScript> _ours = new HashSet<PuppetScript>();

        internal static Apparition Active { get; private set; }
        internal static bool IsOurs(PuppetScript p) => _ours.Count != 0 && p != null && _ours.Contains(p);

        PuppetScript _puppet;
        Kind _kind;
        GamePlayScene _main;
        float _born, _until, _lookedAt;
        Vector3 _runDir;
        bool _lunging, _hitDone, _vanishing, _hidden;
        float _lungeAt, _vanishAt, _nextMonsterCheck;
        readonly List<Material> _materials = new List<Material>();

        // ================================================================================================ creation

        internal static Apparition Create(GamePlayScene main, Kind kind, Vector3 pos, float yaw, float lifetime, Vector3 runDir = default)
        {
            GameObject root = null;
            try
            {
                var table = Hub.s?.tableman?.monsterPuppet;
                var row = table?.FindRow(PuppetId);
                if (row == null || row.prefab == null) row = table?.FindRow(FallbackPuppetId);
                if (row == null || row.prefab == null)
                {
                    Log.WarnOnce("no-puppet", "No mimic puppet in this game version: no apparitions");
                    return null;
                }
                root = new GameObject("PoltergeistApparition");
                root.SetActive(false);
                var bg = main.GetBGRoot();
                if (bg != null) root.transform.SetParent(bg, false); // gone with the level, whatever else happens
                Vector3 floor = PhysicsUtility.DropToFloor(pos, 0.5f, 1.5f);
                if (floor == pos) floor = PhysicsUtility.DropToFloor(pos, 1f, 1.5f);
                if (floor != pos) pos.y = floor.y;
                root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
                var go = Instantiate(row.prefab, root.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                var puppet = go.GetComponent<PuppetScript>();
                var anim = go.GetComponent<Animator>();
                if (puppet == null || anim == null)
                {
                    Log.WarnOnce("puppet-parts", "The mimic puppet has no PuppetScript/Animator (game update?): no apparitions");
                    Destroy(root);
                    return null;
                }
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }
                foreach (var cam in go.GetComponentsInChildren<CinemachineVirtualCameraBase>(true)) cam.enabled = false;
                // The bone "Head" has an AudioSource but no socket marker: the grab's choke ("sound:x@Head") needs one.
                var headBone = FindDeep(go.transform, "Head");
                if (headBone != null && !headBone.TryGetComponent<SocketNodeMarker>(out _)) headBone.gameObject.AddComponent<SocketNodeMarker>();

                var a = root.AddComponent<Apparition>();
                a._puppet = puppet;
                a._kind = kind;
                a._main = main;
                a._born = Time.time;
                a._until = Time.time + lifetime;
                a._runDir = runDir;
                _ours.Add(puppet);
                root.SetActive(true);
                if (SocketAttachable.HasParameter(anim, ActorTypeParam)) anim.SetInteger(ActorTypeParam, ActorTypeIllusion);
                puppet.HoldByHand(PuppetHandheldState.Empty);
                puppet.Move(Vector3.zero, WalkSpeed, RunSpeed);
                puppet.UpdateMoveType(ActorMoveType.Walk);
                Active = a;
                return a;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("create", "Building an apparition failed: " + e);
                if (root != null) Destroy(root);
                return null;
            }
        }

        // ================================================================================================ life

        void Update()
        {
            try
            {
                if (_vanishing) { VanishUpdate(); return; }
                var avatar = _main != null ? _main.GetMyAvatar() : null;
                var cam = Camera.main;
                // Gone with the level's end too (results screen, exit cutscene): the game's own scares stop there.
                if (avatar == null || avatar.dead || cam == null || !(Settings.Enabled || Settings.Debug) || !ReferenceEquals(Hub.Main, _main) || !_main.IsGameLoopRunning)
                {
                    Vanish(quiet: true);
                    return;
                }
                Vector3 eye = cam.transform.position;
                Vector3 head = transform.position + Vector3.up * 1.6f;
                Vector3 toMe = head - eye;
                float dist = toMe.magnitude;
                float angle = Vector3.Angle(cam.transform.forward, toMe);
                bool visible = PhysicsUtility.CheckBlockByWall(eye, head);

                // A real monster comes close: the figure goes (a real threat must never look like a haunting).
                if (Time.time >= _nextMonsterCheck)
                {
                    _nextMonsterCheck = Time.time + 0.25f;
                    if (Haunt.MonsterNear(_main, avatar.transform.position))
                    {
                        Trace($"{_kind}: a monster is near, gone");
                        Vanish(quiet: !(visible && angle < 70f));
                        return;
                    }
                }

                switch (_kind)
                {
                    case Kind.Stalker:
                        FaceFlat(eye);
                        if (visible && angle < 7f)
                        {
                            if (_lookedAt == 0f) _lookedAt = Time.time;
                            if (Time.time - _lookedAt > 0.25f) { Trace("stalker: looked at, gone"); Vanish(); return; }
                        }
                        else _lookedAt = 0f;
                        if (dist < 8f) { Trace("stalker: walked up to, gone"); Vanish(); return; }
                        if (Time.time >= _until) { Trace("stalker: time up"); Vanish(); return; }
                        break;

                    case Kind.Runner:
                    {
                        Vector3 step = _runDir * (RunSpeed * Time.deltaTime);
                        Vector3 next = transform.position + step;
                        Vector3 floor = PhysicsUtility.DropToFloor(next, 0.6f, 1.2f);
                        if (floor != next) next.y = floor.y;
                        transform.position = next;
                        if (_runDir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(_runDir);
                        _puppet.Move(_runDir * RunSpeed, WalkSpeed, RunSpeed);
                        _puppet.UpdateMoveType(ActorMoveType.Run);
                        if (Time.time >= _until) { Trace("runner: gone"); Vanish(); return; }
                        break;
                    }

                    case Kind.Behind:
                        if (!_lunging)
                        {
                            // It keeps up behind you while it's out of your sight: you can't simply walk away from it.
                            if (!(visible && angle < 70f) && dist > 3.4f && FollowBehind(avatar, cam))
                            {
                                head = transform.position + Vector3.up * 1.6f;
                                toMe = head - eye;
                                dist = toMe.magnitude;
                                angle = Vector3.Angle(cam.transform.forward, toMe);
                                visible = PhysicsUtility.CheckBlockByWall(eye, head);
                            }
                            FaceFlat(eye);
                            if (visible && angle < 40f && dist < 4.5f)
                            {
                                _lunging = true;
                                _lungeAt = Time.time;
                                try { _puppet.UseSkill(GrabSkill); }
                                catch (Exception e) { Log.WarnOnce("lunge", "The lunge animation failed: " + e.Message); }
                                Trace($"behind: you turned around ({dist:0.0} m): lunge");
                            }
                            else if (Time.time >= _until)
                            {
                                // Out of time: dissolves if you can see it, else simply isn't there any more.
                                bool seen = visible && angle < 70f;
                                Trace(seen ? "behind: time up, in view: gone" : "behind: never seen, gone quietly");
                                Vanish(quiet: !seen);
                                return;
                            }
                        }
                        else
                        {
                            FaceFlat(eye);
                            float t = Time.time - _lungeAt;
                            // Into your face: 1.4 m in a quarter second, stopping short of the camera.
                            if (t < 0.25f && dist > 0.9f)
                            {
                                Vector3 flat = new Vector3(toMe.x, 0f, toMe.z);
                                if (flat.sqrMagnitude > 0.01f) transform.position -= flat.normalized * (5.6f * Time.deltaTime);
                            }
                            if (!_hitDone && t >= 0.12f)
                            {
                                _hitDone = true;
                                HitFeedback(avatar);
                            }
                            if (t >= 0.55f) { Vanish(); return; }
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("update", "An apparition failed: " + e);
                Vanish(quiet: true);
            }
        }

        /// <summary>Behind: back to ~2.6 m behind you (on the navmesh, nothing between, no teammate there). False if
        /// there's no such spot (it stays where it is).</summary>
        bool FollowBehind(ProtoActor avatar, Camera cam)
        {
            Vector3 back = -cam.transform.forward;
            back.y = 0f;
            if (back.sqrMagnitude < 0.01f) return false;
            Vector3 me = avatar.transform.position;
            var nav = Hub.s?.navman;
            if (nav == null) return false;
            Vector3 n;
            try { n = nav.GetNearestPointOnNavMesh(me + back.normalized * 2.6f, 1.5f); }
            catch (Exception) { return false; }
            if (!NavMeshConstants.IsValid(n) || Mathf.Abs(n.y - me.y) > 1f) return false;
            if (!PhysicsUtility.CheckBlockByWall(me + Vector3.up * 1.5f, n + Vector3.up * 1.5f)) return false;
            if (Haunt.PlayerNear(_main, n, 1.5f)) return false; // never on top of a teammate (often right behind you)
            Vector3 floor = PhysicsUtility.DropToFloor(n, 0.5f, 1.5f);
            if (floor != n) n.y = floor.y;
            transform.position = n;
            return true;
        }

        static Transform FindDeep(Transform t, string name)
        {
            var queue = new Queue<Transform>();
            queue.Enqueue(t);
            while (queue.Count > 0)
            {
                var x = queue.Dequeue();
                if (x.name == name) return x;
                for (int i = 0; i < x.childCount; i++) queue.Enqueue(x.GetChild(i));
            }
            return null;
        }

        void FaceFlat(Vector3 target)
        {
            Vector3 d = target - transform.position;
            d.y = 0f;
            if (d.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(d);
        }

        /// <summary>The game's own local hit feedback on your avatar (particles, screen effect, sound). No packet, no
        /// damage.</summary>
        static void HitFeedback(ProtoActor avatar)
        {
            try { avatar.PlaySkillHitEffect(HitEffect, playAnimation: true); }
            catch (Exception e) { Log.WarnOnce("hit-effect", "The jumpscare's hit effect failed: " + e.Message); }
            try { if (avatar.puppet != null) avatar.puppet.PlayHitFeedback(); }
            catch (Exception e) { Log.WarnOnce("hit-feedback", "The jumpscare's hit feedback failed: " + e.Message); }
        }

        // ================================================================================================ vanish

        internal void Vanish(bool quiet = false)
        {
            if (_vanishing) return;
            _vanishing = true;
            _vanishAt = Time.time;
            if (ReferenceEquals(Active, this)) Active = null;
            if (quiet)
            {
                Destroy(gameObject);
                return;
            }
            try { _puppet.Stop(); } catch (Exception) { /* cosmetic */ }
            try { _puppet.PlayDespawnAnimation(); }
            catch (Exception e) { Log.WarnOnce("despawn", "The apparition's despawn animation failed: " + e.Message); }
            try { EnableDissolve(); } catch (Exception) { /* cosmetic */ }
            try { Hub.s.vfxman.InstantiateVfx(DespawnVfx, transform.position); }
            catch (Exception e) { Log.WarnOnce("despawn-vfx", "The apparition's despawn particles failed: " + e.Message); }
            Destroy(gameObject, 2f);
        }

        void VanishUpdate()
        {
            if (!_hidden && Time.time - _vanishAt >= (_kind == Kind.Behind ? 0.3f : 1.75f)) HideAll();
        }

        void EnableDissolve()
        {
            foreach (var r in _puppet.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                var m = r.material;
                if (m == null || m.shader == null) continue;
                _materials.Add(m);
                if (m.shader.keywordSpace.FindKeyword("_DISSOLVESWITCH_ON").isValid) m.EnableKeyword("_DISSOLVESWITCH_ON");
            }
        }

        void HideAll()
        {
            _hidden = true;
            try { _puppet.SetRenderersForceHidden(true); } catch (Exception) { /* below */ }
            foreach (var r in _puppet.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null) r.enabled = false;
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Active, this)) Active = null;
            if (!ReferenceEquals(_puppet, null)) _ours.Remove(_puppet);
            if (_ours.Count > 0) _ours.RemoveWhere(p => p == null);
            foreach (var m in _materials) if (m != null) Destroy(m);
        }

        /// <summary>Handles our puppet's clip events (Patches): true = let the game play it.</summary>
        internal bool OnAnimationEvent(string statement)
        {
            if (string.IsNullOrEmpty(statement)) return true;
            if (statement.StartsWith("mat:", StringComparison.Ordinal))
            {
                if (statement.Contains("turnon_dissolve")) { if (_vanishing) EnableDissolve(); }
                else if (statement.Contains("turnoff_dissolve")) { if (_vanishing) HideAll(); }
                return false;
            }
            if (_vanishing) return false;
            if (statement.StartsWith("itemsfx:", StringComparison.Ordinal)) return false;   // needs a real actor
            if (statement.StartsWith("camera:", StringComparison.Ordinal)) return false;    // never your camera
            if (statement.StartsWith("vfx:", StringComparison.Ordinal) && statement.Contains("socket_actor_root")) return false;
            return true;
        }

        void Trace(string what)
        {
            if (Settings.VerboseLog) Log.Info(what);
        }
    }

    /// <summary>P1: our apparition's clip events (the game would log "Actor not found" for some of them).</summary>
    [HarmonyPatch(typeof(PuppetScript), nameof(PuppetScript.OnAnimationEvent))]
    static class ApparitionEventPatch
    {
        static bool Prefix(PuppetScript __instance, AnimationEvent evt)
        {
            if (!Apparition.IsOurs(__instance)) return true;
            try
            {
                var a = __instance.GetComponentInParent<Apparition>();
                return a == null || a.OnAnimationEvent(evt != null ? evt.stringParameter : null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("P1", "An apparition's animation event failed: " + e);
                return false;
            }
        }
    }
}
