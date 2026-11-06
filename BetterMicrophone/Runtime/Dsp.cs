// MIMESIS BetterMicrophone - the signal chain around the game's voice preprocessor
// Author: Mohamed Darwesh (@medovanx) - https://github.com/medovanx
//
// Dissonance cleans every 10 ms frame (480 samples, 48 kHz, mono) on its own thread: RNNoise (the neural noise
// remover, which the game leaves off) and then WebRTC (noise suppression, voice detection). This chain wraps it:
//   Pre   (before both)   mic boost only: RNNoise must see the whole spectrum (measured in game: a 90 Hz low cut in
//                         front of it took its suppression of pink noise from 40 dB to almost nothing). It also decides
//                         whether you're speaking, on the input (through its own rumble filter, on a copy): noise there
//                         is steady (after the denoisers what's left of it flickers and looks like speech), speech rises
//                         above it.
//   Post  (after both)    low cut (4th-order Butterworth high-pass: rumble, desk knocks, breath pops), optional mains
//                         hum notches (50/60 Hz and harmonics), voice clarity EQ (optional), silence between words (a
//                         downward expander, with hold so word ends aren't cut), even volume (slow AGC that only learns
//                         from speech and never lifts the noise between words), and a peak limiter (no clipping).
// Speaking = the input above its own noise floor, or the game's voice detection (Silero, else WebRTC) when the caller
// has it (the live pipeline). One VoiceChain per stream: the live one on Dissonance's thread, a separate one for the
// menu's mic test. Settings come as an immutable Config snapshot; a new one is picked up at the next frame.

using System;
using System.Diagnostics;

namespace BetterMicrophone
{
    /// <summary>One biquad (RBJ cookbook), transposed direct form II in double precision.</summary>
    sealed class Biquad
    {
        double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

        public static Biquad HighPass(double fs, double f0, double q)
        {
            double w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            return new Biquad { _b0 = (1 + c) / 2 / a0, _b1 = -(1 + c) / a0, _b2 = (1 + c) / 2 / a0, _a1 = -2 * c / a0, _a2 = (1 - alpha) / a0 };
        }

        public static Biquad Notch(double fs, double f0, double q)
        {
            double w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            return new Biquad { _b0 = 1 / a0, _b1 = -2 * c / a0, _b2 = 1 / a0, _a1 = -2 * c / a0, _a2 = (1 - alpha) / a0 };
        }

        public static Biquad Peak(double fs, double f0, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            double a0 = 1 + alpha / a;
            return new Biquad { _b0 = (1 + alpha * a) / a0, _b1 = -2 * c / a0, _b2 = (1 - alpha * a) / a0, _a1 = -2 * c / a0, _a2 = (1 - alpha / a) / a0 };
        }

        public void Process(float[] x)
        {
            double b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2, z1 = _z1, z2 = _z2;
            for (int i = 0; i < x.Length; i++)
            {
                double s = x[i];
                double y = b0 * s + z1;
                z1 = b1 * s - a1 * y + z2;
                z2 = b2 * s - a2 * y;
                x[i] = (float)y;
            }
            // Flush denormals: a decaying tail in silence would otherwise slow every sample to a crawl.
            _z1 = Math.Abs(z1) < 1e-25 ? 0 : z1;
            _z2 = Math.Abs(z2) < 1e-25 ? 0 : z2;
        }
    }

    /// <summary>What the chain does, as chosen in the Patches window. Immutable: a change makes a new one.</summary>
    sealed class Config
    {
        public bool Enabled = true;
        public float BoostDb;            // mic boost, -12..+24 dB
        public int LowCutHz = 90;        // 0 = off
        public int HumHz;                // 0, 50 or 60
        public bool Clarity;
        public float GateRangeDb = -24f; // 0 = off; how far the silence between words goes down
        public bool AutoGain = true;
        public float AutoGainTargetDb = -20f;
        public float LimitDb = -1f;
    }

    /// <summary>Live readings for the Patches window (written by the audio thread, read by the UI: plain floats, a torn
    /// read only misprints one number once).</summary>
    sealed class Meter
    {
        public volatile float InDb = -120f, OutDb = -120f, GainDb, LimitDb, FloorDb = -120f;
        public volatile bool Open;
        public volatile float CostMs;
        public long Frames;
    }

    sealed class VoiceChain
    {
        const double Fs = 48000;
        const float FrameMs = 10f;
        const float SilentDb = -120f;

