// MIMESIS MimicControl - possessor side: hello, camera, input, hints
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The possessor's game never moves the mimic itself: the host does (HostPatches.cs), and this game sees it move like
// any other mimic. This side only:
//   C1/C2  says hello to the host once per death and right before every possession request,
//   C3     takes the host's ack ("you control mimic X", repeated every second while it lasts),
//   C4     switches the spectator orbit camera to world-space binding (otherwise the orbit turns with the mimic, and
//          camera-relative WASD turns the mimic, which turns the camera... a spin loop), reads WASD / left stick, Run
//          and FireWeapon through the game's InputManager (key rebinds and gamepad for free) and sends them,
//   C5     shows the controls on the spectator HUD,
//   C6     and puts everything back when control ends, from whichever path ends it first. Teardown is idempotent: a
//          release delivers both CancelPossessingSig and EndPossessingSig, and the per-frame watchdog runs too.
// Nothing is ever sent unless Control.IsSupportedByHost (a host without MimicControl, or with control off, keeps
// possession exactly vanilla), and control input only goes out for the mimic the host acked.
// What the client-driven plan needed (local movement, echo filtering, freezing during the grab, collider changes)
// doesn't exist here: the mimic stays a normal network-driven actor on this game too.

using System;
using HarmonyLib;
using Mimic.Actors;
using Mimic.InputSystem;
using ReluProtocol;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;

namespace MimicControl
{
    static class ClientControl
    {
        // ---- possessor-only tuning (Const holds the shared possessor timings) ----
        /// <summary>Client-side spam guard for grab clicks (the host has its own cooldown and refuses silently).</summary>
        const float GrabSendInterval = 0.5f;
        /// <summary>Gamepad left stick dead zone (the keyboard is digital).</summary>
        const float StickDeadzone = 0.2f;
        /// <summary>Re-send right away when the walking direction turns this much (degrees)...</summary>
        const float DirChangeAngle = 8f;
        /// <summary>...or, standing still, when the camera yaw moves this much (the host turns an idle mimic to it).</summary>
        const float IdleYawChangeAngle = 5f;
        /// <summary>How often the HUD tips and the hidden 12 s bar are checked (the game can rewrite them).</summary>
        const float UiInterval = 0.25f;

        const string GrabLabel = "Grab", RunLabel = "Run", ReleaseLabel = "Let go";

        static GameObject _driver;

        // ---- state (all main thread; times are Time.unscaledTime) ----
        static int _ackMimic;                    // mimic of the last ack, 0 = none
        static float _ackAt;
        static int _mimic;                       // mimic this game controls now, 0 = none
        static ProtoActor _mimicActor;
        static int _beginFailedFor;              // a mimic Begin threw for: don't retry it every second
        static bool _helloSentThisDeath;

        static CinemachineCamera _cam;
        static CinemachineOrbitalFollow _orbit;
        static BindingMode _savedBinding;
        static bool _bindingChanged;

        static int _seq;
        static float _lastSendAt, _lastYaw, _nextAttackAt;
        static Vector3 _lastDir;
        static bool _lastRun, _lastMoving;

        static bool _mimicBusy;                  // the mimic plays a skill (the grab): no countdown yet
        static float _grabReadyAt;               // estimated end of the host's attack cooldown (HUD hint only)
        static float _nextUiAt;

        /// <summary>Called once from Plugin.Init (Hub.Awake): the per-frame driver, alive for the whole game.</summary>
        public static void Install()
        {
            if (_driver != null) return;
            _driver = new GameObject("MedovanxMimicControl");
            UnityEngine.Object.DontDestroyOnLoad(_driver);
            _driver.AddComponent<ClientDriver>();
        }

        /// <summary>This game drives a mimic right now and the camera is still on it (HUD patches).</summary>
        internal static bool Controlling =>
            _mimic != 0 && Hub.s != null && Hub.s.cameraman != null && Hub.s.cameraman.Mode == CameraManager.CameraMode.MimicPossession;

