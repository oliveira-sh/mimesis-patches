// MIMESIS Hallucinations - dead players: seeing the hallucinations, and steering the one you sent
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every dead player whose game runs this sees the living players' hallucinations: the host sends Show once a target's
// game placed one (or as soon as this player died, if it's already there), then its poses and attacks (Drive, Attack)
// and its end (Gone). They're shown here exactly like on the target's game, but they decide nothing: only the target's
// game knows where the player is, so it decides whether an attack lands and reports the end.
// The dead player who sent one steers it (Show with Ctl = this player): the spectator orbit camera follows it, WASD /
// left stick move it (camera-relative; Shift runs), FireWeapon (left click) makes it attack once (it vanishes with its
// attack, like the original), Interact (E) lets go (it then runs on its own on the target's game). This game moves it
// and sends its poses; the host forwards them to the target and the other dead players.
// Steering ends on every path (let go, its end, the camera taken by the game or SpectatorCam, revived, level left),
// and puts the camera, its input and the HUD tips back. The local copy then just shows it (Viewer).

using System;
using System.Collections.Generic;
using HarmonyLib;
using Mimic.Actors;
using Mimic.InputSystem;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;

namespace Hallucinations
{
    static class Spectate
    {
        const float StickDeadzone = 0.2f;
        const float TipInterval = 0.25f;
        const string AttackLabel = "Attack";
        const string RunLabel = "Run";
        const string LetGoLabel = "Let go";

        static readonly Dictionary<int, FakeMimic> _shown = new Dictionary<int, FakeMimic>();
        static readonly List<int> _scratch = new List<int>();

        // ---- steering (at most one) ----
        static FakeMimic _driven;
        static int _drivenId;
        static int _seq;
        static float _lastSentAt;
        static Pose _lastSent;
        static CinemachineCamera _cam;
        static CinemachineOrbitalFollow _orbit;
        static Transform _savedFollow, _savedLookAt;
        static BindingMode _savedBinding;
        static bool _bindingChanged;
        static float _nextTipAt;

        /// <summary>This game steers a hallucination right now.</summary>
        internal static bool Steering => _driven != null && _drivenId != 0;

        // ======================================================================================= host messages

        internal static void OnShow(in HostMsg m)
        {
            if (m.Id == 0 || _shown.ContainsKey(m.Id)) return;
            var scene = Hub.Main as GamePlayScene;
            var me = scene != null ? scene.GetMyAvatar() : null;
            if (scene == null || !scene.IsGameLogicRunning || me == null || !me.dead || Control.IsReplay) return;
            bool mine = m.Ctl != 0 && m.Ctl == me.ActorID && !Steering;
            var mode = mine ? FakeMimic.Mode.Driver : FakeMimic.Mode.Viewer;
            var fake = FakeMimic.Create(scene, m.Id, m.Weapon, m.Seed, m.TtlMs, m.Pose.Pos, m.Pose.Yaw, navOk: false, mode, OnFakeEnded);
            if (fake == null) return;
            _shown[m.Id] = fake;
            Log.Info($"Hallucination #{m.Id} on {Name(m.Target)} ({fake.Description}) " + (mine ? "is yours to steer" : "is shown to you"));
            if (mine) BeginSteering(fake, m.Id);
            else if (m.Ctl != 0 && m.Ctl == me.ActorID)
                ClientNet.Send(Protocol.Release(m.Id)); // already steering another one: this one runs on its own
        }

        internal static void OnDrive(in HostMsg m)
        {
            if (_shown.TryGetValue(m.Id, out var f) && f != null && f.CurrentMode == FakeMimic.Mode.Viewer) f.ApplyRemote(m.Pose);
        }

        internal static void OnAttack(in HostMsg m)
        {
            if (_shown.TryGetValue(m.Id, out var f) && f != null && f.CurrentMode == FakeMimic.Mode.Viewer) f.AttackNow(m.Pose.Yaw);
        }