        // Speaking, from the input: this far above its noise floor (and not quieter than OpenMinDb).
        const float OpenAboveFloorDb = 8f;     // starts...
        const float StayAboveFloorDb = 4f;     // ...and goes on (hysteresis)
        const float OpenMinDb = -65f;
        // The input's noise floor: the quietest 100 ms block (each block's quietest frame) of the last 3 s (minimum
        // statistics): it drops at once and rises within 3 s when the noise gets louder; a word never lasts 3 s without
        // a quieter gap.
        const int FloorBlockFrames = 10, FloorBlocks = 30;
        // Silence between words.
        const float HoldMs = 280f;             // stays open this long after the last speaking frame (word ends, pauses)
        const float AttackMs = 2f, ReleaseMs = 90f;
        // Even volume.
        const float AgcUpDbPerSec = 5f, AgcDownDbPerSec = 14f;
        const float AgcMaxDb = 18f, AgcMinDb = -12f;
        const float AgcSpeechMinDb = -55f;     // only learns from speaking frames at least this loud
        // Limiter.
        const float LimiterReleaseMs = 60f;

        readonly Meter _meter;
        Config _config;
        Biquad[] _post = Array.Empty<Biquad>();
        Biquad[] _eq = Array.Empty<Biquad>();
        // Speech detection only: rumble and knocks out of a copy of the input (the sound itself isn't touched here).
        readonly Biquad _detectHp = Biquad.HighPass(Fs, 100, 0.7071);
        readonly float[] _detect = new float[480];
        // Speaking (Pre decides, Post uses it for the same frame).
        readonly float[] _blockMin = new float[FloorBlocks];
        int _block, _blockFrames, _blocksFilled;
        float _curMin = float.MaxValue, _floorDb = SilentDb;
        bool _speaking;
        // Post.
        float _gate = 1f, _holdMs;
        bool _open;
        float _agcDb, _speechDb = -30f;
        float _limEnv;
        readonly Stopwatch _clock = new Stopwatch();

        public VoiceChain(Meter meter) { _meter = meter; }

        public Meter Meter => _meter;

        /// <summary>A new settings snapshot (seen by the audio thread at a frame): the filters are rebuilt.</summary>
        void Configure(Config c)
        {
            _config = c;
            var post = new System.Collections.Generic.List<Biquad>();
            if (c.LowCutHz > 0)
            {
                // 4th-order Butterworth: two biquads with these Q values.
                post.Add(Biquad.HighPass(Fs, c.LowCutHz, 0.5412));
                post.Add(Biquad.HighPass(Fs, c.LowCutHz, 1.3066));
            }
            if (c.HumHz == 50 || c.HumHz == 60)
                for (int h = 1; h <= 4; h++) post.Add(Biquad.Notch(Fs, c.HumHz * h, 25 + 5 * h));
            _post = post.ToArray();
            _eq = c.Clarity
                ? new[] { Biquad.Peak(Fs, 250, 1.0, -2.0), Biquad.Peak(Fs, 3200, 0.9, 3.0), Biquad.Peak(Fs, 8000, 0.7, 1.5) }
                : Array.Empty<Biquad>();
        }

        /// <summary>Before RNNoise and WebRTC: the mic boost, and whether this frame is speech.</summary>
        public void Pre(float[] frame, Config c)
        {
            if (!ReferenceEquals(c, _config)) Configure(c);
            _clock.Restart();
            _meter.InDb = LevelDb(frame);
            // Digital silence (a muted mic, or the zeros Dissonance injects when the game drops samples on a slow frame)
            // isn't the room's noise: it would pin the floor far down.
            bool zero = true;
            for (int i = 0; i < frame.Length; i++)
                if (frame[i] != 0f) { zero = false; break; }
            if (c.Enabled && Math.Abs(c.BoostDb) > 0.01f)
            {
                float g = DbToGain(c.BoostDb);
                for (int i = 0; i < frame.Length; i++) frame[i] *= g;
            }
            float[] d = frame.Length == _detect.Length ? _detect : new float[frame.Length];
            Array.Copy(frame, d, frame.Length);
            _detectHp.Process(d);
            float level = LevelDb(d);
            TrackFloor(level, zero);
            _speaking = !zero && level > OpenMinDb && level > _floorDb + (_speaking ? StayAboveFloorDb : OpenAboveFloorDb);
            _clock.Stop();
        }