        /// <summary>Every frame (ClientDriver.Update).</summary>
        public static void Tick()
        {
            var main = Hub.Main;
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            if (main == null || cm == null)
            {
                _helloSentThisDeath = false;
                _beginFailedFor = 0;
                Teardown("left the level");
                return;
            }
            var me = main.GetMyAvatar();
            if (me == null || !me.dead)
            {
                // A new death (or a new level, which spawns a new avatar alive) says hello again.
                _helloSentThisDeath = false;
                _beginFailedFor = 0;
                Teardown("alive");
                return;
            }

            bool supported = Control.IsSupportedByHost;
            // Once the spectator view is up, not at the moment of death: the host answers with a cooldown sync, and the
            // game's handler starts a coroutine on the spectator HUD, which stays inactive during the death animation
            // (Unity logs an error). Possession needs the spectator view anyway, and the host counts the delay rule
            // from the death itself.
            if (supported && !_helloSentThisDeath && cm.IsSpectatorMode) SendHello("died");

            float now = Time.unscaledTime;
            if (_mimic == 0)
            {
                // C4: the vanilla possession started (camera on the mimic) and the host acked that very mimic.
                if (supported && _ackMimic != 0 && _ackMimic != _beginFailedFor && now - _ackAt < Const.AckTimeout
                    && cm.Mode == CameraManager.CameraMode.MimicPossession && cm.SpectatorTargetActorID == _ackMimic)
                    Begin(main, cm, _ackMimic, now);
                return;
            }

            // C4': the watchdog. Every way control can end without the hooks seeing it ends here within a frame.
            var reason = WatchdogReason(main, cm, supported, now);
            if (reason != null)
            {
                Teardown(reason);
                return;
            }

            try { Drive(main, cm, now); }
            catch (Exception e) { Log.ErrorOnce("drive", "Reading the mimic controls failed: " + e); }
        }

        static string WatchdogReason(GameMainBase main, CameraManager cm, bool supported, float now)
        {
            if (!supported) return "the host doesn't support it any more";
            if (cm.Mode != CameraManager.CameraMode.MimicPossession) return "possession ended";
            if (cm.SpectatorTargetActorID != _mimic) return "the camera left the mimic";
            if (_mimicActor == null || _mimicActor.dead || !ReferenceEquals(main.GetActorByActorID(_mimic), _mimicActor)) return "the mimic is gone";
            if (now - _ackAt > Const.AckTimeout) return "the host stopped acknowledging";
            // Revive and level end destroy the spectator camera (a new one comes with the next death).
            if (_cam == null || !ReferenceEquals(cm.spectatorCamera, _cam)) return "the camera was replaced";
            return null;
        }

        // ---------------- hello and ack ----------------

        static void SendHello(string why)
        {
            _helloSentThisDeath = true;
            if (Send(Protocol.Hello())) Log.Info($"Hello sent to the host ({why}, protocol {Const.ProtocolVersion})");
        }

        /// <summary>C3: the host's ack (and its per-second repeats). Returns true if the sig was one.</summary>
        static bool OnAck(SyncPossessorInfoSig sig)
        {
            var hub = Hub.s;
            if (hub == null || hub.pdata == null || !Protocol.IsAck(sig, hub.pdata.MyActorID)) return false;
            int mimic = sig.possessedActorID;
            if (_mimic != 0 && _mimic != mimic) Teardown("the host acked another mimic");
            if (_ackMimic != mimic) Log.Info($"Host: you control mimic {mimic}");
            _ackMimic = mimic;
            _ackAt = Time.unscaledTime;
            // What vanilla's handler would have done with this sig, minus feeding the magic number to the mimic's
            // 12 s bar: the cooldown it carries is the real one (0 while possessing).
            if (hub.cameraman != null) hub.cameraman.SetLeftPossessionTime(0L, sig.possessorLeftPossessingCooltime);
            return true;
        }

