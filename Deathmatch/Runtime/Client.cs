// MIMESIS Deathmatch - every player: the rules in the corner during a deathmatch
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx

using System;
using System.Collections.Generic;
using Mimic.Actors;
using UnityEngine;

namespace Deathmatch
{
    static class Client
    {
        /// <summary>AppDomain data other patches read (every patch is its own assembly): true while a deathmatch is on
        /// in this game. Poltergeist rests then: a fight between players stays fair (no figure to waste a shell on, no
        /// lights going out for one player only).</summary>
        internal const string NoHauntingsKey = "MimesisPatches.NoHauntings";

        internal static bool On;
        internal static string Gold = "";
        static object _scene;

        internal static void OnMessage(ProtoActor receiver, string op, Dictionary<string, string> m)
        {
            if (op != "dm") return;
            On = m.TryGetValue("on", out string on) && on == "1";
            Gold = m.TryGetValue("gold", out string g) ? g : "";
            _scene = Hub.Main;
            AppDomain.CurrentDomain.SetData(NoHauntingsKey, On);
        }

        /// <summary>Every frame: a new level clears it.</summary>
        internal static void Tick()
        {
            if (On && !ReferenceEquals(Hub.Main, _scene))
            {
                On = false;
                AppDomain.CurrentDomain.SetData(NoHauntingsKey, false);
            }
        }
    }

    sealed class RulesHud : MonoBehaviour
    {
        GUIStyle _style;

        void Update()
        {
            try
            {
                Client.Tick();
                if (Plugin.ClientReady) MimesisPatches.TramMode.ClientTick();   // "I have the patch" to the host
            }
            catch (Exception e) { Log.ErrorOnce("hud-tick", "Deathmatch HUD update failed: " + e); }
        }

        void OnGUI()
        {
            if (_failed || !Client.On || !MimesisPatches.GameHud.Visible) return;   // not over the game's menu or its special screens
            try
            {
                if (_style == null) _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Bold, wordWrap = false };
                float scale = Screen.height / 1080f;
                _style.fontSize = Mathf.RoundToInt(22 * scale);
                string text = $"DEATHMATCH  -  a death: +{Client.Gold} gold, the dead lose what they carry, the shotguns are only lent";
                Vector2 size = _style.CalcSize(new GUIContent(text));   // as wide as the line (never cut off)
                var r = new Rect(24 * scale, Screen.height * 0.30f, size.x + 4f, Mathf.Max(40 * scale, size.y + 4f));
                var old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.75f);
                GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, _style);
                GUI.color = new Color(1f, 0.55f, 0.12f, 0.95f);
                GUI.Label(r, text, _style);
                GUI.color = old;
            }
            catch (Exception e)
            {
                _failed = true;   // OnGUI runs several times a frame: said once
                Log.ErrorOnce("hud-draw", "The rules line failed and is off: " + e);
            }
        }

        bool _failed;
    }
}
