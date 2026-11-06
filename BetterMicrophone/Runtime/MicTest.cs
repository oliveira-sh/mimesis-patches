// MIMESIS BetterMicrophone - the microphone test: record 5 s, hear it as captured and as others hear it
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Two ways to get the recording:
//   - voice is running (in a lobby or a game): both versions are taken straight from the live pipeline (Live taps),
//     the raw frame before anything and the frame Dissonance sends;
//   - otherwise (main menu): Unity's Microphone records your game microphone, and the same chain runs on it here:
//     Pre, the game's own RNNoise and WebRTC (through Dissonance's native plugin, with their own state), Post.
// Playback is a plain 2D AudioSource. Everything but the taps runs on the main thread (MicTestDriver.Update).

using System;
using Dissonance;
using Dissonance.Audio.Capture;
using UnityEngine;

namespace BetterMicrophone
{
    static class MicTest
    {
        internal enum Phase { Idle, Recording, Ready }

        const int Rate = 48000, Seconds = 5, Frame = 480;

        static float[] _raw, _clean;
        static volatile bool _tapping;
        static int _rawLen, _cleanLen;      // written by the audio thread while tapping
        static bool _fromMic;
        static string _device;
        static AudioClip _micClip;
        static float _startedAt;
        static AudioSource _source;
        static AudioClip _playClip;
        static string _note = "";

        internal static Phase State { get; private set; }

        /// <summary>For the page: what the test is doing, or its last result.</summary>
        internal static string Status
        {
            get
            {
                switch (State)
                {
                    case Phase.Recording:
                        return $"Recording... speak normally, with your usual background noise ({Mathf.Max(0f, Seconds - (Time.unscaledTime - _startedAt)):0} s)";
                    case Phase.Ready:
                        return (_source != null && _source.isPlaying ? "Playing. " : "Recorded. ") + _note;
                    default:
                        return _note;
                }
            }
        }

        internal static void Install(GameObject host)
        {
            _source = host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.loop = false;
            _source.ignoreListenerPause = true;
            _source.bypassEffects = true;
            _source.bypassListenerEffects = true;
            _source.bypassReverbZones = true;
            host.AddComponent<MicTestDriver>();
        }

        internal static void Record()
        {
            if (State == Phase.Recording) return;
            StopPlaying();
            _raw = new float[Rate * Seconds];
            _clean = new float[Rate * Seconds];
            _rawLen = _cleanLen = 0;
            _startedAt = Time.unscaledTime;
            if (Live.Running)
            {
                // Voice is running: take both straight from the pipeline.
                _fromMic = false;
                _tapping = true;
                State = Phase.Recording;
                Log.Info("Mic test: recording from your live voice");
                return;
            }
            try
            {
                _device = MicName();
                if (Microphone.IsRecording(_device))
                {
                    // The game's voice chat has it (between frames of a slow moment): never take it away from it.
                    _fromMic = false;
                    _tapping = true;
                    State = Phase.Recording;
                    Log.Info("Mic test: your microphone is in use by the voice chat: recording from your live voice");
                    return;
                }
                Microphone.GetDeviceCaps(_device, out int min, out int max);
                int rate = min == 0 && max == 0 ? Rate : Mathf.Clamp(Rate, min, max);
                _micClip = Microphone.Start(_device, false, Seconds + 1, rate);
                if (_micClip == null) throw new Exception("Microphone.Start returned nothing");
                _fromMic = true;
                State = Phase.Recording;
                Log.Info($"Mic test: recording from '{_device ?? "default microphone"}' at {rate} Hz");
            }
            catch (Exception e)
            {
                State = Phase.Idle;
                _note = "Couldn't open your microphone: " + e.Message;
                Log.Warn("Mic test: " + _note);
            }
        }

        internal static void Play(bool cleaned)
        {
            if (State != Phase.Ready) return;
            var data = cleaned ? _clean : _raw;
            int len = cleaned ? _cleanLen : _rawLen;
            if (data == null || len <= 0) return;
            StopPlaying();
            _playClip = AudioClip.Create(cleaned ? "BetterMicrophone_Cleaned" : "BetterMicrophone_Original", len, 1, Rate, false);
            var copy = new float[len];
            Array.Copy(data, copy, len);
            ClipData(_playClip, "SetData", copy);
            _source.clip = _playClip;
            _source.volume = 1f;
            _source.Play();
        }