        // ---------------- begin / teardown ----------------

        static void Begin(GameMainBase main, CameraManager cm, int mimicId, float now)
        {
            var actor = main.GetActorByActorID(mimicId);
            var cam = cm.spectatorCamera;
            if (actor == null || actor.dead || cam == null) return; // not there yet: next frame (until the ack goes stale)

            _mimic = mimicId;
            _mimicActor = actor;
            _cam = cam;
            try
            {
                SwitchCamera(cam);
                Control.SetActiveFlag(true);

                _seq = 0;
                _lastSendAt = -100f;
                _lastDir = Vector3.zero;
                _lastRun = _lastMoving = false;
                _lastYaw = float.NaN;
                _nextAttackAt = 0f;
                _mimicBusy = false;
                // The host lets a freshly taken mimic grab at once (an AI grab still running shows as busy, and the
                // countdown starts at its end, as after any grab).
                _grabReadyAt = now;
                _nextUiAt = now + UiInterval;

                // One idle input right away: the host's lease and idle facing start from it. No camera: keep its heading
                // (the wire has no "no yaw").
                float yaw = CameraYaw(cm);
                if (float.IsNaN(yaw)) yaw = actor.transform.eulerAngles.y;
                SendInput(Vector3.zero, false, yaw, now, false);

                // Cosmetic, on the game's private UI fields: a failure here costs the hints, not the control (the host
                // has already taken the mimic over; giving up would leave it frozen until the lease runs out).
                try
                {
                    HideTimerBar(actor);
                    ApplyTips(main.spectatorui);
                }
                catch (Exception e) { Log.ErrorOnce("begin-hud", "Showing the control tips failed: " + e); }
                Log.Info($"Controlling mimic {mimicId}" + (_bindingChanged ? " (camera orbit now world-space)" : ""));
            }
            catch (Exception e)
            {
                Log.ErrorOnce("begin", "Couldn't start controlling the mimic: " + e);
                _beginFailedFor = mimicId;
                Teardown("start failed");
            }
        }

        /// <summary>Ends control and puts back everything Begin changed. Safe to call any number of times, from any
        /// hook: it does nothing (but forget the ack) when not controlling.</summary>
        static void Teardown(string reason)
        {
            _ackMimic = 0;
            if (_mimic == 0) return;
            int mimic = _mimic;
            _mimic = 0; // first: the HUD postfixes that RefreshGameTips triggers must see "not controlling"

            try { RestoreCamera(); }
            catch (Exception e) { Log.ErrorOnce("restore-camera", "Couldn't restore the spectator camera: " + e); }
            _cam = null;
            _orbit = null;
            _mimicActor = null;
            Control.SetActiveFlag(false);

            try { RestoreTips(); }
            catch (Exception e) { Log.ErrorOnce("restore-tips", "Couldn't restore the spectator tips: " + e); }

            Log.Info($"Stopped controlling mimic {mimic}: {reason}");
        }

        // ---------------- camera ----------------

        static void SwitchCamera(CinemachineCamera cam)
        {
            _bindingChanged = false;
            _orbit = cam.GetComponent<CinemachineOrbitalFollow>();
            if (_orbit == null)
            {
                Log.WarnOnce("no-orbit", "The spectator camera has no orbit component (game update?): the camera stays as it is.");
                return;
            }
            _savedBinding = _orbit.TrackerSettings.BindingMode;
            if (_savedBinding == BindingMode.WorldSpace) return;
            _orbit.TrackerSettings.BindingMode = BindingMode.WorldSpace;
            _bindingChanged = true;
            // The orbit's axes were relative to the mimic's heading; re-infer them from where the camera is now (world
            // space), so the view doesn't jump.
            var t = cam.transform;
            cam.ForceCameraPosition(t.position, t.rotation);
        }