        internal static void OnGone(in HostMsg m)
        {
            if (!_shown.TryGetValue(m.Id, out var f)) return;
            _shown.Remove(m.Id);
            if (m.Id == _drivenId) EndSteering("it's over: " + m.Outcome, sendRelease: false);
            if (f != null) f.EndShown(m.Outcome);
        }

        /// <summary>The target's game took it over (no poses from us for a while): we just watch it now.</summary>
        internal static void OnRelease(in HostMsg m)
        {
            if (m.Id != _drivenId) return;
            EndSteering("the target's game took it over", sendRelease: false);
        }

        static void OnFakeEnded(FakeMimic fake, Outcome outcome)
        {
            if (ReferenceEquals(fake, null)) return;
            if (fake.Id == _drivenId) EndSteering(outcome == Outcome.Cancelled ? "cut short" : "it attacked", sendRelease: false);
            if (_shown.TryGetValue(fake.Id, out var f) && ReferenceEquals(f, fake)) _shown.Remove(fake.Id);
        }

        // ============================================================================================ every frame

        internal static void Tick()
        {
            if (_shown.Count > 0)
            {
                // Destroyed behind our back (level unload): forget them.
                _scratch.Clear();
                foreach (var kv in _shown)
                    if (kv.Value == null) _scratch.Add(kv.Key);
                foreach (int id in _scratch)
                {
                    _shown.Remove(id);
                    if (id == _drivenId) EndSteering("gone", sendRelease: false);
                }
                _scratch.Clear();
            }
            if (!Steering) return;
            string why = Watchdog();
            if (why != null)
            {
                EndSteering(why, sendRelease: true);
                return;
            }
            Drive();
        }

        /// <summary>Why steering must end now, or null.</summary>
        static string Watchdog()
        {
            var main = Hub.Main;
            var me = main != null ? main.GetMyAvatar() : null;
            if (me == null) return "level left";
            if (!me.dead) return "you're alive again";
            if (_driven.IsVanishing) return "it's gone";
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            if (cm == null || cm.Mode != CameraManager.CameraMode.PlayerSpectate) return "the camera changed";
            if (_cam == null || !ReferenceEquals(cm.spectatorCamera, _cam)) return "the camera changed";
            // The game moved the camera to someone (its target died, auto-switch) or SpectatorCam's free camera took it.
            if (_cam.Follow != _driven.CamRoot) return "the camera left it";
            return null;
        }

        static void Drive()
        {
            var input = Hub.s != null ? Hub.s.inputman : null;
            bool allowed = input != null && SpectatorInput.InputAllowed(input);
            Vector3 dir = Vector3.zero;
            bool run = false, attack = false;
            if (allowed)
            {
                Vector2 move = ReadMove(input);
                var cam = Camera.main;
                float yaw = cam != null ? cam.transform.eulerAngles.y : _driven.transform.eulerAngles.y;
                dir = Quaternion.Euler(0f, yaw, 0f) * new Vector3(move.x, 0f, move.y);
                run = input.isPressed(InputAction.Run);
                attack = input.wasPressedThisFrame(InputAction.FireWeapon);
            }
            _driven.SetDriveInput(dir, run);

            if (attack && !_driven.IsAttacking)
            {
                // Faces where the player looks (the camera), not only where it walked.
                var cam = Camera.main;
                float yaw = cam != null ? cam.transform.eulerAngles.y : _driven.transform.eulerAngles.y;
                SendPose(force: true);
                if (_driven.AttackNow(yaw))
                {
                    ClientNet.Send(Protocol.Attack(_drivenId, yaw));
                    Log.Info($"Hallucination #{_drivenId}: you attack");
                }
                return;
            }
            SendPose(force: false);
            UpdateTips();
        }

