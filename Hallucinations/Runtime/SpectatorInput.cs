// MIMESIS Hallucinations - dead player: E (possess or hallucinate) and the spectator HUD tip
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// E is the game's Interact action, read by CameraManager.UpdateMimicPossession every frame. Context rule:
//   - a mimic can really be possessed (IsPossessionAvailable) -> vanilla possesses, untouched;
//   - else the spectated living player can be sent a hallucination (Eligible) -> we send the request and skip the
//     original for this one frame (so a stale vanilla _availableMimic can't also send a doomed StartPossessingReq);
//   - else vanilla.
// The HUD tip reuses the spectator's possession tip (vanilla hides it while spectating a player): "Possess" (the
// game's own text), "Hallucinate", "Hallucinate (12)" on cooldown, or a short result message. Hidden otherwise. With a
// host that doesn't run this (no Ack this level), or while SpectatorCam's free camera is on (its E flies the camera),
// nothing here touches the HUD or E.

using System;
using HarmonyLib;
using Mimic.Actors;
using Mimic.InputSystem;
using ReluProtocol.Enum;
using UnityEngine;

namespace Hallucinations
{
    static class SpectatorInput
    {
        const float TipInterval = 0.25f;
        const float FlashSeconds = 3f;

        const string HallucinateLabel = "Hallucinate";

        // ---- state (main thread; times are Time.unscaledTime) ----
        static int _nonce;
        static int _pendingNonce;        // 0 = no request in flight
        static int _pendingTarget;
        static float _pendingAt;
        static float _cooldownUntil;
        static int _lastSentId;          // our latest hallucination (Result Ok): only its end can give the cooldown back
        static string _flashText;
        static float _flashUntil;
        static UIPrefab_Spectator _tipUi; // the spectator HUD whose possession tip shows our text (we must hide/restore it)
        static bool _applying;           // re-entrancy guard: RefreshGameTips calls our C3 postfix
        static float _nextTipAt;

        /// <summary>Every frame (ClientDriver): pending request timeout, and the tip a few times a second (the game rewrites
        /// it; the cooldown counts down).</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (_pendingNonce != 0 && now - _pendingAt > Const.RequestTimeoutSec)
            {
                Log.Info($"The host didn't answer the hallucination request on {Name(_pendingTarget)} (lost?): you can try again");
                _pendingNonce = 0;
                _nextTipAt = 0f;
            }
            if (now < _nextTipAt) return;
            _nextTipAt = now + TipInterval;

            var main = Hub.Main;
            var ui = main != null ? main.spectatorui : null;
            if (ui == null)
            {
                _tipUi = null; // destroyed with its scene: nothing to restore
                return;
            }
            ApplyTip(ui);
        }

        /// <summary>The host's Ack changed what we may do: refresh the tip now.</summary>
        internal static void OnViewChanged() => _nextTipAt = 0f;

        /// <summary>Spectate gave the HUD back after steering (it reset the tips): ours is set up again from scratch.</summary>
        internal static void OnSteeringEnded()
        {
            _tipUi = null;
            _nextTipAt = 0f;
        }

        /// <summary>C2, every frame while dead. True = run vanilla; false = E was ours this frame.</summary>
        internal static bool OnUpdateMimicPossession(CameraManager cm)
        {
            // Steering a hallucination: E lets go of it, and never possesses a mimic meanwhile.
            if (Spectate.Steering)
            {
                Spectate.OnInteract();
                return false;
            }
            if (cm == null || cm.Mode != CameraManager.CameraMode.PlayerSpectate || cm._waitServerResponse) return true;
            if (!ClientNet.View.Supported || FreeCamera(cm)) return true;
            var input = Hub.s != null ? Hub.s.inputman : null;
            if (input == null || !input.wasPressedThisFrame(InputAction.Interact)) return true;
            if (!InputAllowed(input)) return true;
            if (IsPossessionAvailable(cm)) return true;                 // vanilla possesses
            if (!Eligible(cm, out var target)) return true;             // vanilla (as without the mod)

            float now = Time.unscaledTime;
            _nextTipAt = 0f;
            if (_pendingNonce != 0 || now < _cooldownUntil) return false; // E means "hallucinate"; the tip says why not yet

            if (++_nonce <= 0) _nonce = 1;
            if (!ClientNet.Send(Protocol.Request(target.ActorID, _nonce))) return true;
            _pendingNonce = _nonce;
            _pendingTarget = target.ActorID;
            _pendingAt = now;
            Log.Info($"Hallucination requested on {target.nickName}");
            return false;                                                // skip vanilla this frame only
        }