        static void RestoreCamera()
        {
            if (!_bindingChanged) return;
            _bindingChanged = false;
            var orbit = _orbit;
            var cam = _cam;
            if (orbit == null || cam == null) return; // destroyed with the level or by a revive: nothing to restore
            orbit.TrackerSettings.BindingMode = _savedBinding;
            // Still orbiting the mimic (teardown before the game moved the camera): re-infer the axes relative to its
            // heading again, so this view doesn't jump either. After the game switched targets it's a cut anyway.
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            if (cm != null && ReferenceEquals(cm.spectatorCamera, cam) && _mimicActor != null && cam.Follow == _mimicActor.FpvCameraRoot)
            {
                var t = cam.transform;
                cam.ForceCameraPosition(t.position, t.rotation);
            }
            Log.Info("Camera orbit binding restored");
        }

        /// <summary>The direction the player looks, in degrees (Unity yaw), or NaN without a camera.</summary>
        static float CameraYaw(CameraManager cm)
        {
            var main = Camera.main;
            Transform t = main != null ? main.transform : cm.spectatorCamera != null ? cm.spectatorCamera.transform : null;
            return t != null ? t.eulerAngles.y : float.NaN;
        }

        // ---------------- input -> packets ----------------

        static void Drive(GameMainBase main, CameraManager cm, float now)
        {
            var input = Hub.s.inputman;
            bool allowed = input != null && InputAllowed(input);
            Vector2 move = Vector2.zero;
            bool run = false, grab = false;
            if (allowed)
            {
                move = ReadMove(input);
                run = input.isPressed(InputAction.Run);
                grab = input.wasPressedThisFrame(InputAction.FireWeapon);
            }

            float yaw = CameraYaw(cm);
            if (float.IsNaN(yaw))
            {
                // No camera to be relative to: stand still and keep facing (the wire has no "no yaw").
                yaw = _mimicActor.transform.eulerAngles.y;
                move = Vector2.zero;
            }
            var dir = Quaternion.Euler(0f, yaw, 0f) * new Vector3(move.x, 0f, move.y);
            bool moving = dir.sqrMagnitude > Protocol.MinDirSqr;

            float since = now - _lastSendAt;
            bool changed = moving != _lastMoving
                || (moving && (run != _lastRun || Vector3.Angle(dir, _lastDir) > DirChangeAngle))
                || (!moving && !float.IsNaN(_lastYaw) && Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) > IdleYawChangeAngle);
            if ((changed && since >= Const.ChangeSendInterval) || (moving && since >= Const.MoveSendInterval) || since >= Const.HeartbeatInterval)
                SendInput(dir, run, yaw, now, moving);

            if (grab && now >= _nextAttackAt)
            {
                _nextAttackAt = now + GrabSendInterval;
                if (Send(Protocol.Attack(_mimic, yaw)) && Settings.VerboseLog)
                    Log.Trace($"Grab sent: mimic {_mimic}, yaw {yaw:0}");
            }

            UpdateHud(main, now);
        }

