// MIMESIS Hallucinations - testing keys (Debug setting)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Only while your own "Debug keys" setting is on, you're alive in a level and the host acked with debug on (playing
// alone you're the host, so it's your setting both times):
//   Ctrl+Shift+H  Protocol.Request(your own actor id, nonce, next weapon): a hallucination on yourself, any radiation,
//                 no cooldown. Each press uses the next weapon (Shotgun, Bomb, Melee, Grab, ...), so one session tests
//                 them all. It goes through the host like a real one (the whole relay is exercised).
//   Ctrl+Shift+J  Protocol.DebugConta(Const.DebugContaStepPct): +10 % radiation to yourself (never past the talk cap),
//                 to test the threshold and the HUD tip without waiting.
// Keyboard.current (Unity Input System) directly: these aren't game actions, and nothing in the game binds H or J.
// Results and outcomes of your own debug hallucinations come back here (their target is you) and are only logged.

using Mimic.Actors;
using UnityEngine;

namespace Hallucinations
{
    static class DebugKeys
    {
        static readonly Weapon[] Cycle = { Weapon.Shotgun, Weapon.Bomb, Weapon.Melee, Weapon.Grab };
        static int _next;
        static int _nonce = 1000000; // apart from the spectator's nonces (both only ever compared within their own side)

        /// <summary>Every frame (ClientDriver).</summary>
        internal static void Tick()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            bool h = kb.hKey.wasPressedThisFrame, j = kb.jKey.wasPressedThisFrame;
            if (!h && !j) return;
            if (!(kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) || !(kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)) return;
            if (!Settings.Debug) return; // your own Debug keys setting is off: the keys do nothing

            var input = Hub.s != null ? Hub.s.inputman : null;
            if (!SpectatorInput.InputAllowed(input)) return;
            var scene = Hub.Main as GamePlayScene;
            ProtoActor me = scene != null ? scene.GetMyAvatar() : null;
            if (me == null || me.dead)
            {
                Log.WarnOnce("debug-alive", "Debug keys work only while you're alive in a level.");
                return;
            }
            if (!ClientNet.View.Supported)
            {
                Log.WarnOnce("debug-host", "Debug keys: the host doesn't run Hallucinations, or has them off (no answer to the hello this level).");
                return;
            }
            if (!ClientNet.View.Debug)
            {
                Log.WarnOnce("debug-host-off", "Debug keys: the host has its own \"Debug keys\" setting off.");
                return;
            }

            if (h)
            {
                var w = Cycle[_next % Cycle.Length];
                _next = (_next + 1) % Cycle.Length;
                if (++_nonce <= 0) _nonce = 1000000;
                if (ClientNet.Send(Protocol.Request(me.ActorID, _nonce, w))) Log.Info($"Debug hallucination requested ({w})");
            }
            if (j)
            {
                if (ClientNet.Send(Protocol.DebugConta(Const.DebugContaStepPct))) Log.Info($"Debug: +{Const.DebugContaStepPct}% radiation requested");
            }
        }

        /// <summary>The host's answer to a debug request (its target is this player).</summary>
        internal static void OnResult(in HostMsg m)
        {
            if (m.Result == Result.Ok) Log.Info($"Debug hallucination #{m.Id}: Ok");
            else Log.Info($"Debug hallucination refused by the host: {m.Result}");
        }

        /// <summary>How a debug hallucination ended (normally the host doesn't send these for debug ones: no requester).</summary>
        internal static void OnOutcome(in HostMsg m)
        {
            Log.Info($"Debug hallucination #{m.Id} ended: {m.Outcome}");
        }
    }
}
