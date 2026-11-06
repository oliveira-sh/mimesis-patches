// MIMESIS SpectatorCam - free camera around the spectated player
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// While spectating, F (keyboard) or R3 (gamepad) detaches the camera:
//   move WASD / left stick, up E or Space / RB, down Q or Ctrl / LB, look mouse / right stick, fast Shift / L3.
// Fairness limits: the camera stays within MaxDistance of the spectated player and never passes a wall
// between it and that player (checked from the player's head), so it only shows what is around them.
// Press F / R3 again, or switch target, to return to the normal orbit camera.

using System.Collections.Generic;
using HarmonyLib;
using Unity.Cinemachine;
using UnityEngine;
using Mimic.Actors;
using UnityEngine.InputSystem;

namespace SpectatorCam
{
    [HarmonyPatch(typeof(CameraManager))]
    static class FreeCamPatch
    {
        const float MaxDistance = 8f;
        const float WallMargin = 0.25f;
        const float Speed = 3.5f, FastSpeed = 7f;
        const float MouseSensitivity = 0.08f, StickSensitivity = 120f;

        /// <summary>Free camera on/off (Patches window).</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt("medovanx.SpectatorCam.Enabled", 1) == 1;
            set { PlayerPrefs.SetInt("medovanx.SpectatorCam.Enabled", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        static bool _free;
        static float _yaw, _pitch;
        static readonly List<Behaviour> Disabled = new List<Behaviour>();

        // Runs every frame from CameraManager.Update. While free it replaces the game's spectator input,
        // since A/D would otherwise switch targets.
        [HarmonyPrefix, HarmonyPatch("UpdateSpectatorCameraInput")]
        static bool Update(CameraManager __instance)
        {
            var cam = __instance.spectatorCamera;
            if (!Enabled)
            {
                if (_free) Exit(cam, __instance.TryGetCurrentSpectatorTarget(out var t) ? t : null);
                return true;
            }
            if (cam == null || !__instance.TryGetCurrentSpectatorTarget(out var target) || target == null)
            {
                if (_free) Exit(cam, null);
                return true;
            }
            if (Hub.s.uiman != null && Hub.s.uiman.isGameMenuOpen) return !_free;
            // The game also ignores spectator input while its F9 feedback form (text fields) is open.
            if (Hub.s.feedbackMan != null && Hub.s.feedbackMan.feedbackUI != null && Hub.s.feedbackMan.feedbackUI.IsOpen) return !_free;

            var kb = Keyboard.current;
            var pad = Gamepad.current;
            if ((kb != null && kb.fKey.wasPressedThisFrame) || (pad != null && pad.rightStickButton.wasPressedThisFrame))
            {
                if (_free) Exit(cam, target); else Enter(cam);
            }
            if (!_free) return true;

            Move(cam.transform, target.FpvCameraRoot, target.transform, kb, pad);
            return false;
        }

        // CameraManager.Update now also reads Interact (E by default) while spectating, to possess a mimic or end it.
        // While free the keyboard flies the camera (E = up), so its keys don't do that; F back to orbit first.
        static readonly Key[] FreeKeys = { Key.W, Key.A, Key.S, Key.D, Key.E, Key.Q, Key.Space, Key.LeftCtrl, Key.LeftShift };

        [HarmonyPrefix, HarmonyPatch("UpdateMimicPossession")]
        static bool BeforePossession()
        {
            var kb = Keyboard.current;
            if (!_free || kb == null) return true;
            foreach (var k in FreeKeys) if (kb[k].wasPressedThisFrame) return false;
            return true;
        }

        // Switching targets (or the game resetting the camera) returns to orbit mode first.
        [HarmonyPrefix, HarmonyPatch("SetupSpectatorCamera")]
        static void BeforeTargetChange(CameraManager __instance)
        {
            if (_free) Exit(__instance.spectatorCamera, null);
        }

        static void Enter(CinemachineCamera cam)
        {
            var view = Camera.main != null ? Camera.main.transform : cam.transform;
            cam.transform.SetPositionAndRotation(view.position, view.rotation);
            var e = view.rotation.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;

            // Turn off Cinemachine's follow/orbit/aim and its input so we drive the camera transform directly.
            Disabled.Clear();
            foreach (var b in cam.GetComponents<Behaviour>())
                if (b != cam && b.enabled) { b.enabled = false; Disabled.Add(b); }
            cam.Follow = cam.LookAt = null;
            _free = true;
        }

        static void Exit(CinemachineCamera cam, ProtoActor target)
        {
            foreach (var b in Disabled) if (b != null) b.enabled = true;
            Disabled.Clear();
            if (cam != null && target != null) cam.Follow = cam.LookAt = target.FpvCameraRoot;
            _free = false;
        }

        static void Move(Transform cam, Transform head, Transform body, Keyboard kb, Gamepad pad)
        {
            Vector2 look = Vector2.zero, move = Vector2.zero;
            float up = 0f;
            bool fast = false;
            if (Mouse.current != null) look += Mouse.current.delta.ReadValue() * MouseSensitivity;
            if (kb != null)
            {
                move += new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0), (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
                up += (kb.eKey.isPressed || kb.spaceKey.isPressed ? 1 : 0) - (kb.qKey.isPressed || kb.leftCtrlKey.isPressed ? 1 : 0);
                fast |= kb.leftShiftKey.isPressed;
            }
            if (pad != null)
            {
                look += pad.rightStick.ReadValue() * StickSensitivity * Time.unscaledDeltaTime;
                move += pad.leftStick.ReadValue();
                up += (pad.rightShoulder.isPressed ? 1 : 0) - (pad.leftShoulder.isPressed ? 1 : 0);
                fast |= pad.leftStickButton.isPressed;
            }

            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - look.y, -85f, 85f);
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            var dir = rot * new Vector3(move.x, 0f, move.y) + Vector3.up * up;
            var pos = cam.position + Vector3.ClampMagnitude(dir, 1f) * (fast ? FastSpeed : Speed) * Time.unscaledDeltaTime;

            // Stay near the spectated player and on their side of any wall.
            var origin = head.position;
            var offset = Vector3.ClampMagnitude(pos - origin, MaxDistance);
            pos = origin + offset;
            float dist = offset.magnitude;
            if (dist > 0.01f)
            {
                foreach (var hit in Physics.SphereCastAll(origin, 0.15f, offset / dist, dist, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform.IsChildOf(body)) continue;
                    var limit = Mathf.Max(hit.distance - WallMargin, 0f);
                    if (limit < dist) { dist = limit; pos = origin + offset.normalized * dist; }
                }
            }
            cam.SetPositionAndRotation(pos, rot);
        }
    }
}