        /// <summary>The game's own rules for "the player is typing into something else": no capture (cursor free), pause
        /// menu, F9 feedback form, debug console. Like the game's spectator input and SpectatorCam.</summary>
        static bool InputAllowed(InputManager input)
        {
            if (!input.IsCapturing()) return false;
            var hub = Hub.s;
            if (hub.uiman != null && hub.uiman.isGameMenuOpen) return false;
            if (hub.feedbackMan != null && hub.feedbackMan.feedbackUI != null && hub.feedbackMan.feedbackUI.IsOpen) return false;
            try { if (hub.console != null && hub.console.IsConsoleActive()) return false; }
            catch (Exception) { /* no console panel in this build */ }
            return true;
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

        static void SendInput(Vector3 dir, bool run, float yaw, float now, bool moving)
        {
            _seq++;
            Send(Protocol.Input(_mimic, dir, run, yaw, _seq));
            _lastSendAt = now;
            _lastDir = dir;
            _lastRun = run;
            _lastMoving = moving;
            _lastYaw = yaw;
            if (Settings.VerboseLog)
                Log.Trace($"Input #{_seq} mimic {_mimic}: {(moving ? run ? "run" : "walk" : "idle")} dir ({dir.x:0.00}, {dir.z:0.00}) yaw {yaw:0}");
        }

        /// <summary>Fire-and-forget to the host (no callback: no response expected, no 60 s timeout).</summary>
        static bool Send(IMsg msg)
        {
            var main = Hub.Main;
            if (main == null) return false;
            try
            {
                main.SendPacket(msg);
                return true;
            }
            catch (Exception e)
            {
                Log.WarnOnce("send", "Couldn't send to the host: " + e.Message);
                return false;
            }
        }

        // ---------------- HUD ----------------

        /// <summary>Per frame: track the mimic's grab for the cooldown hint. A few times a second: keep the tips ours
        /// and the 12 s bar hidden (the game rewrites both now and then).</summary>
        static void UpdateHud(GameMainBase main, float now)
        {
            bool busy = _mimicActor.IsUsingSkill();
            // The host counts the attack cooldown from the end of the grab; the client sees the grab end through the
            // mimic's own UseSkillSig (the skill animation's length), so this estimate is close, and only a hint.
            if (_mimicBusy && !busy) _grabReadyAt = now + Settings.Session.AttackCooldownMs / 1000f;
            _mimicBusy = busy;

            if (now < _nextUiAt) return;
            _nextUiAt = now + UiInterval;
            try
            {
                HideTimerBar(_mimicActor);
                var ui = main.spectatorui;
                if (ui == null) return;
                // E.g. the game's spectator-switch countdown resets the Next tip's text when the camera left the player.
                if (!ui.UE_UI_GameTip_Spectator_Prev.gameObject.activeSelf || !ui.UE_UI_GameTip_Spectator_Next.gameObject.activeSelf
                    || ui.NextGameTip.UE_UI_GuideText.text != RunLabel
                    || ui.PossessionGameTip.UE_UI_GuideText.text != ReleaseLabel)
                {
                    ApplyTips(ui);
                    return;
                }
                var grab = GrabText(now);
                var prev = ui.PrevGameTip.UE_UI_GuideText;
                if (prev.text != grab) prev.SetText(grab); // the countdown: text only, the key icons stay
            }
            catch (Exception e) { Log.ErrorOnce("hud", "Updating the control tips failed: " + e); }
        }

        static string GrabText(float now)
        {
            if (_mimicBusy) return GrabLabel;
            float left = _grabReadyAt - now;
            return left > 0.05f ? $"{GrabLabel} ({Mathf.CeilToInt(left)})" : GrabLabel;
        }

        /// <summary>C5: Prev = Grab (FireWeapon), Next = Run, the possession tip = Let go (Interact). The spectator's tips
        /// stack in a vertical layout group, so showing the two hidden ones doesn't overlap the possession tip.</summary>
        internal static void ApplyTips(UIPrefab_Spectator ui)
        {
            if (ui == null) return;
            ui.SetActiveForUIPrefab(ui.UE_UI_GameTip_Spectator_Prev.gameObject, true);
            ui.SetActiveForUIPrefab(ui.UE_UI_GameTip_Spectator_Next.gameObject, true);
            SetTip(ui, ui.PrevGameTip, GrabText(Time.unscaledTime), InputAction.FireWeapon);
            SetTip(ui, ui.NextGameTip, RunLabel, InputAction.Run);
            SetTip(ui, ui.PossessionGameTip, ReleaseLabel, InputAction.Interact);
        }

        static void SetTip(UIPrefab_Spectator ui, UIPrefab_GameTip tip, string text, InputAction action)
        {
            if (tip == null) return;
            tip.UE_UI_GuideText.SetText(text);
            ui.SetGameTipKeyIcons(tip, action); // the player's own binds, keyboard or pad icons
        }

        /// <summary>Back to the game's tips: its possession layout if the camera is still on the mimic, its texts and
        /// key icons in any case (the game's own Show() handles the layout when it moves the camera itself).</summary>
        static void RestoreTips()
        {
            var main = Hub.Main;
            var ui = main != null ? main.spectatorui : null;
            if (ui == null) return;
            var cm = Hub.s != null ? Hub.s.cameraman : null;
            if (cm != null && cm.Mode == CameraManager.CameraMode.MimicPossession)
            {
                ui.UE_UI_GameTip_Spectator_Prev.gameObject.SetActive(false);
                ui.UE_UI_GameTip_Spectator_Next.gameObject.SetActive(false);
            }
            ui.RefreshGameTips();
        }

        /// <summary>The mimic's world-space 12 s bar counts down a duration that doesn't apply while controlling.</summary>
        static void HideTimerBar(ProtoActor mimic)
        {
            if (mimic == null) return;
            if (mimic._progressCoroutine != null)
            {
                mimic.StopCoroutine(mimic._progressCoroutine);
                mimic._progressCoroutine = null;
            }
            var bar = mimic._possessionProgressbar;
            if (bar != null && bar.gameObject.activeSelf) bar.gameObject.SetActive(false);
        }

        // ---------------- hooks (called by the patch classes below) ----------------

        internal static void OnTryStartPossession(int mimicId)
        {
            if (!Control.IsSupportedByHost) return;
            if (_mimic == 0) _ackMimic = 0; // only an ack for this new possession counts
            SendHello("possessing mimic " + mimicId);
        }

        internal static bool OnPossessorInfo(SyncPossessorInfoSig sig) => OnAck(sig);

        internal static void OnPossessionCameraEnd() => Teardown("possession ended");

        internal static void OnRevived(CameraManager cm)
        {
            Teardown("revived");
            _helloSentThisDeath = false;
            _beginFailedFor = 0;
            if (!Control.IsSupportedByHost) return;
            // The host's revive zeroes its possession cooldown without telling this game (no sync below zero), so a
            // cooldown from this death (Protocol.BlockedCooltime with "one per death") would block the next death.
            cm.SetLeftPossessionTime(0L, 0L);
            var main = Hub.Main;
            var ui = main != null ? main.spectatorui : null;
            if (ui == null) return;
            if (ui._possessionCooltimeCoroutine != null)
            {
                ui.StopCoroutine(ui._possessionCooltimeCoroutine);
                ui._possessionCooltimeCoroutine = null;
            }
            ui.UE_Possession_Gauge.fillAmount = 0f;
            ui.UpdatePossessionState(0f); // hides the cooldown icon and gauge (no coroutine: the HUD may be inactive)
        }
    }