        /// <summary>After RNNoise and WebRTC, before the encoder, the mimics' recorder and the voice meter. vad: the
        /// game's own voice detection for this frame, when the caller has it (null: the input alone decides).</summary>
        public void Post(float[] frame, Config c, bool? vad = null)
        {
            if (!ReferenceEquals(c, _config)) Configure(c);
            _clock.Start();
            if (c.Enabled)
            {
                foreach (var f in _post) f.Process(frame);
                foreach (var f in _eq) f.Process(frame);
                bool speaking = _speaking || vad == true;
                if (speaking)
                {
                    _open = true;
                    _holdMs = HoldMs;
                }
                else if (_open)
                {
                    _holdMs -= FrameMs;
                    if (_holdMs <= 0f) _open = false;
                }
                float level = LevelDb(frame);
                float agcBefore = _agcDb;
                // Learns only from speech: the input said so (and the game's detection agrees, when there is one).
                if (c.AutoGain && _speaking && vad != false && level > AgcSpeechMinDb) LearnGain(level, c);
                if (!c.AutoGain) _agcDb = 0f;
                Apply(frame, agcBefore, c);
                Limit(frame, c);
            }
            _clock.Stop();
            _meter.OutDb = LevelDb(frame);
            _meter.Open = _open;
            _meter.GainDb = _agcDb;
            _meter.FloorDb = _floorDb;
            _meter.CostMs = (float)_clock.Elapsed.TotalMilliseconds;
            _meter.Frames++;
        }

        /// <summary>Minimum statistics over the input level (exact zeros skipped).</summary>
        void TrackFloor(float level, bool zero)
        {
            if (!zero && level < _curMin) _curMin = level;
            if (++_blockFrames >= FloorBlockFrames)
            {
                _blockMin[_block] = _curMin;
                _block = (_block + 1) % FloorBlocks;
                if (_blocksFilled < FloorBlocks) _blocksFilled++;
                _blockFrames = 0;
                _curMin = float.MaxValue;
            }
            float floor = _curMin;
            for (int i = 0; i < _blocksFilled; i++)
                if (_blockMin[i] < floor) floor = _blockMin[i];
            _floorDb = floor == float.MaxValue ? SilentDb : Math.Max(floor, SilentDb); // nothing but digital silence yet
        }

        /// <summary>The speech level (about 0.3 s of it), and the gain that brings it to the target, approached slowly.</summary>
        void LearnGain(float level, Config c)
        {
            _speechDb += (level - _speechDb) * 0.035f;
            float want = Clamp(c.AutoGainTargetDb - _speechDb, AgcMinDb, AgcMaxDb);
            float step = (want > _agcDb ? AgcUpDbPerSec : AgcDownDbPerSec) * FrameMs / 1000f;
            _agcDb = want > _agcDb ? Math.Min(want, _agcDb + step) : Math.Max(want, _agcDb - step);
        }

        /// <summary>The volume (ramped over the frame from agcBefore) and the silence between words. Between words the
        /// result is the gate's range below what came in, whatever the volume gain: it never lifts the noise.</summary>
        void Apply(float[] frame, float agcBefore, Config c)
        {
            bool gateOn = c.GateRangeDb < -0.5f;
            float range = gateOn ? DbToGain(c.GateRangeDb) : 1f;
            float a0 = DbToGain(agcBefore), a1 = DbToGain(_agcDb);
            float inc = (a1 - a0) / frame.Length;
            // Closed: the gate takes the volume gain back off too (never more than unity).
            float target = _open ? 1f : Math.Min(1f, range / Math.Max(a0, a1));
            if (!gateOn && !_open) target = Math.Min(1f, 1f / Math.Max(a0, a1)); // no gate: still never lift the noise
            float coef = (float)Math.Exp(-1.0 / ((target > _gate ? AttackMs : ReleaseMs) * 0.001 * Fs));
            float g = _gate, a = a0;
            for (int i = 0; i < frame.Length; i++)
            {
                a += inc;
                g = target + (g - target) * coef;
                frame[i] *= a * g;
            }
            _gate = g;
        }

        void Limit(float[] frame, Config c)
        {
            float ceiling = DbToGain(c.LimitDb);
            float release = (float)Math.Exp(-1.0 / (LimiterReleaseMs * 0.001 * Fs));
            float env = _limEnv, most = 1f;
            for (int i = 0; i < frame.Length; i++)
            {
                float a = Math.Abs(frame[i]);
                env = a > env ? a : env * release;
                if (env > ceiling)
                {
                    float g = ceiling / env;
                    frame[i] *= g;
                    if (g < most) most = g;
                }
                // Anything still past full scale (it can't be, but never hand the encoder a clipped sample).
                if (frame[i] > 0.999f) frame[i] = 0.999f;
                else if (frame[i] < -0.999f) frame[i] = -0.999f;
            }
            _limEnv = env;
            _meter.LimitDb = GainToDb(most);
        }

        internal static float LevelDb(float[] frame)
        {
            double sum = 0;
            for (int i = 0; i < frame.Length; i++) sum += frame[i] * frame[i];
            double rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
            return rms < 1e-6 ? SilentDb : (float)(20 * Math.Log10(rms));
        }

        static float DbToGain(float db) => (float)Math.Pow(10, db / 20.0);
        static float GainToDb(float g) => g <= 1e-6f ? SilentDb : (float)(20 * Math.Log10(g));
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