        /// <summary>C3 (the game just rebuilt the spectator tips) and Tick: puts our tip text in place, or gives the tip back
        /// to the game.</summary>
        internal static void ApplyTip(UIPrefab_Spectator ui)
        {
            if (_applying || ui == null || Spectate.Steering) return; // Spectate shows the steering tips meanwhile
            _applying = true;
            try
            {
                if (!ReferenceEquals(_tipUi, null) && !ReferenceEquals(_tipUi, ui)) _tipUi = null; // another level's HUD
                var cm = Hub.s != null ? Hub.s.cameraman : null;
                string text = TipText(cm);
                if (text == null)
                {
                    if (!ReferenceEquals(_tipUi, null)) Release(ui, cm);
                    return;
                }
                if (!ui.IsShow) return; // the HUD is hidden (death camera, menus): leave it alone until it shows again

                var tipGo = ui.UE_UI_GameTip_Spectator_Possession.gameObject;
                if (!tipGo.activeSelf) ui.SetActiveForUIPrefab(tipGo, true);
                var tip = ui.PossessionGameTip;
                if (tip == null) return;
                if (ReferenceEquals(_tipUi, null))
                {
                    ui.SetGameTipKeyIcons(tip, InputAction.Interact); // the player's own binds, keyboard or pad icons
                    _tipUi = ui;
                }
                var label = tip.UE_UI_GuideText;
                if (label != null && label.text != text) label.SetText(text);
            }
            finally { _applying = false; }
        }

        /// <summary>What E does right now, for the tip; null = not ours to show (vanilla keeps the tip).</summary>
        static string TipText(CameraManager cm)
        {
            if (cm == null || cm.Mode != CameraManager.CameraMode.PlayerSpectate) return null;
            if (!ClientNet.View.Supported || !Control.SpectatorReady || FreeCamera(cm)) return null;
            if (IsPossessionAvailable(cm)) return PossessText();
            float now = Time.unscaledTime;
            if (_flashText != null && now < _flashUntil) return _flashText;
            if (!Eligible(cm, out _)) return null;
            if (_pendingNonce != 0) return HallucinateLabel + "...";
            float left = _cooldownUntil - now;
            return left > 0.05f ? $"{HallucinateLabel} ({Mathf.CeilToInt(left)})" : HallucinateLabel;
        }

        /// <summary>The game's own "Possess" (its world prompt over the mimic uses the same key).</summary>
        static string PossessText()
        {
            try
            {
                var s = Hub.GetL10NText("STRING_MIMIC_VOICE_LINK");
                if (!string.IsNullOrEmpty(s) && s != "STRING_MIMIC_VOICE_LINK") return s;
            }
            catch (Exception) { /* no localization: English below */ }
            return "Possess";
        }

        /// <summary>Gives the possession tip back to the game: hidden while spectating a player (as vanilla), and its texts
        /// reset ("End possession" for the possession camera).</summary>
        static void Release(UIPrefab_Spectator ui, CameraManager cm)
        {
            _tipUi = null;
            if (ui == null) return;
            if (cm != null && cm.Mode == CameraManager.CameraMode.PlayerSpectate)
                ui.UE_UI_GameTip_Spectator_Possession.gameObject.SetActive(false);
            ui.RefreshGameTips(); // our C3 postfix runs inside: _applying makes it return
        }