    sealed class ClientDriver : MonoBehaviour
    {
        void Update()
        {
            if (!Control.ClientReady) return;
            try { ClientControl.Tick(); }
            catch (Exception e) { Log.ErrorOnce("tick", "Possessor update failed: " + e); }
        }
    }

    /// <summary>C2-C3: hello before every possession request, and the host's ack.</summary>
    [Core(Side.Client)]
    [HarmonyPatch]
    static class ClientNetPatch
    {
        // C2. Right before the game sends StartPossessingReq: a hello first (same connection, so the host has it when
        // the request arrives and takes the mimic over after a successful start).
        [HarmonyPrefix, HarmonyPatch(typeof(GameMainBase), nameof(GameMainBase.TryStartPossession))]
        static void BeforeStartPossession(int inMimicActorId)
        {
            if (!Control.ClientReady) return;
            try { ClientControl.OnTryStartPossession(inMimicActorId); }
            catch (Exception e) { Log.ErrorOnce("c2", "Hello before possessing failed: " + e); }
        }

        // C3. The possessor sync. The host's ack (Protocol.IsAck) is swallowed: vanilla would feed its magic "time left"
        // to the mimic's 12 s bar. Any other sig: vanilla.
        [HarmonyPrefix, HarmonyPatch(typeof(GameMainBase), nameof(GameMainBase.OnPacket), new[] { typeof(SyncPossessorInfoSig) })]
        static bool OnPossessorInfo(SyncPossessorInfoSig sig)
        {
            if (!Control.ClientReady) return true;
            try { return !ClientControl.OnPossessorInfo(sig); }
            catch (Exception e) { Log.ErrorOnce("c3", "Reading the possessor sync failed: " + e); return true; }
        }
    }

