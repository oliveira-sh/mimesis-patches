// MIMESIS BetterMicrophone - the hooks into Dissonance's capture pipeline and Opus encoder
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Both frame hooks run on Dissonance's preprocessor thread, once per 10 ms frame (480 samples, 48 kHz). They never
// throw: an exception there would kill that thread and with it your voice for the rest of the game. On the first
// failure the chain turns itself off (your voice goes on unprocessed) and says so once.
//   P1  WebRtcPreprocessingPipeline.PreprocessAudioFrame (prefix): the frame as captured -> Pre, then the game runs
//       RNNoise and WebRTC on it.
//   P2  BasePreprocessingPipeline.SendSamplesToSubscribers (prefix): the cleaned frame -> Post, before it goes to the
//       encoder (what others hear), the mimics' speech recorder and the voice meter, with the game's voice detection
//       for it (SileroVadStream.RecordingVadSpeaking, set by PreprocessAudioFrame just before).
//   P3  OpusEncoder's constructor (postfix): "Very high" sends at 32 kbps (High is Dissonance's own 24 kbps).

using System;
using System.Threading;
using Dissonance;
using Dissonance.Audio.Capture;
using Dissonance.Audio.Codecs.Opus;
using HarmonyLib;

namespace BetterMicrophone
{
    static class Live
    {
        internal static readonly Meter Meter = new Meter();
        internal static readonly VoiceChain Chain = new VoiceChain(Meter);
        internal static volatile bool Broken;
        static int _lastFrameTick;

        /// <summary>Dissonance is capturing (a frame came within the last 3 s: frames come in bursts when the game runs
        /// slowly).</summary>
        internal static bool Running => Meter.Frames > 0 && unchecked(Environment.TickCount - Volatile.Read(ref _lastFrameTick)) < 3000;

        internal static void Pre(float[] frame)
        {
            Volatile.Write(ref _lastFrameTick, Environment.TickCount);
            MicTest.TapRaw(frame);
            if (Broken) return;
            try { Chain.Pre(frame, Settings.Current); }
            catch (Exception e) { Fail(e); }
        }

        /// <summary>vad: the game's voice detection for this frame (Silero when it's on, else WebRTC), set just before.</summary>
        internal static void Post(float[] frame, bool vad)
        {
            if (!Broken)
            {
                try { Chain.Post(frame, Settings.Current, vad); }
                catch (Exception e) { Fail(e); }
            }
            MicTest.TapClean(frame);
        }

        static void Fail(Exception e)
        {
            Broken = true;
            Log.ErrorOnce("chain", "The voice chain failed and is off until the game restarts (your voice goes on as the game " +
                                   "makes it): " + e);
        }
    }

    [HarmonyPatch]
    static class FrameHooks
    {
        // P1
        [HarmonyPrefix, HarmonyPatch(typeof(WebRtcPreprocessingPipeline), nameof(WebRtcPreprocessingPipeline.PreprocessAudioFrame))]
        static void BeforePreprocess(float[] frame)
        {
            if (frame != null) Live.Pre(frame);
        }

        // P2
        [HarmonyPrefix, HarmonyPatch(typeof(BasePreprocessingPipeline), nameof(BasePreprocessingPipeline.SendSamplesToSubscribers))]
        static void BeforeSend(BasePreprocessingPipeline __instance, float[] buffer)
        {
            // PreprocessAudioFrame sets SileroVadStream.RecordingVadSpeaking for this frame right before sending it.
            if (buffer != null && __instance is WebRtcPreprocessingPipeline) Live.Post(buffer, SileroVadStream.RecordingVadSpeaking);
        }
    }

    [HarmonyPatch(typeof(OpusEncoder), MethodType.Constructor, typeof(AudioQuality), typeof(FrameSize), typeof(bool))]
    static class EncoderHook
    {
        static int _logged;

        // P3. The encoder for your outgoing voice is built when voice starts (after DissonanceSetup set Quality = High).
        static void Postfix(OpusEncoder __instance, AudioQuality quality)
        {
            try
            {
                int bitrate = Settings.Enabled ? Settings.Bitrate : 0;
                if (bitrate <= 0 || __instance._encoder == null) return;
                __instance._encoder.Bitrate = bitrate;
                if (bitrate != _logged)
                {
                    _logged = bitrate;
                    Log.Info($"Voice encoder: {bitrate / 1000} kbps (the game's {quality} would be " +
                             $"{(quality == AudioQuality.High ? 24 : quality == AudioQuality.Medium ? 17 : 10)} kbps)");
                }
            }
            catch (Exception e) { Log.ErrorOnce("encoder", "Setting the voice bitrate failed: " + e.Message); }
        }
    }
}
