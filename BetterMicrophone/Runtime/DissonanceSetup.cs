// MIMESIS BetterMicrophone - the game's own voice settings it changes (Dissonance's VoiceSettings)
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Dissonance has RNNoise built in ("background sound removal"), off in this game. While BetterMicrophone is on:
//   RNNoise on, at the strength's depth (Light 0.5 ... Max 1.0 = up to -80 dB per band): measured in game, 25-50 dB off
//   fan, pink and white noise at ordinary levels, but next to nothing on quiet steady hiss;
//   WebRTC noise suppression at VeryHigh as the game has it (High for Light): it takes 13-20 dB off steady noise,
//   including the quiet hiss RNNoise leaves;
//   Opus quality High (24 kbps, read when voice starts; Very high raises the encoder to 32 kbps in Hooks).
// The backing fields are set directly and PropertyChanged raised, as the public setters do, but without their
// PlayerPrefs save: turning the patch off (or removing it) leaves the game's own values. Dissonance's preprocessor
// listens to PropertyChanged and reconfigures RNNoise and WebRTC live, from the main thread, under its own lock.

using System;
using Dissonance;
using Dissonance.Audio.Capture;
using Dissonance.Config;

namespace BetterMicrophone
{
    static class DissonanceSetup
    {
        static bool _captured;
        static int _origBg, _origDenoise;
        static float _origBgAmount;
        static AudioQuality _origQuality;

        internal static void Apply()
        {
            try
            {
                var vs = VoiceSettings.Instance;
                if (!_captured)
                {
                    _origBg = vs._bgSoundRemovalEnabled;
                    _origBgAmount = vs._bgSoundRemovalAmount;
                    _origDenoise = vs._denoiseAmount;
                    _origQuality = vs._quality;
                    _captured = true;
                    Log.Info($"Game voice settings: RNNoise {(_origBg != 0 ? "on" : "off")} ({_origBgAmount:0.00}), " +
                             $"noise suppression {(NoiseSuppressionLevels)_origDenoise}, quality {_origQuality}");
                }
                bool on = Settings.Enabled;
                Set(vs, ref vs._bgSoundRemovalEnabled, on ? 1 : _origBg, "BackgroundSoundRemovalEnabled");
                SetF(vs, ref vs._bgSoundRemovalAmount, on ? Settings.RnnoiseAmount : _origBgAmount, "BackgroundSoundRemovalAmount");
                Set(vs, ref vs._denoiseAmount, on ? (int)NsLevel(Settings.Strength) : _origDenoise, "DenoiseAmount");
                var quality = on && Settings.Quality != SendQuality.Game ? AudioQuality.High : _origQuality;
                if (vs._quality != quality)
                {
                    vs._quality = quality;
                    vs.OnPropertyChanged("Quality");
                }
            }
            catch (Exception e) { Log.ErrorOnce("dissonance", "Changing the game's voice settings failed (game update?): " + e); }
        }

        /// <summary>RNNoise misses quiet steady noise (measured in game: -1 dB on hiss at -40 dB) where WebRTC still takes
        /// 13 dB off at VeryHigh, the game's own level: kept there, a notch lower for Light.</summary>
        internal static NoiseSuppressionLevels NsLevel(Strength s) =>
            s == Strength.Light ? NoiseSuppressionLevels.High : NoiseSuppressionLevels.VeryHigh;

        static void Set(VoiceSettings vs, ref int field, int value, string property)
        {
            if (field == value) return;
            field = value;
            vs.OnPropertyChanged(property);
        }

        static void SetF(VoiceSettings vs, ref float field, float value, string property)
        {
            if (Math.Abs(field - value) < 0.0001f) return;
            field = value;
            vs.OnPropertyChanged(property);
        }
    }
}
