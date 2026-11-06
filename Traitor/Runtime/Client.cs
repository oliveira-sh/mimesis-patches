// MIMESIS Traitor - every player: your role on screen
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// The host tells each player their role once the traitor job starts ("role": traitor / crew / none). A small line
// stays in the corner for the rest of the level: the traitor is reminded that monsters ignore them, the crew that
// someone among them is lying. Cleared when the job ends or the level changes.

using System;
using System.Collections.Generic;
using Mimic.Actors;
using UnityEngine;

namespace Traitor
{
    static class Client
    {
        internal static string Role = "";          // "", "traitor", "crew"
        static object _scene;

        internal static void OnMessage(ProtoActor receiver, string op, Dictionary<string, string> m)
        {
            if (op != "role") return;
            m.TryGetValue("r", out string r);
            Role = r == "traitor" || r == "crew" ? r : "";
            _scene = Hub.Main;
            Log.Info("Role: " + (Role.Length == 0 ? "none" : Role));
        }

        /// <summary>Every frame: a new level clears the role.</summary>
        internal static void Tick()
        {
            if (Role.Length != 0 && !ReferenceEquals(Hub.Main, _scene)) Role = "";
        }
    }

    sealed class RoleHud : MonoBehaviour
    {
        GUIStyle _style;

        void Update()
        {
            try
            {
                Client.Tick();
                if (Plugin.ClientReady) MimesisPatches.TramMode.ClientTick();   // "I have the patch" to the host
            }
            catch (Exception e) { Log.ErrorOnce("hud-tick", "Role update failed: " + e); }
        }

        void OnGUI()
        {
            if (_failed || Client.Role.Length == 0 || !MimesisPatches.GameHud.Visible) return;   // not over the game's menu or its special screens
            try
            {
                if (_style == null) _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Bold, wordWrap = false };
                float scale = Screen.height / 1080f;
                _style.fontSize = Mathf.RoundToInt(22 * scale);
                bool traitor = Client.Role == "traitor";
                string text = traitor ? "TRAITOR  -  monsters ignore you" : "A traitor is among you";
                var color = traitor ? new Color(1f, 0.25f, 0.25f, 0.95f) : new Color(1f, 0.72f, 0.3f, 0.95f);
                Vector2 size = _style.CalcSize(new GUIContent(text));   // as wide as the line (never cut off)
                var r = new Rect(24 * scale, Screen.height * 0.30f, size.x + 4f, Mathf.Max(40 * scale, size.y + 4f));
                var old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.75f);
                GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, _style);
                GUI.color = color;
                GUI.Label(r, text, _style);
                GUI.color = old;
            }
            catch (Exception e)
            {
                _failed = true;   // OnGUI runs several times a frame: said once
                Log.ErrorOnce("hud-draw", "The role line failed and is off: " + e);
            }
        }

        bool _failed;
    }
}