        /// <summary>The host's answer to our request.</summary>
        internal static void OnResult(in HostMsg m)
        {
            if (_pendingNonce == 0 || m.Nonce != _pendingNonce)
            {
                Log.Trace($"Late answer to request {m.Nonce} ignored ({m.Result})");
                return;
            }
            _pendingNonce = 0;
            _nextTipAt = 0f;
            float now = Time.unscaledTime;
            string who = Name(m.Target);
            switch (m.Result)
            {
                case Result.Ok:
                    _cooldownUntil = now + Mathf.Max(0, m.CooldownLeftMs) / 1000f;
                    _lastSentId = m.Id;
                    Flash("Hallucination sent");
                    Log.Info($"Hallucination #{m.Id} sent to {who}");
                    break;
                case Result.Cooldown:
                    _cooldownUntil = now + Mathf.Max(0, m.CooldownLeftMs) / 1000f;
                    Log.Info($"Hallucination not sent: cooldown ({Mathf.CeilToInt(m.CooldownLeftMs / 1000f)} s left)");
                    break;
                default:
                    Flash(ResultText(m.Result));
                    Log.Info($"Hallucination on {who} refused by the host: {m.Result}");
                    break;
            }
        }

        /// <summary>How our hallucination ended.</summary>
        internal static void OnOutcome(in HostMsg m)
        {
            // The host's rule: the cooldown comes back only if it's still the one this hallucination started, i.e. we
            // haven't sent another one since (that one's cooldown stays).
            bool refunded = Outcomes.Refunds(m.Outcome) && m.Id != 0 && m.Id == _lastSentId;
            Flash(OutcomeText(m.Outcome, refunded));
            if (refunded) _cooldownUntil = 0f;
            _nextTipAt = 0f;
            Log.Info($"Hallucination #{m.Id} on {Name(m.Target)} ended: {m.Outcome}" + (refunded ? " (cooldown given back)" : ""));
        }

        /// <summary>A new level (or left one): forget everything and give the tip back.</summary>
        internal static void Reset()
        {
            _pendingNonce = 0;
            _pendingTarget = 0;
            _cooldownUntil = 0f;
            _lastSentId = 0;
            _flashText = null;
            _flashUntil = 0f;
            _nextTipAt = 0f;
            var ui = _tipUi;
            _tipUi = null;
            // Only the HUD we changed, and only while it still exists (a level change destroys it with its scene).
            if (ReferenceEquals(ui, null) || ui == null || _applying) return;
            var main = Hub.Main;
            if (main == null || !ReferenceEquals(main.spectatorui, ui)) return;
            _applying = true;
            try { Release(ui, Hub.s != null ? Hub.s.cameraman : null); }
            finally { _applying = false; }
        }

        static void Flash(string text)
        {
            _flashText = text;
            _flashUntil = Time.unscaledTime + FlashSeconds;
        }

        static string ResultText(Result r)
        {
            switch (r)
            {
                case Result.Ok: return "Hallucination sent";
                case Result.NoMod: return "They can't see hallucinations";
                case Result.LowRadiation: return "Their radiation is too low";
                case Result.Busy: return "They're already hallucinating";
                case Result.Disabled: return "Hallucinations are off";
                default: return "Can't send one now";
            }
        }

