// MIMESIS Hallucinations - living player: the fake attacker
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// A bare puppet: the game's own prefab_IllusionMimicPuppet (same body, material and animator as players and mimics;
// the vanilla ghost mimic's puppet) under the level's BG root, with NO ProtoActor and no registration anywhere, so no
// game system sees it: not the server, not crosshair/interaction, not the actor map, not other players. This
// MonoBehaviour on its root moves it along the client navmesh, drives its animator (PuppetScript.Move / HoldByHand /
// UseSkill), holds a weapon built like the game's held-item hallucination (colliders and item logic off), plays the
// attack with the real animation states, item sounds and effects, and vanishes with the ghost mimic's despawn
// (PuppetScript.PlayDespawnAnimation with ActorType 10, the dissolve, FX_Mimic_Idle_Despawn) the moment its attack
// lands. Its "hit" is only the game's local hit feedback on the avatar (PlaySkillHitEffect: particles, screen effect,
// sound): no packet, no damage, no radiation.
// Robustness: every Update runs in try/catch (an exception ends it as Error, destroyed at once); every lookup that can
// fail falls back: no weapon -> bare hands (the mimic's grab); no attack state -> walk up and vanish.

using System;
using System.Collections.Generic;
using Bifrost.ConstEnum;
using DarkTonic.MasterAudio;
using HarmonyLib;
using Mimic.Actors;
using Mimic.Animation;
using ReluProtocol.Enum;
using Unity.Cinemachine;
using UnityEngine;

namespace Hallucinations
{
    sealed class FakeMimic : MonoBehaviour
    {
        // ---- puppet and data (EA 0.4.1) ----
        const string PuppetId = "prefab_IllusionMimicPuppet";      // the vanilla ghost mimic's puppet (no ragdoll)
        const string FallbackPuppetId = "prefab_MimicPuppet";      // same look; never call PuppetScript.Die on it
        const int ActorTypeSummon = 10;                            // selects the illusion mimic's Lifetime states
        const string DespawnVfx = "FX_Mimic_Idle_Despawn";         // ghost despawn particles + GhostMimicDespawnAudio
        const string ExplosionFx = "FX_Explosion_Large_Dark_TimeBomb"; // the time bomb's blast (FieldSkillData 70008)
        const float ExplosionFxSeconds = 7f;
        const int ShotgunItem = 1000, TimeBombItem = 5111;

        // ---- movement (mimics' move_speed_walk/run 350/700) ----
        const float WalkSpeed = 3.5f, RunSpeed = 7f;
        const float TurnSpeed = 540f;            // degrees/s
        const float ChargeDistance = 6f;         // runs once this close (or once seen)
        const float DirectDistance = 4f;         // with a clear line this close, it heads straight at the player
        const float TouchDistance = 0.8f;        // the player walks into it
        const float MaxFloorStep = 0.45f;        // + this frame's step: a floor further off is furniture or a gap, not stairs
        const float DirectOffSeconds = 1f;       // after such a floor, the navmesh route for a while instead of straight
        const float RouteInterval = 0.5f;
        const int MaxRouteFails = 3;             // 1.5 s without a route -> Lost
        const float NoWayLimit = 1.5f;
        const float DoorCheckInterval = 0.25f;
        const float ChestHeight = 1.2f, HeadHeight = 1.65f;

        // ---- vanish ----
        const float DespawnSeconds = 2f;         // SummonData 70000001 despawn_delay 2000
        const float ExplodedLinger = 3f;         // keep the bomb's sounds going
        const float HideAt = 1.75f;              // backup for the despawn clip's mat:turnoff_dissolve (1.633): hidden for good
        const float LandedHideAt = 0.25f;        // its attack landed: gone at once (just the start of the dissolve, the particles)
        const float WeaponShrinkSeconds = 0.6f;
        const float TintInterval = 0.25f;

        enum Phase { Stalk, Charge, Windup, Attack, Vanish }

        /// <summary>One way to attack: the real animation state and its hit frame (from the clip events).</summary>
        sealed class AttackSpec
        {
            public string Name;
            public int Item;             // item master id held (0 = bare hands)
            public int Skill;            // 0 = no animation (walk up and vanish)
            public string State;         // SkillInfo.SkillAnimationState, to check the animator has it
            public float HitAt;          // s after UseSkill
            public float StartReach;     // starts the attack this close (with a clear line)
            public float LandReach;      // lands if the player is this close at the hit frame
            public float HalfAngle;      // ...and within this angle of where it faces (180 = any)
            public int Effect;           // SkillTargetEffectData id played on the avatar when it lands
            public float Aim;            // standing aim before the attack
            public bool Melee;           // swing sound at the hit; keeps stepping in while swinging
            public bool MoveDuring;
        }

