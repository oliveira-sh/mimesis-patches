// MIMESIS FakePlayers - the in-game panel (F7 while hosting)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// IMGUI on top of everything. While it's open the mouse is free (InputManager.SetCapturing(false), as the game's menu
// does), so the game takes no look/move input; closing it (F7, or a scene change) gives the mouse back unless the game's
// own menu is open. F7 only: Esc is the game's menu key (both would act on it).
// Clicks are queued and run in Update (the host's server runs on the main thread too, never mid-tick).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FakePlayers
{
    sealed class Panel : MonoBehaviour
    {
        const float Width = 820f, Height = 640f;  // at 1080p (scaled with the screen height)
        const float SliderInterval = 0.12f;        // s between two stat changes while a slider is dragged
        static readonly int[] RadPresets = { 0, 50, 60, 90 };

        static Panel _instance;
        bool _open;
        bool _freedCursor;
        object _scene;   // the scene it was opened in
        Rect _rect = new Rect(40f, 90f, Width, Height);
        Vector2 _scroll;
        string _status = "";
        float _statusAt;
        readonly Queue<Func<string>> _queue = new Queue<Func<string>>();
        readonly Dictionary<(VPlayer, int), float> _lastSlide = new Dictionary<(VPlayer, int), float>();
        GUIStyle _dim, _head, _warn;

        internal static void Install()
        {
            if (_instance != null) return;
            var go = new GameObject("MedovanxFakePlayers");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Panel>();
        }

        internal static void CloseIfOpen()
        {
            if (_instance != null && _instance._open) _instance.Close();
        }

        void Update()
        {
            try
            {
                if (!Plugin.Enabled)
                {
                    if (_open) Close();
                    return;
                }
                if (_open && !ReferenceEquals(Hub.Main, _scene)) Close();   // another scene (a level ends, the tram leaves)
                var kb = Keyboard.current;
                if (kb != null && kb.f7Key.wasPressedThisFrame)
                {
                    if (_open) Close();
                    else Open();
                }
                if (_open) KeepCursorFree();

                while (_queue.Count > 0)
                {
                    var action = _queue.Dequeue();
                    try { Say(action()); }
                    catch (Exception e)
                    {
                        Say("Failed: " + e.Message);
                        Log.Error("A panel action failed: " + e);
                    }
                }
                Bots.Tick();
            }
            catch (Exception e) { Log.Error("Panel update failed: " + e); }
        }

        void Open()
        {
            _open = true;
            _scene = Hub.Main;
            var input = Hub.s != null ? Hub.s.inputman : null;
            if (input != null && input.IsCapturing())
            {
                input.SetCapturing(false);
                _freedCursor = true;
            }
            Cursor.visible = true;
        }

        void Close()
        {
            _open = false;
            _scene = null;
            var hub = Hub.s;
            // The mouse back to the game wherever you play (a level, the tram, the station: any scene with your avatar),
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

        void Do(Func<string> action) => _queue.Enqueue(action);

        // ============================================================================================ drawing

        void OnGUI()
        {
            if (!_open || !Plugin.Enabled) return;
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            if (_dim == null)
            {
                _dim = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _dim.normal.textColor = new Color(0.75f, 0.75f, 0.72f);
                _head = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                _warn = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _warn.normal.textColor = new Color(1f, 0.75f, 0.35f);
            }
            _rect = GUILayout.Window(0x6D647846, _rect, DrawWindow, "Fake players  (F7 closes)");
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
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime - _statusAt < 6f) GUILayout.Label(_status, _head);
            else GUILayout.Label(" ", _head);

            if (!Bots.IsHost)
            {
                GUILayout.Label("Host a game to add fake players (they live on your game's server).", _warn);
                return;
            }
            if (!Bots.TryGetHost(out var host, out var room))
            {
                GUILayout.Label("Waiting for your player (between levels)...", _dim);
                return;
            }
            var bots = Bots.InRoom(room);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"In {RoomName(room)}: {bots.Count} fake player(s)", GUILayout.Width(300f));
            if (GUILayout.Button("Add 1", GUILayout.Width(90f))) Do(() => Bots.Spawn(1));
            if (GUILayout.Button("Add 3", GUILayout.Width(90f))) Do(() => Bots.Spawn(3));
            if (GUILayout.Button("Remove all", GUILayout.Width(110f))) Do(Bots.RemoveAll);
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            DrawPlayer(host, isYou: true);
            foreach (var b in bots) DrawPlayer(b, isYou: false);
            GUILayout.EndScrollView();

            if (bots.Count > 1) DrawAll(bots);
            GUILayout.Label(
                "Fake players are the game's own dummy players: everyone in your game sees them, they don't move unless they " +
                "follow you, and they stay behind when the team changes level. Hallucinations: with its Debug keys on, raise a " +
                "fake player's radiation above the threshold, Kill yourself, watch it and press E to steer one at it.", _dim);
        }

        void DrawPlayer(VPlayer p, bool isYou)
        {
            bool alive = p.IsAliveStatus();
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            string who = isYou ? $"You ({p.ActorName})" : p.ActorName;
            GUILayout.Label($"{who}  #{p.ObjectID}", _head, GUILayout.Width(210f));
            GUILayout.Label(LifeText(p), GUILayout.Width(80f));
            GUILayout.Label($"HP {Bots.HpPct(p)}%", GUILayout.Width(80f));
            GUILayout.Label($"Rad {Bots.RadPct(p)}%", GUILayout.Width(80f));
            if (!isYou && Bots.TryGetHost(out var host, out _))
                GUILayout.Label($"{Vector3.Distance(host.PositionVector, p.PositionVector):0.0} m", GUILayout.Width(70f));
            GUILayout.FlexibleSpace();
            if (alive)
            {
                if (GUILayout.Button(isYou ? "Kill me" : "Kill", GUILayout.Width(80f))) Do(() => Bots.Kill(p));
            }
            else if (GUILayout.Button(isYou ? "Revive me" : "Revive", GUILayout.Width(90f))) Do(() => Bots.Revive(p));
            if (!isYou && GUILayout.Button("Remove", GUILayout.Width(80f))) Do(() => Bots.Remove(p));
            GUILayout.EndHorizontal();

            if (alive)
            {
                GUILayout.BeginHorizontal();
                StatSlider(p, 0, "Radiation", Bots.RadPct(p), 0, 99, v => Bots.SetRad(p, v));
                foreach (int pct in RadPresets)
                    if (GUILayout.Button(pct + "%", GUILayout.Width(52f))) Do(() => Bots.SetRad(p, pct));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                StatSlider(p, 1, "Health", Bots.HpPct(p), 1, 100, v => Bots.SetHp(p, v));
                if (GUILayout.Button("Full", GUILayout.Width(52f))) Do(() => Bots.SetHp(p, 100));
                if (!isYou)
                {
                    if (GUILayout.Button("Bring", GUILayout.Width(66f))) Do(() => Bots.Bring(p));
                    bool follows = Bots.Following(p);
                    if (GUILayout.Button(follows ? "Stay" : "Follow", GUILayout.Width(66f))) Do(() => Bots.SetFollow(p, !follows));
                    if (GUILayout.Button("Talk 5 s", GUILayout.Width(76f))) Do(() => Bots.Talk(p, 5f));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        /// <summary>A 0-100 slider that changes the stat while dragged (at most every SliderInterval).</summary>
        void StatSlider(VPlayer p, int which, string label, int current, int min, int max, Func<int, string> set)
        {
            GUILayout.Label(label, GUILayout.Width(80f));
            float v = GUILayout.HorizontalSlider(current, min, max, GUILayout.Width(260f));
            GUILayout.Label($"{current}%", GUILayout.Width(44f));
            int want = Mathf.RoundToInt(v);
            if (want == current) return;
            var key = (p, which);
            float now = Time.unscaledTime;
            if (_lastSlide.TryGetValue(key, out float at) && now - at < SliderInterval) return;
            _lastSlide[key] = now;
            Do(() => set(want));
        }

        void DrawAll(List<VPlayer> bots)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("All fake players:", _head, GUILayout.Width(150f));
            if (GUILayout.Button("Kill", GUILayout.Width(60f))) Do(() => ForAll(bots, Bots.Kill));
            if (GUILayout.Button("Revive", GUILayout.Width(70f))) Do(() => ForAll(bots, Bots.Revive));
            if (GUILayout.Button("Rad 0%", GUILayout.Width(70f))) Do(() => ForAll(bots, b => Bots.SetRad(b, 0)));
            if (GUILayout.Button("Rad 60%", GUILayout.Width(76f))) Do(() => ForAll(bots, b => Bots.SetRad(b, 60)));
            if (GUILayout.Button("Full HP", GUILayout.Width(70f))) Do(() => ForAll(bots, b => Bots.SetHp(b, 100)));
            if (GUILayout.Button("Bring", GUILayout.Width(60f))) Do(() => ForAll(bots, Bots.Bring));
            if (GUILayout.Button("Follow", GUILayout.Width(66f))) Do(() => ForAll(bots, b => Bots.SetFollow(b, true)));
            if (GUILayout.Button("Stay", GUILayout.Width(56f))) Do(() => ForAll(bots, b => Bots.SetFollow(b, false)));
            GUILayout.EndHorizontal();
        }

        static string ForAll(List<VPlayer> bots, Func<VPlayer, string> action)
        {
            string last = null;
            foreach (var b in bots)
            {
                try { last = action(b); }
                catch (Exception e) { Log.Error($"{b.ActorName}: " + e); }
            }
            return bots.Count == 1 ? last : $"Done for {bots.Count} fake players";
        }

        static string LifeText(VPlayer p)
        {
            switch (p.LifeCycle)
            {
                case VCreatureLifeCycle.Alive: return "Alive";
                case VCreatureLifeCycle.Dead: return "Dead";
                case VCreatureLifeCycle.Dying:
                case VCreatureLifeCycle.ForceDying: return "Dying";
                default: return "Spawning";
            }
        }

        static string RoomName(IVroom room)
        {
            if (room is DungeonRoom) return "the level";
            if (room is MaintenanceRoom) return "the tram";
            if (room is VWaitingRoom) return "the waiting room";
            return room.GetType().Name;
        }
    }
}
