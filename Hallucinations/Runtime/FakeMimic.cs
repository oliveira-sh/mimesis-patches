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

        /// <summary>Who moves it on this game.
        /// Ai: the target's game, on its own (the original behaviour).
        /// Remote: the target's game, following the poses of the dead player who steers it; it still decides here
        ///         whether its attack lands and whether the player hit it.
        /// Viewer: a dead player's game, showing it where its mover says.
        /// Driver: the steering dead player's game, moving it from their input.</summary>
        internal enum Mode { Ai, Remote, Viewer, Driver }

        const float RemoteSnapDistance = 6f;     // a pose this far off is a jump, not a step: snap
        const float RemoteFollowRate = 12f;      // 1/s, exponential approach to the last pose
        const float RemoteSlackSeconds = 3f;     // viewers/driver: it can't outlive its lifetime by more than this
        const float CamRootHeight = 1.55f;       // the steering player's orbit camera follows this point

        // ---- steering (Driver) ----
        const float KneeHeight = 0.5f;
        const float FloorProbeUp = 1f;           // the floor is looked for from this far above its feet...
        const float FloorProbeDown = 1.2f;       // ...down to this far below them
        const float NavFloorReach = 0.4f;        // a navmesh point this close (flat) stands in for a floor physics misses
        const float LostStepUp = 1.5f;           // with no floor under it at all, any floor this close up or down will do
        const float UnstickAfter = 0.75f;        // s pushed without moving before it's moved onto the nearest navmesh
        const float ProxyViewHalfAngle = 50f;    // a fake player "sees" this far either side of where it faces

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
        internal Mode CurrentMode => _mode;
        internal bool IsAttacking => _phase == Phase.Attack;
        internal Transform CamRoot { get; private set; }
        /// <summary>Where it is now, for its mover's Drive messages.</summary>
        internal Pose CurrentPose => new Pose(transform.position, transform.eulerAngles.y, _curMove);

        // ---- who moves it ----
        Mode _mode;
        Action<FakeMimic, Outcome> _onEnded;
        Action<FakeMimic> _onAttack;
        Action<FakeMimic> _onTakeOver;
        MoveKind _curMove;
        Vector3 _rPos;            // the last pose its mover sent (Remote, Viewer)
        float _rYaw;
        MoveKind _rMove;
        bool _rHas;
        float _lastRemoteAt;
        Vector3 _dDir;            // the steering player's input this frame (Driver)
        bool _dRun;
        float _blockedFor;        // Driver: how long it has been pushed without moving
        bool _blockedLogged;
        // Its target is a fake player (FakePlayers' dummy): the host's game plays that player's side (hits, AI). Never seen
        // or heard here: the dead players' copies (Spectate), the host's included, are what shows.
        bool _proxy;

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
        bool _hit, _fired, _exploded, _explosionFxShown, _quiet, _noDespawnEffect, _hidden, _attackEnded;
        readonly List<Vector3> _path = new List<Vector3>();
        int _pathIdx, _routeFails;
        readonly HashSet<string> _playedItemSfx = new HashSet<string>();

        // ======================================================================================== creation (9.1-9.2)

        /// <summary>Builds the fake at pos facing yaw and starts it. Null (and nothing left behind) when it can't be built.
        /// navOk false: the spot came from the physics fallback (no client navmesh), so it walks straight.
        /// onEnded: called exactly once when it ends (any reason); onAttack: when it starts an attack on its own (Ai).
        /// proxyTarget: a fake player whose side this game plays (Ai/Remote only): it goes after them, unseen here.</summary>
        internal static FakeMimic Create(GameMainBase main, int id, Weapon weapon, int seed, int ttlMs, Vector3 pos, float yaw,
            bool navOk, Mode mode, Action<FakeMimic, Outcome> onEnded, Action<FakeMimic> onAttack = null,
            Action<FakeMimic> onTakeOver = null, ProtoActor proxyTarget = null)
        {
            var spawn = new HostMsg { Id = id, Weapon = weapon, Seed = seed, TtlMs = ttlMs };
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
                // The navmesh point can be a little above or below the real floor: stand on the floor (within 0.5 m up,
                // 1 m down; from 1 m up when it's that far under it).
                Vector3 floor = PhysicsUtility.DropToFloor(pos, 0.5f, 1.5f);
                if (floor == pos) floor = PhysicsUtility.DropToFloor(pos, 1f, 1.5f);
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

                var camRoot = new GameObject("HallucinationCamRoot").transform;
                camRoot.SetParent(root.transform, false);
                camRoot.localPosition = Vector3.up * CamRootHeight;

                var fake = root.AddComponent<FakeMimic>();
                fake.Id = spawn.Id;
                fake.Puppet = puppet;
                fake.CamRoot = camRoot;
                fake._anim = anim;
                fake._main = main;
                fake._proxy = proxyTarget != null && (mode == Mode.Ai || mode == Mode.Remote);
                fake._avatar = fake._proxy ? proxyTarget : main != null ? main.GetMyAvatar() : null;
                fake._navOk = navOk;
                fake._tint = puppetGo.GetComponent<PuppetContaAppearance>();
                fake._mode = mode;
                fake._onEnded = onEnded;
                fake._onAttack = onAttack;
                fake._onTakeOver = onTakeOver;
                fake._rPos = pos;
                fake._rYaw = yaw;
                fake._rHas = mode == Mode.Viewer || mode == Mode.Driver;
                fake._lastRemoteAt = Time.time;
                Victim.RegisterPuppet(puppet);

                root.SetActive(true);
                fake.Setup(spawn);
                if (fake._proxy) fake.HideAll();
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
            if (_proxy) { _beepPending = false; return; }
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
                bool targetSide = _mode == Mode.Ai || _mode == Mode.Remote;
                // The target's game ends it on time; the others keep theirs a little longer (its Gone message ends it).
                if (now - _spawnedAt > _ttl + (targetSide ? 0f : RemoteSlackSeconds))
                {
                    Vanish(Outcome.Timeout);
                    return;
                }

                if (!targetSide)
                {
                    // A dead player's game: only show it (Viewer) or move it from input (Driver). No hit checks here.
                    if (_phase == Phase.Attack) AttackUpdate(dt, transform.position, transform.position, 0f);
                    else if (_mode == Mode.Driver) DriveStep(dt);
                    else FollowRemote(dt);
                    return;
                }
                if (_mode == Mode.Remote && _phase != Phase.Attack)
                {
                    // Steered by a dead player: no poses for a while (they dropped out) -> it runs on its own.
                    float limit = _rHas ? Const.SteerStaleSec : Const.SteerStartSec;
                    if (now - _lastRemoteAt > limit)
                    {
                        Log.Info($"Hallucination #{Id}: its dead player stopped steering it; it goes on its own");
                        SetMode(Mode.Ai);
                        try { _onTakeOver?.Invoke(this); }
                        catch (Exception e) { Log.ErrorOnce("fake-takeover", "Reporting the take-over failed: " + e); }
                    }
                }

                var cam = _proxy ? null : Camera.main;
                Vector3 me = _avatar.transform.position;
                Vector3 eye = cam != null ? cam.transform.position : me + Vector3.up * HeadHeight;
                Vector3 pos = transform.position;
                float dist = Placement.Flat(me - pos).magnitude;
                // Walking into it ends it, but not while a dead player steers it: they walk it up to the player to strike.
                if (_mode == Mode.Ai && _phase != Phase.Attack && dist < TouchDistance && Mathf.Abs(me.y - pos.y) < 1.5f)
                {
                    Log.Trace($"Hallucination #{Id}: you walked into it");
                    Vanish(Outcome.HitByPlayer);
                    return;
                }

                if (_mode == Mode.Remote)
                {
                    if (_phase == Phase.Attack) AttackUpdate(dt, me, eye, dist);
                    else FollowRemote(dt);
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
            if (_proxy)
            {
                // A fake player's: only that player matters (this game's own player may live, die or watch).
                if (_avatar == null) return "its fake player left";
                if (_avatar.dead) return "its fake player died";
                return Control.IsReplay ? "replay" : null;
            }
            var me = main.GetMyAvatar();
            if (me == null || !ReferenceEquals(me, _avatar)) return "your avatar changed";
            if (Control.IsReplay) return "replay";
            if (_mode == Mode.Viewer || _mode == Mode.Driver)
                return me.dead ? null : "you're alive again"; // only dead players see the others' hallucinations
            if (me.dead) return "you died";
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            if (cm != null && cm.IsSpectatorMode) return "spectating";
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
            _curMove = speed > WalkSpeed + 0.1f ? MoveKind.Run : MoveKind.Walk;
            return floorOk;
        }

        void StandStill()
        {
            Puppet.Move(Vector3.zero, WalkSpeed, RunSpeed); // eases to 0 (PuppetScript's velocity stickiness)
            Puppet.UpdateMoveType(ActorMoveType.Walk);
            _curMove = MoveKind.Idle;
        }

        // ================================================================================= steered / shown by others

        /// <summary>Remote, Viewer: glide to the last pose its mover sent; animate from the actual motion.</summary>
        void FollowRemote(float dt)
        {
            if (!_rHas || dt <= 0f)
            {
                StandStill();
                return;
            }
            Vector3 pos = transform.position;
            Vector3 next = (_rPos - pos).sqrMagnitude > RemoteSnapDistance * RemoteSnapDistance
                ? _rPos
                : Vector3.Lerp(pos, _rPos, 1f - Mathf.Exp(-RemoteFollowRate * dt));
            transform.position = next;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, _rYaw, 0f), TurnSpeed * dt);
            Vector3 vel = Placement.Flat(next - pos) / dt;
            if (_rMove == MoveKind.Idle && vel.magnitude < 0.3f)
            {
                StandStill();
                return;
            }
            float speed = _rMove == MoveKind.Run ? RunSpeed : WalkSpeed;
            // The pose's speed, in the direction it actually moves (a slow glide still walks, not slides).
            Vector3 dir = vel.sqrMagnitude > 0.0001f ? vel.normalized : transform.forward;
            Puppet.Move(dir * speed, WalkSpeed, RunSpeed);
            Puppet.UpdateMoveType(_rMove == MoveKind.Run ? ActorMoveType.Run : ActorMoveType.Walk);
            _curMove = _rMove;
        }

        /// <summary>Driver: one step of the steering player's input along the floor; walls and drops stop it (it slides
        /// along a wall when only one axis is blocked). Never stuck for good: where neither physics nor the navmesh finds a
        /// floor under it (it appeared under or inside something), any floor near enough will do, and if it still can't
        /// move it's put on the nearest navmesh point.</summary>
        void DriveStep(float dt)
        {
            Vector3 dir = Placement.Flat(_dDir);
            if (dir.sqrMagnitude < 0.01f || dt <= 0f)
            {
                StandStill();
                return;
            }
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            float speed = _dRun ? RunSpeed : WalkSpeed;
            Vector3 pos = transform.position;
            Vector3 step = dir * (speed * dt);
            bool lost = !FloorAt(pos, pos.y, MaxFloorStep, out _);
            // Straight on; else along the wall it ran into (what's left of the step once the part into the wall is taken
            // off, so it slides round corners and crates); else along either axis.
            if (TryStep(pos, step, lost, out Vector3 next, out string why, out Vector3 wall) ||
                (wall != Vector3.zero && TryStep(pos, Slide(step, wall), lost, out next, out _, out _)) ||
                TryStep(pos, new Vector3(step.x, 0f, 0f), lost, out next, out _, out _) ||
                TryStep(pos, new Vector3(0f, 0f, step.z), lost, out next, out _, out _))
            {
                _blockedFor = 0f;
                transform.position = next;
                Face(dir, dt);
                Puppet.Move(dir * speed, WalkSpeed, RunSpeed);
                Puppet.UpdateMoveType(_dRun ? ActorMoveType.Run : ActorMoveType.Walk);
                _curMove = _dRun ? MoveKind.Run : MoveKind.Walk;
                return;
            }
            StandStill();
            Face(dir, dt);
            _blockedFor += dt;
            if (_blockedFor < UnstickAfter) return;
            _blockedFor = 0f;
            if (!_blockedLogged)
            {
                _blockedLogged = true;
                Log.Info($"Hallucination #{Id}: can't go that way from {Fmt(pos)} ({why}{(lost ? "; no floor under it" : "")})");
            }
            if (lost) Unstick(pos);
        }

        /// <summary>A step is fine when nothing solid is in the way at knee and chest height and there's a floor within a
        /// stair step of where it stands (lost: within LostStepUp). why: what stopped it.</summary>
        bool TryStep(Vector3 pos, Vector3 step, bool lost, out Vector3 next, out string why, out Vector3 wall)
        {
            next = pos;
            why = null;
            wall = Vector3.zero;
            float len = step.magnitude;
            if (len < 0.0001f) { why = "no step"; return false; }
            Vector3 dir = step / len;
            float reach = len + 0.3f; // keep a body's width from walls
            if (Blocked(pos + Vector3.up * KneeHeight, dir, reach, "knee", out why, out wall)) return false;
            if (Blocked(pos + Vector3.up * ChestHeight, dir, reach, "chest", out why, out wall)) return false;
            Vector3 to = pos + step;
            if (!FloorAt(to, pos.y, (lost ? LostStepUp : MaxFloorStep) + len, out float y))
            {
                why = "no floor ahead (a drop, or a step too high)";
                return false;
            }
            next = new Vector3(to.x, y, to.z);
            return true;
        }

        static bool Blocked(Vector3 from, Vector3 dir, float dist, string what, out string why, out Vector3 wall)
        {
            why = null;
            wall = Vector3.zero;
            if (!Physics.Raycast(from, dir, out var hit, dist, Hub.DefaultOnlyLayerMask, QueryTriggerInteraction.Ignore)) return false;
            var c = hit.collider;
            why = $"{what} blocked by {(c != null ? c.name : "?")} (layer {(c != null ? LayerMask.LayerToName(c.gameObject.layer) : "?")}, {hit.distance:0.00} m)";
            wall = Placement.Flat(hit.normal);
            if (wall.sqrMagnitude > 0.0001f) wall.Normalize();
            return true;
        }

        /// <summary>step without its part into the wall (wall: the wall's flat normal), kept at step's length so it
        /// doesn't crawl along it.</summary>
        static Vector3 Slide(Vector3 step, Vector3 wall)
        {
            Vector3 along = step - wall * Vector3.Dot(step, wall);
            float len = along.magnitude;
            return len < 0.0001f ? Vector3.zero : along / len * step.magnitude;
        }

        /// <summary>The floor at (at.x, at.z) within tolerance of refY: what physics hits from a little above, else the
        /// client navmesh (built on every game) when its nearest point is right there.</summary>
        static bool FloorAt(Vector3 at, float refY, float tolerance, out float y)
        {
            y = refY;
            var from = new Vector3(at.x, refY + FloorProbeUp, at.z);
            if (Physics.Raycast(from, Vector3.down, out var hit, FloorProbeUp + FloorProbeDown, Hub.DefaultOnlyLayerMask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(hit.point.y - refY) <= tolerance)
            {
                y = hit.point.y;
                return true;
            }
            var nav = Hub.s != null ? Hub.s.navman : null;
            if (nav == null) return false;
            Vector3 n;
            try { n = nav.GetNearestPointOnNavMesh(at, 1f); }
            catch (Exception) { return false; }
            if (!NavMeshConstants.IsValid(n) || Placement.Flat(n - at).magnitude > NavFloorReach || Mathf.Abs(n.y - refY) > tolerance + 0.3f) return false;
            y = n.y;
            return true;
        }

        /// <summary>No floor under it and it can't step anywhere: onto the nearest navmesh point (3 m at most).</summary>
        void Unstick(Vector3 pos)
        {
            var nav = Hub.s != null ? Hub.s.navman : null;
            if (nav == null) return;
            Vector3 n;
            try { n = nav.GetNearestPointOnNavMesh(pos, 3f); }
            catch (Exception) { return; }
            if (!NavMeshConstants.IsValid(n)) return;
            if (FloorAt(n, n.y, MaxFloorStep, out float y)) n.y = y;
            transform.position = n;
            Log.Info($"Hallucination #{Id}: stuck where there's no floor; moved onto the floor {Vector3.Distance(pos, n):0.0} m away");
        }

        static string Fmt(Vector3 p) => $"({p.x:0.0}, {p.y:0.0}, {p.z:0.0})";

        /// <summary>Remote/Viewer: a new pose from its mover.</summary>
        internal void ApplyRemote(in Pose p)
        {
            if (_phase == Phase.Vanish) return;
            _rPos = p.Pos;
            _rYaw = p.Yaw;
            _rMove = p.Move;
            _rHas = true;
            _lastRemoteAt = Time.time;
        }

        /// <summary>Driver: the steering player's input for this frame (world direction on the ground, run).</summary>
        internal void SetDriveInput(Vector3 dir, bool run)
        {
            _dDir = dir;
            _dRun = run;
        }

        /// <summary>Its mover says it attacks now, facing yaw (Remote, Viewer), or the steering player clicked (Driver).
        /// False when it can't attack right now (already attacking or gone).</summary>
        internal bool AttackNow(float yaw)
        {
            if (_phase == Phase.Vanish || _phase == Phase.Attack) return false;
            if ((_mode == Mode.Remote || _mode == Mode.Viewer) && _rHas)
            {
                // Strike from where its mover struck (its last pose came just before the attack), not from where this
                // game's smoothing had got to: the delay would otherwise leave it short of the player.
                transform.position = _rPos;
            }
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (_mode == Mode.Remote && _avatar != null)
            {
                // Steered attacks aim at the player when they're in reach: the dead player aims with a third-person camera
                // that looks at the hallucination, not at the player, and sees them a moment late.
                Vector3 to = Placement.Flat(_avatar.transform.position - transform.position);
                if (to.sqrMagnitude > 0.01f && to.magnitude <= _spec.LandReach + 1.5f)
                    transform.rotation = Quaternion.LookRotation(to);
            }
            _rYaw = transform.eulerAngles.y;
            _lastRemoteAt = Time.time;
            StartAttack(Time.time);
            return true;
        }

        /// <summary>Changes who moves it (the dead player let go or dropped out: Ai on the target, Viewer on the dead
        /// player's game; or a viewer finds out it steers it).</summary>
        internal void SetMode(Mode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            _rPos = transform.position;
            _rYaw = transform.eulerAngles.y;
            _rHas = true;
            _lastRemoteAt = Time.time;
            _dDir = Vector3.zero;
            if (mode == Mode.Ai && _phase != Phase.Attack && _phase != Phase.Vanish)
            {
                _phase = Phase.Charge; // it already knows where the player is: straight at them
                _phaseAt = Time.time;
                _path.Clear();
                _pathIdx = 0;
                _routeFails = 0;
                _nextRouteAt = 0f;
            }
        }

        /// <summary>Viewer/Driver: the target's game says it's over (its Gone message).</summary>
        internal void EndShown(Outcome outcome)
        {
            if (_phase == Phase.Vanish) return;
            // Its attack's own vanish already ran on this game when the attack played here.
            Vanish(outcome, quiet: outcome == Outcome.Cancelled);
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
            if (_proxy && _avatar != null)
            {
                // A fake player has no camera: it sees ahead of where it faces, from its head.
                Vector3 head = _avatar.transform.position + Vector3.up * HeadHeight;
                Vector3 to = Placement.Flat(Chest - head);
                if (to.sqrMagnitude < 0.01f || Vector3.Angle(Placement.Flat(_avatar.transform.forward), to) > ProxyViewHalfAngle) return false;
                return Clear(head, Chest) || Clear(head, Head);
            }
            if (cam == null) return false;
            Placement.FrustumPlanes(cam.projectionMatrix * cam.worldToCameraMatrix, _planes);
            if (!Placement.InFrustum(_planes, transform.position + Vector3.up * 0.95f, new Vector3(0.4f, 0.95f, 0.4f))) return false;
            Vector3 eye = cam.transform.position;
            return PhysicsUtility.CheckBlockByWall(eye, Chest) || PhysicsUtility.CheckBlockByWall(eye, Head);
        }

        /// <summary>A clear line from its chest to the player (head or body).</summary>
        bool HasLine(Vector3 eye, Vector3 me) => Clear(Chest, eye) || Clear(Chest, me + Vector3.up * 1f);

        /// <summary>Nothing solid between a and b but the player's own body: the points aimed at (head, body) are inside
        /// it, and another player's avatar (a fake player's, on the host's game) has a capsule on the walls' layer.</summary>
        bool Clear(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.001f) return true;
            if (!Physics.Raycast(a, d / len, out var hit, len, Hub.DefaultOnlyLayerMask, QueryTriggerInteraction.Ignore)) return true;
            var actor = hit.collider != null ? hit.collider.GetComponentInParent<ProtoActor>() : null;
            return actor != null && ReferenceEquals(actor, _avatar);
        }

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
            if (_mode == Mode.Ai)
            {
                // On its own: the dead players who see it are told (the host forwards it).
                try { _onAttack?.Invoke(this); }
                catch (Exception e) { Log.ErrorOnce("fake-attack-cb", "Reporting the attack failed: " + e); }
            }
        }

        bool TargetSide => _mode == Mode.Ai || _mode == Mode.Remote;

        void AttackUpdate(float dt, Vector3 me, Vector3 eye, float dist)
        {
            _attackT += dt;
            if (!_hit && !_exploded)
            {
                // Only on its own does it keep closing in and turning to the player while it swings; steered, it strikes
                // where its dead player aimed it.
                if (_mode == Mode.Ai && _spec.MoveDuring && dist > 1.1f && HasLine(eye, me)) MoveTowards(me, WalkSpeed, dt);
                else
                {
                    StandStill();
                    if (_mode == Mode.Ai) FaceTowards(me, dt);
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
            // Only the target's game knows where the player really is: it decides, and reports it. Elsewhere the attack
            // just plays and it's gone.
            bool landed = TargetSide && InReach(transform.position, _spec.LandReach, _spec.HalfAngle);
            if (landed) HitAvatar(_spec.Effect);
            _attackEnded = true;
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
            bool landed = TargetSide && InReach(at, _spec.LandReach, 180f);
            if (landed) HitAvatar(_spec.Effect);
            _attackEnded = true;
            Vanish(landed ? Outcome.Landed : Outcome.Missed, noDespawnEffect: true);
        }

        bool SpawnExplosion(Vector3 at)
        {
            if (_proxy) return true; // nothing to see here: the fake player "saw" it
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
            var cam = _proxy ? null : Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : me + Vector3.up * HeadHeight;
            Vector3 origin = from + (from == transform.position ? Vector3.up * ChestHeight : Vector3.zero);
            return Clear(origin, eye) || Clear(origin, me + Vector3.up * 1f);
        }

        /// <summary>What a real hit looks and sounds like on this game (ProtoActor.ResolvePacket_HitTargetSig without the
        /// server): the effect's particles, screen effect, sound and flinch, and the hit feedback. Local only: no packet,
        /// no stat.</summary>
        void HitAvatar(int effectId)
        {
            if (_avatar == null || _proxy) return; // a fake player's hit is in its head only
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
            if (_proxy || _spec == null || _spec.Item == 0 || string.IsNullOrEmpty(key) || !_playedItemSfx.Add(key)) return;
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
            if (_proxy)
            {
                // Unseen and unheard: only the bomb's timing matters.
                if (statement.StartsWith("sfx:Bomb_Explosion_Body", StringComparison.Ordinal) && _weapon == Weapon.Bomb &&
                    _phase == Phase.Attack) Explode();
                return false;
            }
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
            // Its attack landed: it's gone the moment it hits, not dissolving through the rest of the swing. On a dead
            // player's game its attack is its end too (the target's game decides the rest).
            if (outcome == Outcome.Landed || (_attackEnded && !TargetSide)) _hideAt = LandedHideAt;
            try { _onEnded?.Invoke(this, outcome); }
            catch (Exception e) { Log.ErrorOnce("fake-ended", "Reporting a hallucination's end failed: " + e); }
            StopBeep();
            if (quiet)
            {
                Destroy(gameObject);
                return;
            }
            try { Puppet.Stop(); }
            catch (Exception) { /* cosmetic */ }
            if (!noDespawnEffect && !_proxy)
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
            if (_tint == null || _avatar == null || _proxy) return;
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