    /// <summary>C6: the possession ends (twice for a release: Cancel then End), or the player is revived.</summary>
    [Core(Side.Client)]
    [HarmonyPatch]
    static class ClientCameraPatch
    {
        // Before the game moves the camera back to a living player: restore the orbit while it still follows the mimic,
        // so the view doesn't jump, and the game's own HUD refresh after the switch sees control already over.
        [HarmonyPrefix, HarmonyPatch(typeof(CameraManager), nameof(CameraManager.EndMimicPossessionCamera))]
        static void BeforeEndPossessionCamera()
        {
            if (!Control.ClientReady) return;
            try { ClientControl.OnPossessionCameraEnd(); }
            catch (Exception e) { Log.ErrorOnce("c6-pre", "Ending control failed: " + e); }
        }

        // Safety net (idempotent): normally the prefix already ended control.
        [HarmonyPostfix, HarmonyPatch(typeof(CameraManager), nameof(CameraManager.EndMimicPossessionCamera))]
        static void AfterEndPossessionCamera()
        {
            if (!Control.ClientReady) return;
            try { ClientControl.OnPossessionCameraEnd(); }
            catch (Exception e) { Log.ErrorOnce("c6-post", "Ending control failed: " + e); }
        }

        // Revived (Revive patch, tram): teardown, and clear the camera manager's possession cooldown, which may still
        // hold Protocol.BlockedCooltime from this death (the host's Reset zeroes its side without telling us).
        [HarmonyPostfix, HarmonyPatch(typeof(CameraManager), nameof(CameraManager.OnPlayerRevive))]
        static void AfterRevive(CameraManager __instance)
        {
            if (!Control.ClientReady) return;
            try { ClientControl.OnRevived(__instance); }
            catch (Exception e) { Log.ErrorOnce("c6-revive", "Cleanup after revive failed: " + e); }
        }
    }

    /// <summary>C5: the spectator HUD's tips while controlling. Not core: without it the tips are still fixed up a few
    /// times a second by the driver (ClientControl.UpdateHud).</summary>
    [HarmonyPatch]
    static class ClientUiPatch
    {
        // UIPrefab_Spectator declares a parameterless Show() (the base class has Show(bool)): pick it with no arguments.
        // In possession mode it hides the Prev/Next tips; while controlling they show Grab and Run instead.
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_Spectator), nameof(UIPrefab_Spectator.Show), new Type[0])]
        static void AfterShow(UIPrefab_Spectator __instance)
        {
            if (!Control.ClientReady) return;
            try { if (ClientControl.Controlling) ClientControl.ApplyTips(__instance); }
            catch (Exception e) { Log.ErrorOnce("c5-show", "Control tips failed: " + e); }
        }

        // Language, input device or key binding changed: the game rewrites the tips here.
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_Spectator), nameof(UIPrefab_Spectator.RefreshGameTips))]
        static void AfterRefreshTips(UIPrefab_Spectator __instance)
        {
            if (!Control.ClientReady) return;
            try { if (ClientControl.Controlling) ClientControl.ApplyTips(__instance); }
            catch (Exception e) { Log.ErrorOnce("c5-refresh", "Control tips failed: " + e); }
        }
    }
}