        static string OutcomeText(Outcome o, bool refunded)
        {
            string back = refunded ? " (cooldown back)" : "";
            switch (o)
            {
                case Outcome.Landed: return "It got them!";
                case Outcome.Missed: return "They dodged it";
                case Outcome.HitByPlayer: return "They fought it off";
                case Outcome.Timeout: return "It faded away";
                case Outcome.Cancelled: return "It was cut short";
                case Outcome.NoSpot: return "No room for it" + back;
                case Outcome.Lost: return "It lost them" + back;
                case Outcome.Busy: return "They're already hallucinating"; // no refund (Outcomes.Refunds)
                default: return "It didn't work" + back;
            }
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

        /// <summary>The game's own rules for "the player is typing into something else": no capture (cursor free), pause
        /// menu, F9 feedback form, debug console. Like the game's spectator input and SpectatorCam.</summary>
        internal static bool InputAllowed(InputManager input)
        {
            if (input == null || !input.IsCapturing()) return false;
            var hub = Hub.s;
            if (hub.uiman != null && hub.uiman.isGameMenuOpen) return false;
            if (hub.feedbackMan != null && hub.feedbackMan.feedbackUI != null && hub.feedbackMan.feedbackUI.IsOpen) return false;
            try { if (hub.console != null && hub.console.IsConsoleActive()) return false; }
            catch (Exception) { /* no console panel in this build */ }
            return true;
        }

        /// <summary>E would send a hallucination to the spectated player: the host supports it, we're dead and watching a
        /// living player whose game shows hallucinations and whose radiation is above the host's threshold (the same
        /// formula as the host; it checks again anyway). Other players' radiation is synced to every game
        /// (AnnounceMutableStatSig); the spectator HUD shows the same value.</summary>
        internal static bool Eligible(CameraManager cm, out ProtoActor target)
        {
            target = null;
            if (cm == null || !ClientNet.View.Supported || cm.Mode != CameraManager.CameraMode.PlayerSpectate) return false;
            var main = Hub.Main;
            var me = main != null ? main.GetMyAvatar() : null;
            if (me == null || !me.dead) return false;
            if (!cm.TryGetCurrentSpectatorTarget(out var t) || t == null) return false;
            if (t.ActorType != ActorType.Player || t.dead || t.AmIAvatar()) return false;
            if (!ClientNet.View.IsTarget(t.ActorID)) return false;
            var data = t.netSyncActorData;
            if (data == null) return false;
            // The host's rule: conta > MaxConta * threshold / 100 ("above", strictly).
            if (data.conta * 100L <= (long)ClientNet.View.ThresholdPct * Control.MaxConta) return false;
            target = t;
            return true;
        }

        /// <summary>SpectatorCam's free camera is on: it detaches the spectator camera (Follow null, which the game's own
        /// SetupSpectatorCamera always sets) and its E flies the camera up.</summary>
        static bool FreeCamera(CameraManager cm)
        {
            var cam = cm != null ? cm.spectatorCamera : null;
            return cam != null && cam.Follow == null;
        }

        /// <summary>Vanilla E would really start a possession now. Stricter than vanilla's own `_availableMimic != null`:
        /// that field goes stale during the 40 s possession cooldown (CheckMimicPossessionPosition returns before clearing
        /// it), and E would then send a StartPossessingReq that the host refuses.</summary>
        internal static bool IsPossessionAvailable(CameraManager cm)
        {
            if (cm == null || !cm._isPossessionActivated || cm._waitServerResponse || cm._possessionCooltime_ms > 0) return false;
            var mimic = cm._availableMimic;
            return mimic != null && !mimic.dead && mimic.Possession.State == ProtoActor.PossessionState.Available;
        }
    }

    /// <summary>C2: E. Without it a dead player can't send hallucinations (hello without Caps.Request).</summary>
    [Core(Part.Spectator)]
    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.UpdateMimicPossession))]
    static class SpectatorInputPatch
    {
        // C2. Last of all prefixes on this method: SpectatorCam's own prefix returns false while its free camera has E
        // (= fly up) pressed; HarmonyX runs every prefix and ANDs their results into __runOriginal, so we see that veto
        // and stay out. Our false skips the original for this frame only.
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(CameraManager __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!Control.SpectatorReady || Control.IsReplay) return true;
            try { return SpectatorInput.OnUpdateMimicPossession(__instance); }
            catch (Exception e)
            {
                Log.ErrorOnce("C2", "Reading E failed: " + e);
                return true;
            }
        }
    }

    /// <summary>C3: the tip. Not core: the driver re-applies it a few times a second anyway.</summary>
    [HarmonyPatch]
    static class SpectatorTipPatch
    {
        // UIPrefab_Spectator declares a parameterless Show() (the base class has Show(bool)): pick it with no arguments.
        // Every spectator camera change ends here (OnSetupSpectatorCamera -> ShowSpectatorHUD).
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_Spectator), nameof(UIPrefab_Spectator.Show), new Type[0])]
        static void AfterShow(UIPrefab_Spectator __instance)
        {
            if (!Control.SpectatorReady) return;
            try { SpectatorInput.ApplyTip(__instance); }
            catch (Exception e) { Log.ErrorOnce("C3-show", "Spectator tip failed: " + e); }
        }

        // Language, input device or key binding changed: the game rewrites the tips here (and resets the possession tip's
        // text to "End possession").
        [HarmonyPostfix, HarmonyPatch(typeof(UIPrefab_Spectator), nameof(UIPrefab_Spectator.RefreshGameTips))]
        static void AfterRefreshTips(UIPrefab_Spectator __instance)
        {
            if (!Control.SpectatorReady) return;
            try { SpectatorInput.ApplyTip(__instance); }
            catch (Exception e) { Log.ErrorOnce("C3-refresh", "Spectator tip failed: " + e); }
        }
    }
}