        static readonly AttackSpec ShotgunSpec = new AttackSpec { Name = "shotgun", Item = ShotgunItem, Skill = 10900, State = "Action.Handgun.Fire", HitAt = 0.05f, StartReach = 5f, LandReach = 8f, HalfAngle = 30f, Effect = 9001, Aim = 0.35f };
        static readonly AttackSpec BombSpec = new AttackSpec { Name = "time bomb", Item = TimeBombItem, Skill = 7910, State = "Skill.FakeSuicideBomb", HitAt = 1.36f, StartReach = 2.5f, LandReach = 5f, HalfAngle = 180f, Effect = 40003 };
        static readonly AttackSpec[] MeleeSpecs =
        {
            new AttackSpec { Name = "bat", Item = 1100, Skill = 10800, State = "Action.Bat.Swing", HitAt = 0.69f, StartReach = 1.8f, LandReach = 2.6f, HalfAngle = 60f, Effect = 40007, Melee = true, MoveDuring = true },
            new AttackSpec { Name = "crowbar", Item = 2900, Skill = 10806, State = "Action.Bat.Swing2", HitAt = 0.25f, StartReach = 1.8f, LandReach = 2.6f, HalfAngle = 60f, Effect = 40002, Melee = true, MoveDuring = true },
            new AttackSpec { Name = "frying pan", Item = 5106, Skill = 11003, State = "Action.OneHandedScrap.Swing", HitAt = 0.50f, StartReach = 1.8f, LandReach = 2.6f, HalfAngle = 60f, Effect = 40002, Melee = true, MoveDuring = true },
            new AttackSpec { Name = "folding chair", Item = 5151, Skill = 10807, State = "Action.TwoHandedScrapSideGrip.Swing3", HitAt = 0.68f, StartReach = 1.8f, LandReach = 2.6f, HalfAngle = 60f, Effect = 40003, Melee = true, MoveDuring = true },
        };
        static readonly AttackSpec GrabSpec = new AttackSpec { Name = "bare hands", Skill = 7900, State = "Skill.FakeGrapple", HitAt = 0.06f, StartReach = 1.5f, LandReach = 2f, HalfAngle = 60f, Effect = 40002 };
        static readonly AttackSpec WalkUpSpec = new AttackSpec { Name = "bare hands, no attack animation", HitAt = 0f, StartReach = 1.5f, LandReach = 2f, HalfAngle = 75f, Effect = 40002 };

        static readonly int ActorTypeParam = Animator.StringToHash("ActorType");
        static readonly Vector4[] _planes = new Vector4[6];

        // ---- identity ----
        internal int Id;
        internal PuppetScript Puppet;
        internal string Description => _spec != null ? _spec.Name : "?";
        internal bool IsVanishing => _phase == Phase.Vanish;
        internal Vector3 Chest => transform.position + Vector3.up * ChestHeight;
        internal Vector3 Head => transform.position + Vector3.up * HeadHeight;

        // ---- state ----
        GameMainBase _main;
        ProtoActor _avatar;
        Animator _anim;
        PuppetContaAppearance _tint;
        GameObject _weaponGo;
        Renderer[] _weaponRenderers = Array.Empty<Renderer>();
        Vector3 _weaponScale = Vector3.one;
        PlaySoundResult _beep;
        bool _beepPending;
        int _beepTries;
        float _nextBeepAt;
        readonly List<Material> _ownedMaterials = new List<Material>();
        AttackSpec _spec;
        Weapon _weapon;
        bool _navOk;
        Phase _phase;
        float _spawnedAt, _ttl, _phaseAt, _attackT, _vanishAt, _nextTintAt, _nextRouteAt, _nextDoorCheckAt, _noWayFor, _directOffUntil;
        float _hideAt = HideAt;
        bool _hit, _fired, _exploded, _explosionFxShown, _quiet, _noDespawnEffect, _hidden;
        readonly List<Vector3> _path = new List<Vector3>();
        int _pathIdx, _routeFails;
        readonly HashSet<string> _playedItemSfx = new HashSet<string>();

        // ======================================================================================== creation (9.1-9.2)

        /// <summary>Builds the fake at pos facing yaw and starts it. Null (and nothing left behind) when it can't be built.
        /// navOk false: the spot came from the physics fallback (no client navmesh), so it walks straight.</summary>
        internal static FakeMimic Create(GameMainBase main, in HostMsg spawn, Vector3 pos, float yaw, bool navOk)
        {
            GameObject root = null;
            PuppetScript puppet = null;
            try
            {
                var table = Hub.s != null && Hub.s.tableman != null ? Hub.s.tableman.monsterPuppet : null;
                if (table == null) { Log.WarnOnce("no-puppet-table", "No puppet table (game update?): hallucinations can't be shown."); return null; }
                var row = table.FindRow(PuppetId);
                if (row == null || row.prefab == null) row = table.FindRow(FallbackPuppetId);
                if (row == null || row.prefab == null) { Log.WarnOnce("no-puppet", $"Neither {PuppetId} nor {FallbackPuppetId} exists (game update?): hallucinations can't be shown."); return null; }

                // Built under an inactive root: nothing of the prefab runs (Awake, OnEnable) before its physics and cameras
                // are off.
                root = new GameObject($"Hallucination#{spawn.Id}");
                root.SetActive(false);
                var bg = main != null ? main.GetBGRoot() : null;
                if (bg != null) root.transform.SetParent(bg, false); // destroyed with the level, whatever else happens
                // The navmesh point can be a little above or below the real floor: stand on the floor (within 0.5 m).
                Vector3 floor = PhysicsUtility.DropToFloor(pos, 0.5f, 1f);
                if (floor != pos) pos.y = floor.y;
                root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

                var puppetGo = Instantiate(row.prefab, root.transform, false);
                puppetGo.name = row.prefab.name;
                puppetGo.transform.localPosition = Vector3.zero;
                puppetGo.transform.localRotation = Quaternion.identity;
                puppet = puppetGo.GetComponent<PuppetScript>();
                var anim = puppetGo.GetComponent<Animator>();
                if (puppet == null || anim == null)
                {
                    Log.WarnOnce("puppet-parts", $"{row.prefab.name} has no PuppetScript/Animator (game update?): hallucinations can't be shown.");
                    Destroy(root);
                    return null;
                }
                MakeInert(puppetGo);
                AddHeadMarker(puppetGo.transform);

                var fake = root.AddComponent<FakeMimic>();
                fake.Id = spawn.Id;
                fake.Puppet = puppet;
                fake._anim = anim;
                fake._main = main;
                fake._avatar = main != null ? main.GetMyAvatar() : null;
                fake._navOk = navOk;
                fake._tint = puppetGo.GetComponent<PuppetContaAppearance>();
                Victim.RegisterPuppet(puppet);

                root.SetActive(true);
                fake.Setup(spawn);
                return fake;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("fake-create", "Building a hallucination failed: " + e);
                if (!ReferenceEquals(puppet, null)) Victim.UnregisterPuppet(puppet);
                if (root != null) Destroy(root);
                return null;
            }
        }

