// MIMESIS Poltergeist - the lights around you flicker (only on your screen)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Every point and spot light within Radius (your flashlight too) goes out and back on in a short random pattern,
// sometimes ending with a second of darkness. Only intensities change, and each light gets exactly its own value back
// at the end (or when the level goes away). Never during the game's own blackout. A light the game animates every frame
// (SimpleLightAnimation) has that animation paused meanwhile, or it would undo the flicker at once.

using System;
using System.Collections.Generic;
using HarmonyLib;
using Mimic.Actors;
using UnityEngine;

namespace Poltergeist
{
    static class Flicker
    {
        const float Radius = 18f;

        sealed class Lit
        {
            public Light Light;
            public float Intensity;
            public SimpleLightAnimation Anim;   // paused during the flicker (null: none)
        }

        const float MaxSeconds = 6f;          // whatever happens (a frame hitch), the lights are back after this

        static List<Lit> _lights;
        static readonly List<(bool on, float sec)> _pattern = new List<(bool on, float sec)>();
        static int _step;
        static float _stepUntil, _startedAt;
        static readonly System.Random _rng = new System.Random();

        internal static bool Running => _lights != null;

        internal static bool Start(Vector3 center)
        {
            if (_lights != null) return false;
            Light[] all;
            try { all = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None); }
            catch (Exception) { return false; }
            var list = new List<Lit>();
            foreach (var l in all)
            {
                if (l == null || !l.enabled || l.intensity <= 0f) continue;
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                if (Vector3.Distance(l.transform.position, center) > Radius) continue;
                var anim = l.GetComponent<SimpleLightAnimation>();
                list.Add(new Lit { Light = l, Intensity = l.intensity, Anim = anim != null && anim.enabled ? anim : null });
            }
            if (list.Count == 0) return false;
            foreach (var x in list) if (x.Anim != null) x.Anim.enabled = false;
            _pattern.Clear();
            int flickers = _rng.Next(3, 8);
            for (int i = 0; i < flickers; i++)
            {
                _pattern.Add((false, Rand(0.04f, 0.14f)));
                _pattern.Add((true, Rand(0.05f, 0.3f)));
            }
            if (_rng.NextDouble() < 0.45)
            {
                _pattern.Add((false, Rand(0.9f, 1.8f)));  // a moment of darkness
                _pattern.Add((true, 0.1f));
            }
            _lights = list;
            _step = -1;
            _startedAt = Time.unscaledTime;
            _stepUntil = _startedAt;
            return true;
        }

        /// <summary>Every frame (Haunt.Tick). Steps are on a timeline from the start, so a slow frame skips steps instead
        /// of stretching the flicker (at 1 frame a second, one step per frame would keep the lights out for many seconds).</summary>
        internal static void Tick()
        {
            if (_lights == null) return;
            float now = Time.unscaledTime;
            if (now - _startedAt > MaxSeconds)
            {
                Restore();
                return;
            }
            if (now < _stepUntil) return;
            bool on = true;
            while (now >= _stepUntil)
            {
                _step++;
                if (_step >= _pattern.Count)
                {
                    Restore();
                    return;
                }
                on = _pattern[_step].on;
                _stepUntil += _pattern[_step].sec;
            }
            foreach (var x in _lights)
                if (x.Light != null) x.Light.intensity = on ? x.Intensity : 0f;
        }

        /// <summary>Every light back to its own intensity (the end, or the level going away).</summary>
        internal static void Restore()
        {
            if (_lights == null) return;
            foreach (var x in _lights)
            {
                try
                {
                    if (x.Light != null) x.Light.intensity = x.Intensity;
                    if (x.Anim != null) x.Anim.enabled = true;
                }
                catch (Exception) { /* destroyed with the level */ }
            }
            _lights = null;
        }

        /// <summary>The light's own intensity while the flicker has it (false: not one of ours).</summary>
        internal static bool TryGetOwnIntensity(Light light, out float intensity)
        {
            intensity = 0f;
            if (_lights == null || light == null) return false;
            foreach (var x in _lights)
                if (ReferenceEquals(x.Light, light)) { intensity = x.Intensity; return true; }
            return false;
        }

        static float Rand(float min, float max) => (float)(min + _rng.NextDouble() * (max - min));
    }

    /// <summary>P2: turning a flashlight on copies its light into the avatar's shoulder light (used while the flashlight
    /// is in the bag). During a flicker's darkness that copy would be 0 and stay so: it gets the light's own value.</summary>
    [HarmonyPatch(typeof(ProtoActor), nameof(ProtoActor.OverrideMountedLight))]
    static class MountedLightPatch
    {
        static void Postfix(ProtoActor __instance, Light light)
        {
            try
            {
                if (__instance.mountedLight != null && Flicker.TryGetOwnIntensity(light, out float own)) __instance.mountedLight.intensity = own;
            }
            catch (Exception e) { Log.ErrorOnce("P2", "Keeping the shoulder light during a flicker failed: " + e); }
        }
    }
}