        internal static void StopPlaying()
        {
            if (_source != null && _source.isPlaying) _source.Stop();
            if (_playClip != null)
            {
                UnityEngine.Object.Destroy(_playClip);
                _playClip = null;
            }
        }

        // ======================================================================== taps (Dissonance's audio thread)

        internal static void TapRaw(float[] frame)
        {
            if (!_tapping) return;
            var buf = _raw;
            int n = Math.Min(frame.Length, buf.Length - _rawLen);
            if (n > 0)
            {
                Array.Copy(frame, 0, buf, _rawLen, n);
                _rawLen += n;
            }
        }

        internal static void TapClean(float[] frame)
        {
            if (!_tapping) return;
            var buf = _clean;
            int n = Math.Min(frame.Length, buf.Length - _cleanLen);
            if (n > 0)
            {
                Array.Copy(frame, 0, buf, _cleanLen, n);
                _cleanLen += n;
            }
            if (_cleanLen >= buf.Length) _tapping = false;
        }

        // ======================================================================== main thread

        internal static void Update()
        {
            if (State != Phase.Recording) return;
            float elapsed = Time.unscaledTime - _startedAt;
            if (!_fromMic)
            {
                if (_tapping && elapsed < Seconds + 2f) return;
                _tapping = false;
                Finish("from your live voice (exactly what the others hear)");
                return;
            }
            if (elapsed < Seconds + 0.2f) return;
            try
            {
                int pos = Microphone.GetPosition(_device);
                Microphone.End(_device);
                int got = pos > 0 ? Math.Min(pos, _micClip.samples) : _micClip.samples;
                var mono = new float[got * _micClip.channels];
                ClipData(_micClip, "GetData", mono);
                if (_micClip.channels > 1) mono = Downmix(mono, _micClip.channels);
                var at48 = _micClip.frequency == Rate ? mono : Resample(mono, _micClip.frequency, Rate);
                int len = Math.Min(at48.Length / Frame * Frame, _raw.Length);
                Array.Copy(at48, _raw, len);
                _rawLen = len;
                string how = ProcessOffline(_raw, _clean, len);
                _cleanLen = len;
                Finish(how);
            }
            catch (Exception e)
            {
                State = Phase.Idle;
                _note = "The recording failed: " + e.Message;
                Log.Warn("Mic test: " + e);
            }
            finally
            {
                if (_micClip != null) UnityEngine.Object.Destroy(_micClip);
                _micClip = null;
            }
        }

        static void Finish(string how)
        {
            State = Phase.Ready;
            float rawDb = VoiceChain.LevelDb(Slice(_raw, _rawLen)), cleanDb = VoiceChain.LevelDb(Slice(_clean, _cleanLen));
            _note = $"{_rawLen / (float)Rate:0.0} s {how}. Average level {rawDb:0} dB as captured, {cleanDb:0} dB cleaned.";
            Log.Info("Mic test: " + _note);
        }