        /// <summary>Its pose to the host: often while it moves, rarely while it stands (also a "still steering" sign).</summary>
        static void SendPose(bool force)
        {
            float now = Time.unscaledTime;
            var pose = _driven.CurrentPose;
            bool changed = pose.Move != _lastSent.Move || (pose.Pos - _lastSent.Pos).sqrMagnitude > 0.0004f ||
                           Mathf.Abs(Mathf.DeltaAngle(pose.Yaw, _lastSent.Yaw)) > 2f;
            float interval = pose.Move == MoveKind.Idle && !changed ? Const.IdleSendInterval : Const.MoveSendInterval;
            if (!force && now - _lastSentAt < interval) return;
            _lastSentAt = now;
            _lastSent = pose;
            ClientNet.Send(Protocol.Drive(_drivenId, pose, ++_seq));
        }

        /// <summary>Like ProtoActor.GetMovementInput: move actions plus the left stick, at most length 1.</summary>
        static Vector2 ReadMove(InputManager input)
        {
            var m = Vector2.zero;
            if (input.isPressed(InputAction.MoveForward)) m.y += 1f;
            if (input.isPressed(InputAction.MoveBackward)) m.y -= 1f;
            if (input.isPressed(InputAction.MoveLeft)) m.x -= 1f;
            if (input.isPressed(InputAction.MoveRight)) m.x += 1f;
            if (Hub.GetGamepad() != null)
            {
                var stick = input.gamepadLeftStick;
                if (stick.sqrMagnitude >= StickDeadzone * StickDeadzone) m += stick;
            }
            if (m.sqrMagnitude > 1f) m.Normalize();
            return m;
        }

        /// <summary>C2 (E while dead): while steering, E lets go. True = E was ours this frame.</summary>
        internal static bool OnInteract()
        {
            if (!Steering) return false;
            var input = Hub.s != null ? Hub.s.inputman : null;
            if (input == null || !input.wasPressedThisFrame(InputAction.Interact) || !SpectatorInput.InputAllowed(input)) return true;
            EndSteering("you let go", sendRelease: true);
            return true;
        }

        // ============================================================================================ camera

        static void BeginSteering(FakeMimic fake, int id)
        {
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            var cam = cm != null ? cm.spectatorCamera : null;
            if (cm == null || cam == null || cm.Mode != CameraManager.CameraMode.PlayerSpectate || cam.Follow == null)
            {
                // No spectator camera to put on it (or SpectatorCam's free camera has it): it runs on its own.
                fake.SetMode(FakeMimic.Mode.Viewer);
                ClientNet.Send(Protocol.Release(id));
                Log.Info($"Hallucination #{id}: your camera isn't free to follow it, so it goes on its own");
                return;
            }
            _driven = fake;
            _drivenId = id;
            _seq = 0;
            _lastSentAt = -999f;
            _lastSent = default;
            _cam = cam;
            _savedFollow = cam.Follow;
            _savedLookAt = cam.LookAt;
            cam.Follow = fake.CamRoot;
            cam.LookAt = fake.CamRoot;
            // World-space orbit: otherwise the orbit turns with it, and camera-relative WASD turns it, which turns the
            // camera... a spin loop.
            _bindingChanged = false;
            _orbit = cam.GetComponent<CinemachineOrbitalFollow>();
            if (_orbit != null)
            {
                _savedBinding = _orbit.TrackerSettings.BindingMode;
                if (_savedBinding != BindingMode.WorldSpace)
                {
                    _orbit.TrackerSettings.BindingMode = BindingMode.WorldSpace;
                    _bindingChanged = true;
                }
            }
            else Log.WarnOnce("no-orbit", "The spectator camera has no orbit component (game update?): it follows without orbiting.");
            _nextTipAt = 0f;
            UpdateTips();
            SendPose(force: true);
            Log.Info($"Steering hallucination #{id}: WASD moves it, Shift runs, left click attacks (once), E lets go");
        }

        static void EndSteering(string why, bool sendRelease)
        {
            if (_drivenId == 0) return;
            int id = _drivenId;
            var fake = _driven;
            _driven = null;
            _drivenId = 0;
            if (sendRelease) ClientNet.Send(Protocol.Release(id));
            if (fake != null && !fake.IsVanishing) fake.SetMode(FakeMimic.Mode.Viewer);
            RestoreCamera(fake);
            RestoreTips();
            Log.Info($"Stopped steering hallucination #{id}: {why}");
        }

