// MIMESIS Poltergeist - the debug window (F8 in a level, with Debug mode on)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Starts any haunting now, to see and hear it: the schedule and the "what can happen" switches don't apply to it, and it
// works with Poltergeist off. The rest does, as for any haunting: a figure needs open floor where it would stand and no
// monster near (it goes at once when one comes close: a real threat must never look like a haunting), a whisper a
// teammate's recorded voice, and nothing happens during a deathmatch or while the level isn't being played (arriving,
// the result screen). The window says why when one doesn't fit. IMGUI on top of everything, like FakePlayers' panel: it
// opens in a level only; while it's open the mouse is free (InputManager.SetCapturing(false), as the game's menu does);
// it closes when the level does, and closing gives the mouse back unless the game's own menu is open. F8 only: Esc is
// the game's menu key. A figure closes the window as it starts: you need to look around to see it. Clicks are queued and
// run in Update.

using System;
using System.Collections.Generic;
using Mimic.Actors;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Poltergeist
{
    sealed class TestWindow : MonoBehaviour
    {
        const float Width = 640f, Height = 430f;   // at 1080p (scaled with the screen height)

        static readonly (Haunt.Kind kind, string label, string started)[] Kinds =
        {
            (Haunt.Kind.Whisper, "Whisper", "a teammate's voice whispers near you"),
            (Haunt.Kind.Ambient, "Eerie sound", "an eerie sound plays around you"),
            (Haunt.Kind.Flicker, "Flickering lights", "the lights around you flicker"),
            (Haunt.Kind.Stalker, "Figure far away", "a figure stares at you from far ahead: look straight at it and it vanishes"),
            (Haunt.Kind.Runner, "Runner", "something runs past the edge of your view"),
            (Haunt.Kind.Behind, "Jumpscare", "it's right behind you: turn around"),
        };

        bool _open, _freedCursor;
        object _scene;   // the level it was opened in
        Rect _rect = new Rect(60f, 110f, Width, Height);
        string _status = "";
        float _statusAt;
        readonly Queue<Func<string>> _queue = new Queue<Func<string>>();
        GUIStyle _dim, _head, _warn;

        void Update()
        {
            try
            {
                if (!Settings.Debug)
                {
                    if (_open) Close();
                    return;
                }
                if (_open && !ReferenceEquals(Hub.Main, _scene)) Close();   // the level is over (or another scene)
                var kb = Keyboard.current;
                if (kb != null && kb.f8Key.wasPressedThisFrame)
                {
                    if (_open) Close();
                    else if (Hub.Main is GamePlayScene) Open();
                }
                if (_open) KeepCursorFree();

                while (_queue.Count > 0)
                {
                    var action = _queue.Dequeue();
                    try { Say(action()); }
                    catch (Exception e)
                    {
                        Say("Failed: " + e.Message);
                        Log.Error("A debug window action failed: " + e);
                    }
                }
            }
            catch (Exception e) { Log.ErrorOnce("window", "Debug window update failed: " + e); }
        }

        void Open()
        {
            _open = true;
            _scene = Hub.Main;
            KeepCursorFree();
        }

        void Close()
        {
            _open = false;
            _scene = null;
            var hub = Hub.s;
            // The mouse back to the game where you play (a level, the tram, the station: any scene with your avatar),
            // unless the game's own menu is open (it gives it back as it closes).
            if (_freedCursor && hub != null && hub.inputman != null && (hub.uiman == null || !hub.uiman.isGameMenuOpen) &&
                Hub.Main != null && Hub.Main.GetMyAvatar() != null)
                hub.inputman.SetCapturing(true);
            _freedCursor = false;
        }

        void KeepCursorFree()
        {
            var input = Hub.s != null ? Hub.s.inputman : null;
            if (input != null && input.IsCapturing())
            {
                input.SetCapturing(false);
                _freedCursor = true;
            }
            Cursor.visible = true;
        }

        void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _status = text;
            _statusAt = Time.unscaledTime;
        }

        // ============================================================================================ actions

        /// <summary>You, playing a level now; else why not.</summary>
        static bool TryGetPlayer(out GamePlayScene scene, out ProtoActor avatar, out string why)
        {
            scene = Hub.Main as GamePlayScene;
            avatar = scene != null ? scene.GetMyAvatar() : null;
            why = scene == null ? "Hauntings happen in a level"
                : avatar == null ? "Waiting for your player"
                : avatar.dead ? "You're dead: hauntings are for the living"
                : !scene.IsGameLoopRunning ? "The level isn't being played now (arriving, or over): wait for it to start"
                : Haunt.Paused ? "A deathmatch is on: no hauntings here"
                : null;
            return why == null;
        }

        static bool IsFigure(Haunt.Kind k) => k == Haunt.Kind.Stalker || k == Haunt.Kind.Runner || k == Haunt.Kind.Behind;

        string StartHaunting(Haunt.Kind kind, string label, string started)
        {
            if (!TryGetPlayer(out var scene, out var avatar, out string why)) return why;
            if (IsFigure(kind) && Haunt.MonsterNear(scene, avatar.transform.position))
                return $"{label}: a monster is near (a figure goes at once then, so a real threat never looks like a haunting)";
            Haunt.StopAll();
            if (!Haunt.Fire(kind, scene, avatar, out why)) return $"{label}: didn't fit here ({why})";
            if (IsFigure(kind)) Close();
            return $"{label}: {started}";
        }

        /// <summary>One of the kinds that are on, as it picks on its own (the first that fits, in a weighted random order).</summary>
        string StartRandom()
        {
            if (!TryGetPlayer(out var scene, out var avatar, out string why)) return why;
            Haunt.StopAll();
            bool monsterNear = Haunt.MonsterNear(scene, avatar.transform.position);
            foreach (var k in Haunt.Candidates(scene))
            {
                if ((IsFigure(k) && monsterNear) || !Haunt.Fire(k, scene, avatar, out _)) continue;
                if (IsFigure(k)) Close();
                foreach (var x in Kinds)
                    if (x.kind == k) return $"Random: {x.label}: {x.started}";
                return "Random: " + k;
            }
            return "Random: nothing that's on fitted here";
        }

        // ============================================================================================ drawing

        void OnGUI()
        {
            if (!_open || !Settings.Debug) return;
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            if (_dim == null)
            {
                _dim = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _dim.normal.textColor = new Color(0.75f, 0.75f, 0.72f);
                _head = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true };
                _warn = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _warn.normal.textColor = new Color(1f, 0.75f, 0.35f);
            }
            _rect = GUILayout.Window(0x706F6C74, _rect, DrawWindow, "Poltergeist debug  (F8 closes)");
            GUI.matrix = old;
        }

        void DrawWindow(int id)
        {
            try { DrawContents(); }
            catch (Exception e)
            {
                GUILayout.Label("Drawing failed: " + e.Message, _warn);
            }
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        void DrawContents()
        {
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime - _statusAt < 8f) GUILayout.Label(_status, _head);
            else GUILayout.Label(" ", _head);

            bool ready = TryGetPlayer(out var scene, out _, out string why);
            if (!ready) GUILayout.Label(why + ".", _warn);
            if (!Settings.Enabled) GUILayout.Label("Poltergeist is off: nothing happens on its own, but you can start any haunting here.", _warn);

            GUILayout.Label("Start one now:", _head);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = ready;
            for (int row = 0; row < Kinds.Length; row += 3)
            {
                GUILayout.BeginHorizontal();
                for (int i = row; i < row + 3 && i < Kinds.Length; i++)
                {
                    var k = Kinds[i];
                    if (GUILayout.Button(k.label, GUILayout.Width(196f), GUILayout.Height(34f))) _queue.Enqueue(() => StartHaunting(k.kind, k.label, k.started));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Random (as on its own)", GUILayout.Width(296f), GUILayout.Height(30f))) _queue.Enqueue(StartRandom);
            GUI.enabled = wasEnabled;
            if (GUILayout.Button("Stop what's on", GUILayout.Width(296f), GUILayout.Height(30f)))
                _queue.Enqueue(() => { Haunt.StopAll(); return "Stopped"; });
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            string own = !Settings.Enabled ? "off"
                : Haunt.Paused ? "paused (a deathmatch is on)"
                : scene == null || !scene.IsGameLoopRunning ? "waiting for the level to start"
                : $"next in about {Haunt.NextInSec:0} s";
            GUILayout.Label($"On its own: {own}. So far: {Haunt.Count}{(Haunt.LastEvent.Length > 0 ? ", last: " + Haunt.LastEvent : "")}.", _dim);
            GUILayout.Label(
                "Only you see and hear these. Whispers need a teammate's recorded voice; figures need open floor in view (the " +
                "jumpscare 2-3 m behind you) and no monster near. A figure closes this window as it starts, so you can look around.", _dim);
        }
    }
}