        /// <summary>No physics (the ragdoll bones' colliders and bodies), no cameras of its own: it can't block, trigger,
        /// be hit by real attacks, or take over the view.</summary>
        static void MakeInert(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            foreach (var cam in go.GetComponentsInChildren<CinemachineVirtualCameraBase>(true)) cam.enabled = false;
        }

        /// <summary>The bone "Head" has an AudioSource but no socket marker: add one, so clip events "sound:x@Head" (the
        /// grab's choke) find it.</summary>
        static void AddHeadMarker(Transform puppet)
        {
            var head = FindDeep(puppet, "Head");
            if (head != null && !head.TryGetComponent<SocketNodeMarker>(out _)) head.gameObject.AddComponent<SocketNodeMarker>();
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

        void Setup(in HostMsg spawn)
        {
            if (SocketAttachable.HasParameter(_anim, ActorTypeParam)) _anim.SetInteger(ActorTypeParam, ActorTypeSummon);

            // The weapon and its attack. The melee item comes from the seed (the same on a replay of the log line).
            var rnd = new System.Random(spawn.Seed ^ 0x2545F491);
            _weapon = spawn.Weapon;
            switch (_weapon)
            {
                case Weapon.Shotgun: _spec = ShotgunSpec; break;
                case Weapon.Bomb: _spec = BombSpec; break;
                case Weapon.Melee: _spec = MeleeSpecs[rnd.Next(MeleeSpecs.Length)]; break;
                default: _spec = GrabSpec; break;
            }
            if (_spec.Item != 0 && (StateMissing(_spec.State) || !BuildWeapon(_spec.Item)))
            {
                Log.Warn($"Hallucination #{Id}: its {_spec.Name} couldn't be built (game update?): bare hands instead");
                DropWeapon();
                _spec = GrabSpec;
                _weapon = Weapon.Grab;
            }
            if (_weapon == Weapon.Grab || _spec.Item == 0) Puppet.HoldByHand(PuppetHandheldState.Empty);
            if (_spec.Skill != 0 && StateMissing(_spec.State))
            {
                Log.WarnOnce("no-grab-state", $"The animator has no {_spec.State} (game update?): hallucinations walk up and vanish.");
                _spec = WalkUpSpec;
            }

            _spawnedAt = Time.time;
            _ttl = Mathf.Clamp(spawn.TtlMs > 0 ? spawn.TtlMs / 1000f : Settings.LifetimeDefault, 5f, 120f);
            _phase = Phase.Stalk;
            _phaseAt = _spawnedAt;
            Puppet.Move(Vector3.zero, WalkSpeed, RunSpeed);
            Puppet.UpdateMoveType(ActorMoveType.Walk);
            ApplyTint();
        }

        /// <summary>The vanilla held-item hallucination recipe (ProtoActor.TryStartHeldItemHallucination): the item's prefab
        /// on the puppet's hand socket, its item logic, colliders and bodies off, posed by its "_pivot" socket.</summary>
        bool BuildWeapon(int itemId)
        {
            GameObject staging = null, go = null;
            try
            {
                var info = Hub.s.dataman.ExcelDataManager.GetItemInfo(itemId);
                if (info == null || string.IsNullOrWhiteSpace(info.AttachSocketName)) return false;
                var prefab = Hub.s.tableman.lootingObject.FindSkinnedItemInfo(itemId).skinnedItemInfo?.prefab;
                if (prefab == null) return false;
                var socket = SocketNodeMarker.SearchBreadthFirst(Puppet.transform, info.AttachSocketName);
                if (socket == null) return false;

                staging = new GameObject($"HallucinationWeaponStaging_{itemId}");
                staging.SetActive(false);
                staging.transform.SetParent(socket, false);
                go = Instantiate(prefab, staging.transform, false);
                go.name = $"HallucinationWeapon_{itemId}";
                go.SetActive(false);
                foreach (var c in go.GetComponentsInChildren<LootingLevelObject>(true)) c.enabled = false;
                foreach (var c in go.GetComponentsInChildren<SocketAttachable>(true)) c.enabled = false;
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                {
                    rb.detectCollisions = false;
                    rb.isKinematic = true;
                }
                var pivot = SocketNodeMarker.SearchBreadthFirst(go.transform, info.AttachSocketName + "_pivot");
                if (pivot != null)
                {
                    go.transform.localPosition = pivot.localPosition;
                    go.transform.localRotation = pivot.localRotation;
                    go.transform.localScale = pivot.localScale;
                }
                go.transform.SetParent(socket, false);
                Destroy(staging);
                staging = null;
                go.SetActive(true);
                foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.keepAnimatorStateOnDisable = true;

                _weaponGo = go;
                _weaponScale = go.transform.localScale;
                _weaponRenderers = go.GetComponentsInChildren<Renderer>(true);
                go = null;
                Puppet.HoldByHand(info.PuppetHandheldState);

                if (itemId == TimeBombItem)
                {
                    // Its own sound groups (DynamicSoundGroupCreator) may register a frame after activation: start a
                    // moment later, and retry a few times.
                    _beepPending = true;
                    _beepTries = 0;
                    _nextBeepAt = Time.time + 0.1f;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.WarnOnce("weapon-" + itemId, $"Building the hallucination's weapon {itemId} failed: {e.Message}");
                if (go != null) Destroy(go);
                return false;
            }
            finally
            {
                if (staging != null) Destroy(staging);
            }
        }

        void DropWeapon()
        {
            StopBeep();
            if (_weaponGo != null) Destroy(_weaponGo);
            _weaponGo = null;
            _weaponRenderers = Array.Empty<Renderer>();
        }

        /// <summary>Fake bombs beep fast (TimeBomb.OnAttachToSocket); the disabled TimeBomb won't, so we do.</summary>
        void TryBeep(float now)
        {
            if (!_beepPending || now < _nextBeepAt) return;
            try
            {
                if (_weaponGo == null) { _beepPending = false; return; }
                var tb = _weaponGo.GetComponentInChildren<TimeBomb>(true);
                string clip = tb != null && !string.IsNullOrEmpty(tb.BombBeepFastAudioClipName) ? tb.BombBeepFastAudioClipName : "Bomb_Beep_Fast";
                _beep = Hub.s.audioman.PlaySfxTransform(clip, _weaponGo.transform);
                if (_beep != null && (_beep.SoundPlayed || _beep.SoundScheduled)) { _beepPending = false; return; }
            }
            catch (Exception e)
            {
                _beepPending = false;
                Log.WarnOnce("beep", "The hallucination's bomb couldn't beep: " + e.Message);
                return;
            }
            if (++_beepTries >= 4)
            {
                _beepPending = false;
                Log.WarnOnce("beep-silent", "The hallucination's bomb stays silent (its beep sound isn't loaded).");
            }
            else _nextBeepAt = now + 0.25f;
        }

        void StopBeep()
        {
            _beepPending = false;
            var b = _beep;
            _beep = null;
            try
            {
                if (b != null && b.ActingVariation != null) b.ActingVariation.Stop();
            }
            catch (Exception) { /* already gone */ }
        }

        bool? _stateCheckWorks;

        /// <summary>The animator surely lacks this state (a game update renamed it). Only trusted when the same check finds
        /// a state every puppet has: if it can't see that one either, the check itself is what fails, and the attack is
        /// tried anyway (vanilla's own UseSkill uses the same full-path hash).</summary>
        bool StateMissing(string fullPath)
        {
            if (!_stateCheckWorks.HasValue)
            {
                _stateCheckWorks = HasState("Locomotion.Default.Idle") || HasState("Lifetime.Alive");
                if (!_stateCheckWorks.Value) Log.WarnOnce("state-check", "Can't look up the puppet's animator states: attacks are tried as they are.");
            }
            return _stateCheckWorks.Value && !HasState(fullPath);
        }

        bool HasState(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || _anim == null) return false;
            int hash = Animator.StringToHash(fullPath);
            for (int i = 0; i < _anim.layerCount; i++)
                if (_anim.HasState(i, hash)) return true;
            return false;
        }

        // ============================================================================================ behaviour (9.3)

        void Update()
        {
            if (_phase == Phase.Vanish)
            {
                try { VanishUpdate(); }
                catch (Exception e)
                {
                    Log.ErrorOnce("fake-vanish", "A hallucination's vanish failed: " + e);
                    Destroy(gameObject);
                }
                return;
            }
            try
            {
                string why = Watchdog();
                if (why != null)
                {
                    Log.Trace($"Hallucination #{Id} cut short: {why}");
                    Vanish(Outcome.Cancelled, quiet: true);
                    return;
                }
                float now = Time.time, dt = Time.deltaTime;
                if (_beepPending) TryBeep(now);
                if (now - _spawnedAt > _ttl)
                {
                    Vanish(Outcome.Timeout);
                    return;
                }

                var cam = Camera.main;
                Vector3 me = _avatar.transform.position;
                Vector3 eye = cam != null ? cam.transform.position : me + Vector3.up * HeadHeight;
                Vector3 pos = transform.position;
                float dist = Placement.Flat(me - pos).magnitude;
                if (_phase != Phase.Attack && dist < TouchDistance && Mathf.Abs(me.y - pos.y) < 1.5f)
                {
                    Log.Trace($"Hallucination #{Id}: you walked into it");
                    Vanish(Outcome.HitByPlayer);
                    return;
                }

                switch (_phase)
                {
                    case Phase.Stalk:
                    case Phase.Charge:
                        Approach(now, dt, cam, me, eye, dist);
                        break;
                    case Phase.Windup:
                        WindupUpdate(now, dt, me, eye, dist);
                        break;
                    case Phase.Attack:
                        AttackUpdate(dt, me, eye, dist);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("fake-update", "A hallucination failed while running: " + e);
                Fail();
            }
        }

        void LateUpdate()
        {
            if (Time.time < _nextTintAt) return;
            _nextTintAt = Time.time + TintInterval;
            try { ApplyTint(); }
            catch (Exception e) { Log.WarnOnce("tint", "Tinting the hallucination failed: " + e.Message); }
        }

        /// <summary>Why it must go at once (quietly), or null.</summary>
        string Watchdog()
        {
            var main = Hub.Main;
            if (main == null || !ReferenceEquals(main, _main) || !(main is GamePlayScene scene)) return "left the level";
            if (!scene.IsGameLogicRunning) return "the level ended"; // exit cutscene and results: same scene and avatar
            var me = main.GetMyAvatar();
            if (me == null || !ReferenceEquals(me, _avatar)) return "your avatar changed";
            if (me.dead) return "you died";
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            if (cm != null && cm.IsSpectatorMode) return "spectating";
            if (Control.IsReplay) return "replay";
            return null;
        }

        void Approach(float now, float dt, Camera cam, Vector3 me, Vector3 eye, float dist)
        {
            if (_phase == Phase.Stalk && (dist <= ChargeDistance || Seen(cam)))
            {
                _phase = Phase.Charge;
                _phaseAt = now;
                Log.Trace($"Hallucination #{Id}: charging ({(dist <= ChargeDistance ? "close" : "seen")}, {dist:0.0} m)");
            }
            bool line = HasLine(eye, me);
            if (dist <= _spec.StartReach && line && Mathf.Abs(me.y - transform.position.y) < 2f)
            {
                _phase = Phase.Windup;
                _phaseAt = now;
                return;
            }

            Vector3 target;
            bool direct = line && Mathf.Abs(me.y - transform.position.y) < 1.2f && (dist <= DirectDistance || !_navOk) &&
                          (!_navOk || now >= _directOffUntil);
            if (direct) target = me;
            else if (!_navOk || !NextWaypoint(now, me, out target))
            {
                if (_navOk && _routeFails >= MaxRouteFails)
                {
                    Log.Trace($"Hallucination #{Id}: no route to you");
                    Vanish(Outcome.Lost);
                    return;
                }
                _noWayFor += dt;
                if (_noWayFor > NoWayLimit)
                {
                    Log.Trace($"Hallucination #{Id}: no way to you");
                    Vanish(Outcome.Lost);
                    return;
                }
                StandStill();
                FaceTowards(me, dt);
                return;
            }
            _noWayFor = 0f;

            if (now >= _nextDoorCheckAt)
            {
                _nextDoorCheckAt = now + DoorCheckInterval;
                if (PhysicsUtility.IsBlockedByClosedDoor(transform.position, target))
                {
                    Log.Trace($"Hallucination #{Id}: a closed door is in its way");
                    Vanish(Outcome.Lost);
                    return;
                }
            }
            if (!MoveTowards(target, _phase == Phase.Charge ? RunSpeed : WalkSpeed, dt) && direct && _navOk)
                _directOffUntil = now + DirectOffSeconds; // furniture or a gap on the straight line: the navmesh goes around
        }

        /// <summary>The next point of the navmesh route to the player (refreshed twice a second). False = no usable route
        /// right now.</summary>
        bool NextWaypoint(float now, Vector3 me, out Vector3 target)
        {
            target = me;
            bool refreshed = false;
            if (now >= _nextRouteAt)
            {
                _nextRouteAt = now + RouteInterval;
                RefreshRoute(me);
                refreshed = true;
            }
            if (_routeFails >= MaxRouteFails) return false;
            Vector3 pos = transform.position;
            while (_pathIdx < _path.Count && Placement.Flat(_path[_pathIdx] - pos).magnitude < 0.35f) _pathIdx++;
            if (_pathIdx >= _path.Count && !refreshed)
            {
                // At the end of an older route: the player moved on. A fresh one now rather than a stop.
                _nextRouteAt = now + RouteInterval;
                RefreshRoute(me);
                if (_routeFails >= MaxRouteFails) return false;
                while (_pathIdx < _path.Count && Placement.Flat(_path[_pathIdx] - pos).magnitude < 0.35f) _pathIdx++;
            }
            if (_pathIdx >= _path.Count) return false; // the route ends short of the player (and no clear line)
            target = _path[_pathIdx];
            return true;
        }

        void RefreshRoute(Vector3 me)
        {
            PathFindResult r = null;
            try
            {
                var nav = Hub.s != null ? Hub.s.navman : null;
                if (nav != null) r = nav.GetRoute(transform.position, me, shouldArriveEndPoint: false);
            }
            catch (Exception e) { Log.WarnOnce("route", "Routing the hallucination failed: " + e.Message); }
            if (r != null && r.Success && r.PathPoints != null && r.PathPoints.Count > 0)
            {
                _path.Clear();
                _path.AddRange(r.PathPoints);
                _pathIdx = 0;
                _routeFails = 0;
            }
            else
            {
                _routeFails++;
                Log.Trace($"Hallucination #{Id}: no route ({_routeFails}/{MaxRouteFails})");
            }
        }

        /// <summary>One step toward target, on the floor. False when the floor there wasn't a plausible step (furniture in
        /// the way, or no floor at all): it then keeps the straight height line instead of jumping up or down.</summary>
        bool MoveTowards(Vector3 target, float speed, float dt)
        {
            Vector3 pos = transform.position;
            Vector3 flat = Placement.Flat(target - pos);
            float len = flat.magnitude;
            if (len < 0.05f || dt <= 0f)
            {
                StandStill();
                return true;
            }
            Vector3 dir = flat / len;
            float step = Mathf.Min(speed * dt, len);
            Vector3 next = pos + dir * step;
            next.y = Mathf.Lerp(pos.y, target.y, step / len);
            Vector3 floor = PhysicsUtility.DropToFloor(next, 1f, 2f);
            bool floorOk = floor != next && Mathf.Abs(floor.y - next.y) <= MaxFloorStep + step;
            if (floorOk) next.y = floor.y;
            transform.position = next;
            Face(dir, dt);
            Puppet.Move(dir * speed, WalkSpeed, RunSpeed);
            Puppet.UpdateMoveType(speed > WalkSpeed + 0.1f ? ActorMoveType.Run : ActorMoveType.Walk);
            return floorOk;
        }

        void StandStill()
        {
            Puppet.Move(Vector3.zero, WalkSpeed, RunSpeed); // eases to 0 (PuppetScript's velocity stickiness)
            Puppet.UpdateMoveType(ActorMoveType.Walk);
        }

        void Face(Vector3 flatDir, float dt)
        {
            if (flatDir.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flatDir), TurnSpeed * dt);
        }

        void FaceTowards(Vector3 at, float dt) => Face(Placement.Flat(at - transform.position).normalized, dt);

        /// <summary>The player can see it: in the camera's view and not behind a wall.</summary>
        bool Seen(Camera cam)
        {
            if (cam == null) return false;
            Placement.FrustumPlanes(cam.projectionMatrix * cam.worldToCameraMatrix, _planes);
            if (!Placement.InFrustum(_planes, transform.position + Vector3.up * 0.95f, new Vector3(0.4f, 0.95f, 0.4f))) return false;
            Vector3 eye = cam.transform.position;
            return PhysicsUtility.CheckBlockByWall(eye, Chest) || PhysicsUtility.CheckBlockByWall(eye, Head);
        }

        /// <summary>A clear line from its chest to the player (head or body).</summary>
        bool HasLine(Vector3 eye, Vector3 me) =>
            PhysicsUtility.CheckBlockByWall(Chest, eye) || PhysicsUtility.CheckBlockByWall(Chest, me + Vector3.up * 1f);

        void WindupUpdate(float now, float dt, Vector3 me, Vector3 eye, float dist)
        {
            StandStill();
            FaceTowards(me, dt);
            if (!HasLine(eye, me) || dist > _spec.StartReach + 1.5f)
            {
                _phase = Phase.Charge; // the player got away before it struck: after them again
                _phaseAt = now;
                return;
            }
            if (now - _phaseAt >= _spec.Aim) StartAttack(now);
        }

        // ============================================================================================== attack (9.4)

        void StartAttack(float now)
        {
            _phase = Phase.Attack;
            _phaseAt = now;
            _attackT = 0f;
            _hit = _fired = false;
            _playedItemSfx.Clear();
            if (_spec.Skill != 0) Puppet.UseSkill(_spec.Skill);
            Log.Trace($"Hallucination #{Id}: attacks ({_spec.Name})");
        }

        void AttackUpdate(float dt, Vector3 me, Vector3 eye, float dist)
        {
            _attackT += dt;
            if (!_hit && !_exploded)
            {
                if (_spec.MoveDuring && dist > 1.1f && HasLine(eye, me)) MoveTowards(me, WalkSpeed, dt);
                else
                {
                    StandStill();
                    FaceTowards(me, dt);
                }
            }

            if (_weapon == Weapon.Shotgun && !_fired && _attackT >= 0.03f)
            {
                // The clip's own itemsfx events (0.033 s, through C4) play the same sounds; each plays once per attack.
                _fired = true;
                FireWeaponAnimation();
                PlayShotgunSfx();
            }
            if (_weapon == Weapon.Bomb)
            {
                // Normally the clip's own "sfx:Bomb_Explosion_Body" (1.321 s) sets it off through C4; this is the backup.
                if (!_exploded && _attackT >= _spec.HitAt) Explode();
            }
            else if (!_hit && _attackT >= _spec.HitAt) Hit();
            if (_phase == Phase.Attack && _attackT > _spec.HitAt + 3f) Vanish(Outcome.Missed); // safety net
        }

        /// <summary>The hit frame: it lands if the player is still in reach, in front of it, with a clear line.</summary>
        void Hit()
        {
            _hit = true;
            if (_spec.Melee) PlayItemSfx("swing"); // the clip's own swing comes a moment after the hit frame: play it now
            if (_weapon == Weapon.Shotgun) PlayShotgunSfx(); // a slow frame can reach the hit before the 0.03 s mark
            bool landed = InReach(transform.position, _spec.LandReach, _spec.HalfAngle);
            if (landed) HitAvatar(_spec.Effect);
            Vanish(landed ? Outcome.Landed : Outcome.Missed);
        }

        /// <summary>The bomb goes off: the real blast (particles and its sounds) where the bomb is, the fake gone with it.</summary>
        void Explode()
        {
            if (_exploded) return;
            _exploded = true;
            _hit = true;
            StopBeep();
            Vector3 at = _weaponGo != null ? _weaponGo.transform.position : Chest;
            _explosionFxShown = SpawnExplosion(at);
            HideAll();
            bool landed = InReach(at, _spec.LandReach, 180f);
            if (landed) HitAvatar(_spec.Effect);
            Vanish(landed ? Outcome.Landed : Outcome.Missed, noDespawnEffect: true);
        }

        bool SpawnExplosion(Vector3 at)
        {
            try
            {
                var fs = Hub.s.tableman.fieldSkills;
                if (fs != null && fs.TryGet(ExplosionFx, out var row) && row != null && row.prefab != null)
                {
                    var bg = _main != null ? _main.GetBGRoot() : null;
                    var fx = bg != null ? Instantiate(row.prefab, at, Quaternion.identity, bg) : Instantiate(row.prefab, at, Quaternion.identity);
                    Destroy(fx, ExplosionFxSeconds);
                    return true;
                }
                Log.WarnOnce("no-explosion", $"No {ExplosionFx} (game update?): the hallucination's bomb only sounds.");
            }
            catch (Exception e) { Log.WarnOnce("explosion", "The hallucination's blast failed: " + e.Message); }
            return false;
        }

        bool InReach(Vector3 from, float reach, float halfAngle)
        {
            if (_avatar == null) return false;
            Vector3 me = _avatar.transform.position;
            Vector3 to = Placement.Flat(me - from);
            if (to.magnitude > reach || Mathf.Abs(me.y - from.y) > 2.5f) return false;
            if (halfAngle < 180f && to.sqrMagnitude > 0.01f && Vector3.Angle(Placement.Flat(transform.forward), to) > halfAngle) return false;
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : me + Vector3.up * HeadHeight;
            Vector3 origin = from + (from == transform.position ? Vector3.up * ChestHeight : Vector3.zero);
            return PhysicsUtility.CheckBlockByWall(origin, eye) || PhysicsUtility.CheckBlockByWall(origin, me + Vector3.up * 1f);
        }

        /// <summary>What a real hit looks and sounds like on this game (ProtoActor.ResolvePacket_HitTargetSig without the
        /// server): the effect's particles, screen effect, sound and flinch, and the hit feedback. Local only: no packet,
        /// no stat.</summary>
        void HitAvatar(int effectId)
        {
            if (_avatar == null) return;
            try { _avatar.PlaySkillHitEffect(effectId, playAnimation: true); }
            catch (Exception e) { Log.WarnOnce("hit-effect", "The hallucination's hit effect failed: " + e.Message); }
            try { if (_avatar.puppet != null) _avatar.puppet.PlayHitFeedback(); }
            catch (Exception e) { Log.WarnOnce("hit-feedback", "The hallucination's hit feedback failed: " + e.Message); }
        }

        /// <summary>The shotgun prefab's own "Fire" animation (the vanilla SocketAttachable.OnFire, on its disabled
        /// component: the method doesn't need it enabled).</summary>
        void FireWeaponAnimation()
        {
            try
            {
                var sa = _weaponGo != null ? _weaponGo.GetComponentInChildren<SocketAttachable>(true) : null;
                if (sa != null) sa.OnFire();
            }
            catch (Exception e) { Log.WarnOnce("fire-anim", "The hallucination's shotgun animation failed: " + e.Message); }
        }

        /// <summary>An item sound as the game's "itemsfx:key" clip event plays it for a real holder, once per attack.</summary>
        void PlayItemSfx(string key)
        {
            if (_spec == null || _spec.Item == 0 || string.IsNullOrEmpty(key) || !_playedItemSfx.Add(key)) return;
            try
            {
                var tables = Hub.s.tableman;
                if (tables == null) return;
                string id = key == "swing" ? tables.itemSound?.GetSwingSfxId(_spec.Item)
                    : key == "throw" ? tables.itemSound?.GetThrowSfxId(_spec.Item)
                    : tables.itemExtraSfx?.GetSfxId(_spec.Item, key);
                // At a fixed point, not following the fake: its long tails outlive it.
                if (!string.IsNullOrEmpty(id)) Hub.s.audioman.PlaySfxAtPosition(id, Chest);
            }
            catch (Exception e) { Log.WarnOnce("itemsfx", "A hallucination's item sound failed: " + e.Message); }
        }

        void PlayShotgunSfx()
        {
            PlayItemSfx("fire");
            PlayItemSfx("firetail_outdoor");
            PlayItemSfx("firetail_indoor");
        }

        /// <summary>C4: a clip event on our puppet. True = the game's AnimationEventHandler runs it (sfx/sound/vfx/feedback
        /// work without a ProtoActor once the sockets exist); false = handled (or dropped) here.</summary>
        internal bool OnAnimationEvent(string statement)
        {
            if (string.IsNullOrEmpty(statement)) return true;
            if (statement.StartsWith("mat:", StringComparison.Ordinal))
            {
                // The despawn clip's dissolve (vanilla does nothing without an actor).
                if (statement.Contains("turnon_dissolve")) { if (_phase == Phase.Vanish) EnableDissolve(); }
                else if (statement.Contains("turnoff_dissolve")) { if (_phase == Phase.Vanish) HideAll(); } // never pop back
                return false;
            }
            // Gone (or going): nothing more from its clips. Its attack clip keeps running under the despawn and would
            // still swing, beep or explode; the despawn's own particles are spawned by Vanish.
            if (_phase == Phase.Vanish) return false;
            if (statement.StartsWith("itemsfx:", StringComparison.Ordinal))
            {
                // Vanilla needs an actor (RError "Actor not found"): the same sound, played here.
                if (_phase == Phase.Attack) PlayItemSfx(EventParam(statement, "itemsfx:".Length));
                return false;
            }
            if (statement.StartsWith("vfx:", StringComparison.Ordinal) && statement.Contains("socket_actor_root")) return false;
            if (statement.StartsWith("camera:", StringComparison.Ordinal)) return false; // never the player's camera
            if (statement.StartsWith("sfx:Bomb_Explosion_Body", StringComparison.Ordinal) && _weapon == Weapon.Bomb)
            {
                if (_phase != Phase.Attack) return false;
                Explode();
                return !_explosionFxShown; // the blast prefab plays its own explosion sounds; without it, this one
            }
            return true;
        }

        static readonly char[] EventSeparators = { '@', ':' };

        static string EventParam(string statement, int start)
        {
            int end = statement.IndexOfAny(EventSeparators, start);
            return end < 0 ? statement.Substring(start) : statement.Substring(start, end - start);
        }

        // ============================================================================================== vanish (9.6)

        /// <summary>The player's attack reached it (Victim's hit check), a blast caught it, or the player walked into it.</summary>
        internal void OnHitByPlayer()
        {
            if (_phase == Phase.Vanish || _exploded) return;
            Vanish(Outcome.HitByPlayer);
        }

        /// <summary>Gone at once, no effect (level left, reset).</summary>
        internal void Cancel(string why)
        {
            if (_phase == Phase.Vanish) return;
            Log.Trace($"Hallucination #{Id} cut short: {why}");
            Vanish(Outcome.Cancelled, quiet: true);
        }

        void Fail()
        {
            try { Vanish(Outcome.Error, quiet: true); }
            catch (Exception) { /* below */ }
            try { if (this != null) Destroy(gameObject); }
            catch (Exception) { /* already gone */ }
        }

        void Vanish(Outcome outcome, bool quiet = false, bool noDespawnEffect = false)
        {
            if (_phase == Phase.Vanish) return;
            _phase = Phase.Vanish;
            _vanishAt = Time.time;
            _quiet = quiet;
            _noDespawnEffect = noDespawnEffect;
            // Its attack landed: it's gone the moment it hits, not dissolving through the rest of the swing.
            if (outcome == Outcome.Landed) _hideAt = LandedHideAt;
            try { Victim.OnFakeEnded(this, outcome); }
            catch (Exception e) { Log.ErrorOnce("fake-ended", "Reporting a hallucination's end failed: " + e); }
            StopBeep();
            if (quiet)
            {
                Destroy(gameObject);
                return;
            }
            try { Puppet.Stop(); }
            catch (Exception) { /* cosmetic */ }
            if (!noDespawnEffect)
            {
                // The ghost mimic's despawn: the Lifetime layer's "Dead/Despawn (Illusion Mimic)" (ActorType 10 +
                // IsDespawning) animates the dissolve, which needs the material keyword on; the particles and their sound.
                try { Puppet.PlayDespawnAnimation(); }
                catch (Exception e) { Log.WarnOnce("despawn-anim", "The hallucination's despawn animation failed: " + e.Message); }
                try { EnableDissolve(); }
                catch (Exception e) { Log.WarnOnce("dissolve", "The hallucination's dissolve failed: " + e.Message); }
                try { Hub.s.vfxman.InstantiateVfx(DespawnVfx, transform.position); }
                catch (Exception e) { Log.WarnOnce("despawn-vfx", "The hallucination's despawn particles failed: " + e.Message); }
            }
            Destroy(gameObject, noDespawnEffect ? ExplodedLinger : DespawnSeconds);
        }

        void VanishUpdate()
        {
            if (_quiet || _noDespawnEffect) return;
            float t = Time.time - _vanishAt;
            // The weapon has no dissolve: it shrinks away while the body dissolves.
            if (_weaponGo != null && _weaponGo.activeSelf)
            {
                float k = Mathf.Clamp01(1f - t / Mathf.Min(WeaponShrinkSeconds, _hideAt));
                _weaponGo.transform.localScale = _weaponScale * k;
                if (k <= 0f) _weaponGo.SetActive(false);
            }
            if (!_hidden && t >= _hideAt) HideAll();
        }

        /// <summary>The dissolve keyword on the body's materials (ProtoActor.TurnOnMaterialDissolve). .material gives this
        /// renderer its own copy (never the shared one, or every mimic would dissolve); the copies are ours to destroy.</summary>
        void EnableDissolve()
        {
            if (Puppet == null) return;
            foreach (var r in Puppet.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                var m = r.material;
                if (m == null || m.shader == null) continue;
                if (!_ownedMaterials.Contains(m)) _ownedMaterials.Add(m);
                if (m.shader.keywordSpace.FindKeyword("_DISSOLVESWITCH_ON").isValid) m.EnableKeyword("_DISSOLVESWITCH_ON");
            }
        }

        void HideAll()
        {
            _hidden = true;
            try { if (Puppet != null) Puppet.SetRenderersForceHidden(true); }
            catch (Exception) { /* below */ }
            if (Puppet != null)
                foreach (var r in Puppet.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r != null) r.enabled = false;
            foreach (var r in _weaponRenderers)
                if (r != null) r.enabled = false;
        }

        /// <summary>Radiation tints every actor for this player (vanilla symptom 5): ours too, or it would stand out.</summary>
        void ApplyTint()
        {
            if (_tint == null || _avatar == null) return;
            float pct = _avatar.ContaAppearancePercent;
            if (pct < 0f)
            {
                _tint.Apply(-1f, isIndoor: false);
                return;
            }
            var scene = _main as GamePlayScene;
            Vector3 p = transform.position;
            if (scene != null && scene.TryCheckPosIsIndoor(in p, out bool indoor)) _tint.Apply(Mathf.Clamp(pct, 0f, 100f), indoor);
        }

        void OnDestroy()
        {
            try { Victim.UnregisterPuppet(Puppet); }
            catch (Exception) { /* nothing to do */ }
            StopBeep();
            foreach (var m in _ownedMaterials)
                if (m != null) Destroy(m);
            _ownedMaterials.Clear();
            if (_phase != Phase.Vanish)
            {
                // Destroyed from outside (its level's BG root went away) without vanishing: Victim reports it.
                _phase = Phase.Vanish;
            }
        }
    }

    /// <summary>C4: clip events of our fake's puppet. Not core: without it a fake's fire/swing sounds come from FakeMimic's
    /// own timeline and the game logs an "Actor not found" error per such event.</summary>
    [HarmonyPatch]
    static class FakeEventPatch
    {
        // C4. Unity calls PuppetScript.OnAnimationEvent for every clip event of every puppet. A HashSet-cheap check first:
        // anything that isn't our fake runs vanilla.
        [HarmonyPrefix, HarmonyPatch(typeof(PuppetScript), nameof(PuppetScript.OnAnimationEvent))]
        static bool BeforeAnimationEvent(PuppetScript __instance, AnimationEvent evt)
        {
            if (!Control.ClientReady || !Victim.IsFake(__instance)) return true;
            try
            {
                var fake = __instance.GetComponentInParent<FakeMimic>();
                return fake == null || fake.OnAnimationEvent(evt != null ? evt.stringParameter : null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("C4", "A hallucination's animation event failed: " + e);
                return false;
            }
        }
    }
}