        static void RestoreCamera(FakeMimic fake)
        {
            var cam = _cam;
            var orbit = _orbit;
            _cam = null;
            _orbit = null;
            if (cam == null) return; // destroyed with the level: nothing to restore
            if (_bindingChanged && orbit != null) orbit.TrackerSettings.BindingMode = _savedBinding;
            _bindingChanged = false;
            // Only take the camera back if it still follows the fake (the game may have moved it already).
            if (fake != null && cam.Follow == fake.CamRoot)
            {
                if (_savedFollow != null)
                {
                    cam.Follow = _savedFollow;
                    cam.LookAt = _savedLookAt;
                }
                else
                {
                    cam.Follow = null;
                    cam.LookAt = null;
                }
            }
            var t = cam.transform;
            cam.ForceCameraPosition(t.position, t.rotation);
        }

        // ============================================================================================ HUD tips

        static void UpdateTips()
        {
            float now = Time.unscaledTime;
            if (now < _nextTipAt) return;
            _nextTipAt = now + TipInterval;
            var main = Hub.Main;
            var ui = main != null ? main.spectatorui : null;
            if (ui == null || !ui.IsShow) return;
            try
            {
                ui.SetActiveForUIPrefab(ui.UE_UI_GameTip_Spectator_Prev.gameObject, true);
                ui.SetActiveForUIPrefab(ui.UE_UI_GameTip_Spectator_Next.gameObject, true);
                ui.SetActiveForUIPrefab(ui.UE_UI_GameTip_Spectator_Possession.gameObject, true);
                SetTip(ui, ui.PrevGameTip, AttackLabel, InputAction.FireWeapon);
                SetTip(ui, ui.NextGameTip, RunLabel, InputAction.Run);
                SetTip(ui, ui.PossessionGameTip, LetGoLabel, InputAction.Interact);
            }
            catch (Exception e) { Log.ErrorOnce("steer-tips", "Showing the steering tips failed: " + e); }
        }

        static void SetTip(UIPrefab_Spectator ui, UIPrefab_GameTip tip, string text, InputAction action)
        {
            if (tip == null || tip.UE_UI_GuideText == null) return;
            if (tip.UE_UI_GuideText.text == text) return;
            tip.UE_UI_GuideText.SetText(text);
            ui.SetGameTipKeyIcons(tip, action); // the player's own binds, keyboard or pad icons
        }

        /// <summary>The game's tips back (texts and key icons); the possession tip goes back to SpectatorInput / vanilla.</summary>
        static void RestoreTips()
        {
            try
            {
                var main = Hub.Main;
                var ui = main != null ? main.spectatorui : null;
                if (ui == null) return;
                ui.UE_UI_GameTip_Spectator_Possession.gameObject.SetActive(false);
                ui.RefreshGameTips();
                SpectatorInput.OnSteeringEnded();
            }
            catch (Exception e) { Log.ErrorOnce("steer-tips-restore", "Restoring the spectator tips failed: " + e); }
        }

        // ============================================================================================ reset

        /// <summary>A new level (or left one): everything shown here goes at once.</summary>
        internal static void Reset()
        {
            if (Steering) EndSteering("level left", sendRelease: false);
            _driven = null;
            _drivenId = 0;
            foreach (var f in _shown.Values)
                if (f != null) f.Cancel("level left");
            _shown.Clear();
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
    }

    /// <summary>C7: while steering, the spectator's own keys (next/previous player on A/D/clicks, zoom) stay off: they
    /// move and attack with the hallucination instead. Not core: without it a click also switches the watched player,
    /// which ends steering (the camera left it).</summary>
    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.UpdateSpectatorCameraInput))]
    static class SteerSpectatorInputPatch
    {
        static bool Prefix()
        {
            if (!Control.SpectatorReady || !Spectate.Steering) return true;
            return false;
        }
    }
}