        /// <summary>The live chain's processing on a recording, frame by frame: Pre, the game's RNNoise and WebRTC (as
        /// it sets them up for you), Post. Returns how it went, for the status line.</summary>
        static string ProcessOffline(float[] raw, float[] clean, int len)
        {
            var config = Settings.Current;
            var chain = new VoiceChain(new Meter());
            IntPtr rn = IntPtr.Zero, web = IntPtr.Zero;
            string native = "";
            var vs = Dissonance.Config.VoiceSettings.Instance;
            bool rnOn = config.Enabled || vs.BackgroundSoundRemovalEnabled;
            float rnAmount = config.Enabled ? Settings.RnnoiseAmount : vs.BackgroundSoundRemovalAmount;
            var ns = config.Enabled ? DissonanceSetup.NsLevel(Settings.Strength) : vs.DenoiseAmount;
            try
            {
                if (rnOn)
                {
                    rn = AudioPluginDissonanceNative.Dissonance_CreateRnnoiseState();
                    AudioPluginDissonanceNative.Dissonance_SetMinBandGain(rn, (float)Math.Pow(10.0, Mathf.Lerp(0f, -80f, rnAmount) / 20f));
                }
                web = AudioPluginDissonanceNative.Dissonance_CreatePreprocessor(ns, AecSuppressionLevels.Disabled, vs.AecDelayAgnostic,
                    vs.AecExtendedFilter, vs.AecRefinedAdaptiveFilter, AecmRoutingMode.Disabled, vs.AecmComfortNoise);
            }
            catch (Exception e)
            {
                native = " (without the game's RNNoise/WebRTC: " + e.Message + ")";
                Log.WarnOnce("offline-native", "Mic test: the game's native voice processing isn't available here: " + e.Message);
            }
            var frame = new float[Frame];
            try
            {
                for (int at = 0; at + Frame <= len; at += Frame)
                {
                    Array.Copy(raw, at, frame, 0, Frame);
                    chain.Pre(frame, config);
                    if (rn != IntPtr.Zero) AudioPluginDissonanceNative.Dissonance_RnnoiseProcessFrame(rn, Frame, Rate, frame, frame);
                    if (web != IntPtr.Zero)
                        AudioPluginDissonanceNative.Dissonance_PreprocessCaptureFrame(web, Rate, frame, frame, 0);
                    chain.Post(frame, config);
                    Array.Copy(frame, 0, clean, at, Frame);
                }
            }
            finally
            {
                if (rn != IntPtr.Zero) AudioPluginDissonanceNative.Dissonance_DestroyRnnoiseState(rn);
                if (web != IntPtr.Zero) AudioPluginDissonanceNative.Dissonance_DestroyPreprocessor(web);
            }
            return "from your microphone, cleaned here as in game" + native;
        }

        /// <summary>The game's microphone (the one its voice chat uses), or null for the system default.</summary>
        static string MicName()
        {
            try
            {
                var comms = UnityEngine.Object.FindAnyObjectByType<DissonanceComms>();
                string name = comms != null ? comms.MicrophoneName : null;
                if (!string.IsNullOrEmpty(name) && Array.IndexOf(Microphone.devices, name) >= 0) return name;
            }
            catch (Exception) { /* the default one */ }
            return null;
        }

        /// <summary>AudioClip.GetData/SetData(float[], int): Unity 6 adds Span overloads that this project's reference set
        /// can't resolve, so the array ones are called by reflection.</summary>
        static void ClipData(AudioClip clip, string method, float[] data)
        {
            var m = typeof(AudioClip).GetMethod(method, new[] { typeof(float[]), typeof(int) });
            if (m == null) throw new MissingMethodException("AudioClip." + method + "(float[], int)");
            m.Invoke(clip, new object[] { data, 0 });
        }

        static float[] Slice(float[] a, int len)
        {
            if (a == null || len <= 0) return Array.Empty<float>();
            var s = new float[len];
            Array.Copy(a, s, len);
            return s;
        }

        static float[] Downmix(float[] x, int channels)
        {
            var y = new float[x.Length / channels];
            for (int i = 0; i < y.Length; i++)
            {
                float s = 0f;
                for (int c = 0; c < channels; c++) s += x[i * channels + c];
                y[i] = s / channels;
            }
            return y;
        }

        static float[] Resample(float[] x, int from, int to)
        {
            if (x.Length == 0) return x;
            var y = new float[(int)((long)x.Length * to / from)];
            double step = (double)from / to;
            for (int i = 0; i < y.Length; i++)
            {
                double p = i * step;
                int k = (int)p;
                float t = (float)(p - k);
                float a = x[Math.Min(k, x.Length - 1)], b = x[Math.Min(k + 1, x.Length - 1)];
                y[i] = a + (b - a) * t;
            }
            return y;
        }
    }

    sealed class MicTestDriver : MonoBehaviour
    {
        void Update()
        {
            try { MicTest.Update(); }
            catch (Exception e) { Log.ErrorOnce("mictest", "Mic test failed: " + e); }
        }
    }
}
