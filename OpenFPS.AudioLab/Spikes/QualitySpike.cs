using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --quality: what the mixer does to a sound on its way out, with known signals through the REAL
/// provider (docs/AUDIO_QUALITY_2026-10-06.md). Every capture is 32-bit float, taken at the master
/// (post) and, for scenes, also just before the master limiter (pre). The numbers are worked out by
/// tools/audio_quality.py, which reads the captures and the schedule files written beside them.
///
///   --quality resampler [methods=linear,cubic,spline] [out=DIR]
///       Tone ladders through one voice each: a 48 kHz buffer at pitch 1 (every TransientSynth sound),
///       a 44.1 kHz buffer at pitch 1.004 (a walking listener's Doppler), a 24 kHz buffer (thunder),
///       and white noise at pitch 1 and 1.004. One capture per resampler.
///   --quality orbit [rates=0,90,360] [out=DIR]
///       A 300 Hz tone and pink noise circling the head at two metres: frame-rate steps in the binaural
///       stage show as a residual at multiples of the 1024-sample block.
///   --quality scene=park|street|thunder|oneshots|fountain [sec=] [out=DIR] [tag=] [keepout] [legacy] [walk=1]
///       A typical scene, captured before and after the master limiter: Elm Park's fountain and trees
///       with the listener walking in; four city cars passing on a street, with their front voices and
///       facade echoes; a ground flash 3 km away; the synthesised one-shots (legacy: as registered
///       before 10-06); the fountain alone. keepout keeps the 16-bit copy the output was handed.
///   --quality echo [modes=moving,still,borrowed,approach,front,frontstill]
///       A car's reflection, borrowed voice and front voice with the car itself muted: skips and steps.
///   --quality ceiling [sec=]     how often each voice's SoftCeiling bends its samples
///   --quality quant              sixteen bits against float on TransientSynth renders, by level
///   --quality lsb                a tone two bits tall: the float mix against the 16 bits it is handed out as (OPENFPS_DITHER=1: dithered)
///   --quality output             the real output with nothing playing, to read its format off the server
///   --quality thunderfile        a strike's file written with linear and with band-limited upsampling
///
/// OPENFPS_RESAMPLER and the other switches in MixerQuality are read when each mixer is made, so
/// one run can make one of each.
/// </summary>
public static class QualitySpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string dir = Str(args, "out") ?? "/tmp/openfps-quality";
        Directory.CreateDirectory(dir);
        string tag = Str(args, "tag") ?? "default";
        if (args.Contains("resampler")) return Resampler(args, dir);
        if (args.Contains("orbit")) return Orbit(args, dir, tag);
        if (args.Contains("quant")) return Quant();
        if (args.Contains("echo")) return Echo(args, dir, tag);
        if (args.Contains("ceiling")) return Ceiling(args);
        if (args.Contains("thunderfile"))
        {
            // The thunder lab's files were taken from the render's 24 kHz to 48 kHz by linear
            // interpolation; the same strike written both ways, mono, float, unnormalised.
            var air = new Thunder.Air(15f, 0.95f, 1013.25f, new Vector3(15f, 0f, -10f), 0.8f);
            foreach (float km in new[] { 1f, 3f })
            {
                var parts = Thunder.Render(ThunderSpike.Ground(km * 1000f, 45f, 3, 2), new Vector3(0f, 1.7f, 0f), air);
                foreach (bool linear in new[] { true, false })
                {
                    float start = parts.Min(p => p.StartSeconds);
                    float end = parts.Max(p => p.StartSeconds + p.Seconds);
                    var mix = new float[(int)((end - start) * 48000) + 48000];
                    foreach (var p in parts)
                    {
                        var y = linear ? ThunderSpike.To48kLinear(p) : ThunderSpike.To48k(p);
                        int off = (int)((p.StartSeconds - start) * 48000);
                        for (int i = 0; i < y.Length && off + i < mix.Length; i++) mix[off + i] += y[i];
                    }
                    float peak = mix.Max(MathF.Abs);
                    for (int i = 0; i < mix.Length; i++) mix[i] *= 0.5f / MathF.Max(1e-9f, peak);
                    string path = Path.Combine(dir, $"thunderfile-{km:F0}km-{(linear ? "linear" : "bandlimited")}.wav");
                    WriteFloatWav(path, mix, 48000);
                    Console.WriteLine($"  {path}: parts at {parts[0].SampleRate} Hz");
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double secs = 0;
                foreach (var p in parts) { MixerQuality.Resample(p.Pressure, p.SampleRate, 44100); secs += p.Seconds; }
                Console.WriteLine($"  {km:F0} km: {parts.Count} parts, {secs:F1} s of sound; band-limited to 44.1 kHz in {sw.ElapsedMilliseconds} ms on one core");
            }
            return 0;
        }
        if (args.Contains("lsb"))
        {
            // A 1 kHz tone two bits tall through the whole mixer: the float tap against the sixteen
            // bits FMOD hands the output (the WAV writer's copy goes through the same conversion).
            using var s = new Session(dir, "lsb", pre: false);
            int sr = 44100;
            var tone = new float[sr * 6];
            for (int i = 0; i < tone.Length; i++) tone[i] = 2e-5f * (float)Math.Sin(2 * Math.PI * 1000.0 * i / sr);
            s.P.RegisterSynthesisedSoundFloat("lab:quality:lsb", tone, sr);
            s.P.PlaySpatialSound(Plain(-1500, "lab:quality:lsb", new Vector3(0f, 0f, 2f), 1f, 1f, PlaybackMode.Single));
            s.Pump(5.0);
            s.Dispose();
            Console.WriteLine($"  {dir}/lsb.post.wav (float) and lsb.fmodout.wav (what the output is handed)");
            return 0;
        }
        if (args.Contains("output"))
        {
            // The real output, with nothing playing: for reading the stream's format off the sound
            // server while it is open (pactl list sink-inputs). No voice is started and the dither is
            // left out, so what it plays is digital silence.
            Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
            Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", null);
            var p = new FmodAudioProvider();
            if (!p.Initialize()) return 1;
            for (int i = 0; i < 400; i++) { p.Update(); Thread.Sleep(10); }
            p.Dispose();
            return 0;
        }
        string? scene = Str(args, "scene");
        if (scene != null) return Scene(scene, args, dir, tag);
        Console.WriteLine("  --quality resampler | orbit | scene=park|street|thunder | quant  (see QualitySpike)");
        return 1;
    }

    // ── A mixer session: the provider, silent (the WAV writer replaces the sound card), its master
    // captured in float. ─────────────────────────────────────────────────────────────────────────

    private sealed class Session : IDisposable
    {
        public readonly FmodAudioProvider P;
        public readonly System.Diagnostics.Stopwatch Clock = new();
        private readonly string _out;
        public readonly List<float> Cpu = new();

        public Session(string dir, string name, bool pre)
        {
            _out = Path.Combine(dir, name + ".fmodout.wav");
            Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", _out);
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(dir, name + ".post.wav"));
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_PRE", pre ? Path.Combine(dir, name + ".pre.wav") : null);
            P = new FmodAudioProvider();
            if (!P.Initialize()) throw new InvalidOperationException("provider init failed");
            P.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
            for (int i = 0; i < 20; i++) { P.Update(); Thread.Sleep(5); }
            Clock.Start();
        }

        /// <summary>Runs the game's audio loop at its own 250 Hz for <paramref name="seconds"/>,
        /// calling <paramref name="each"/> first on every pass with the session's time.</summary>
        public void Pump(double seconds, Action<double>? each = null)
        {
            double end = Clock.Elapsed.TotalSeconds + seconds;
            double nextCpu = 0;
            while (Clock.Elapsed.TotalSeconds < end)
            {
                double t = Clock.Elapsed.TotalSeconds;
                each?.Invoke(t);
                P.Update();
                if (t >= nextCpu) { nextCpu = t + 0.25; float c = P.DspCpuPercent(); if (float.IsFinite(c)) Cpu.Add(c); }
                Thread.Sleep(4);
            }
        }

        public void Dispose()
        {
            P.Dispose();
            // The WAV writer's file is the sound card's copy of the post capture; only its format is
            // of interest, and that is reported once by the resampler run.
            Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", null);
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", null);
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_PRE", null);
        }

        public void DropSoundCardCopy() { try { File.Delete(_out); } catch { } }
        public string SoundCardCopy => _out;
    }

    private static SpatialEmitter Plain(int id, string sound, Vector3 at, float volume, float pitch, PlaybackMode mode) => new()
    {
        EntityId = id, SoundId = sound, Mode = mode, Type = EmitterType.WorldLocked,
        Position = at, ApparentPosition = at, Volume = volume, MinDistance = 2f, Range = 200f, Pitch = pitch,
        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f, EqLow = 1f, EqMid = 1f, EqHigh = 1f,
        ApertureFactor = 1f, Essential = true, IsEvent = mode == PlaybackMode.Single, LevelDb = 80f, TargetRegionId = -1,
    };

    // ── The resampler ─────────────────────────────────────────────────────────────────────────

    private static float[] Ladder(int rate, float[] hz, float amp, float toneSec = 0.6f, float gapSec = 0.25f)
    {
        int tone = (int)(toneSec * rate), gap = (int)(gapSec * rate), fade = (int)(0.01f * rate);
        var buf = new float[hz.Length * (tone + gap) + gap];
        int at = gap;
        foreach (float f in hz)
        {
            for (int i = 0; i < tone; i++)
            {
                float w = i < fade ? 0.5f - 0.5f * MathF.Cos(MathF.PI * i / fade)
                        : i >= tone - fade ? 0.5f - 0.5f * MathF.Cos(MathF.PI * (tone - i) / fade) : 1f;
                buf[at + i] = amp * w * (float)Math.Sin(2 * Math.PI * f * i / rate);
            }
            at += tone + gap;
        }
        return buf;
    }

    private static float[] Noise(int rate, float seconds, float amp, int seed)
    {
        var rng = new Random(seed);
        var buf = new float[(int)(seconds * rate)];
        int fade = (int)(0.02f * rate);
        for (int i = 0; i < buf.Length; i++)
        {
            float w = Math.Min(1f, Math.Min(i, buf.Length - 1 - i) / (float)fade);
            buf[i] = amp * w * (float)(rng.NextDouble() * 2 - 1);
        }
        return buf;
    }

    private static int Resampler(string[] args, string dir)
    {
        var methods = (Str(args, "methods") ?? "linear,cubic,spline").Split(',');
        var cases = new (string Name, int Rate, float Pitch, float[] Hz)[]
        {
            ("r48_p1", 48000, 1f, new[] { 1000f, 5000f, 10000f, 15000f, 18000f, 20000f, 23000f }),
            ("r44_p1004", 44100, 1.004f, new[] { 1000f, 5000f, 10000f, 15000f, 18000f, 20000f }),
            ("r24_p1", 24000, 1f, new[] { 500f, 2000f, 4000f, 8000f, 11000f }),
        };
        foreach (string m in methods)
        {
            Environment.SetEnvironmentVariable("OPENFPS_RESAMPLER", m);
            using var s = new Session(dir, "resampler-" + m, pre: false);
            var sched = new List<string> { "case,rate,pitch,start_s,seconds,freqs" };
            int id = -400;
            foreach (var c in cases)
            {
                var pcm = Ladder(c.Rate, c.Hz, 0.05f);
                string sid = $"lab:quality:{c.Name}";
                s.P.RegisterSynthesisedSoundFloat(sid, pcm, c.Rate);
                double start = s.Clock.Elapsed.TotalSeconds;
                s.P.PlaySpatialSound(Plain(id--, sid, new Vector3(0f, 0f, 2f), 1f, c.Pitch, PlaybackMode.Single));
                double len = pcm.Length / (double)c.Rate / c.Pitch;
                sched.Add($"{c.Name},{c.Rate},{c.Pitch.ToString(CultureInfo.InvariantCulture)},{start.ToString("F3", CultureInfo.InvariantCulture)},{len.ToString("F3", CultureInfo.InvariantCulture)},{string.Join(' ', c.Hz)}");
                s.Pump(len + 0.6);
            }
            foreach (float pitch in new[] { 1f, 1.004f })
            {
                string sid = $"lab:quality:noise{pitch}";
                var pcm = Noise(44100, 3f, 0.05f, 11);
                s.P.RegisterSynthesisedSoundFloat(sid, pcm, 44100);
                double start = s.Clock.Elapsed.TotalSeconds;
                s.P.PlaySpatialSound(Plain(id--, sid, new Vector3(0f, 0f, 2f), 1f, pitch, PlaybackMode.Single));
                double len = 3.0 / pitch;
                sched.Add($"noise_p{pitch.ToString(CultureInfo.InvariantCulture)},44100,{pitch.ToString(CultureInfo.InvariantCulture)},{start.ToString("F3", CultureInfo.InvariantCulture)},{len.ToString("F3", CultureInfo.InvariantCulture)},");
                s.Pump(len + 0.6);
            }
            File.WriteAllLines(Path.Combine(dir, $"resampler-{m}.schedule.csv"), sched);
            float cpu = s.Cpu.Count > 0 ? s.Cpu.Average() : float.NaN;
            Console.WriteLine($"  resampler {m}: mixer dsp {cpu:F1} % on a near-idle mix (one voice)");
            if (m == methods[0]) Console.WriteLine($"  the sound card's copy: {DescribeWav(s.SoundCardCopy)}");
            s.Dispose();
            s.DropSoundCardCopy();
        }
        Environment.SetEnvironmentVariable("OPENFPS_RESAMPLER", null);
        Console.WriteLine($"  captures in {dir}; measure with tools/audio_quality.py resampler {dir}");
        return 0;
    }

    // ── The binaural stage, moving ─────────────────────────────────────────────────────────────

    private static int Orbit(string[] args, string dir, string tag)
    {
        var rates = (Str(args, "rates") ?? "0,90,360").Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        int sr = 44100;
        // Long one-shots rather than loops: a registered buffer is made LOOP_OFF.
        var tone = new float[sr * 8];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.05f * (float)Math.Sin(2 * Math.PI * 300.0 * i / sr);
        var pink = Pink(sr, 8f, 0.05f, 5);
        foreach (var (kind, pcm) in new[] { ("tone", tone), ("pink", pink) })
            foreach (float rate in rates)
            {
                string name = $"orbit-{tag}-{kind}-{rate:F0}";
                using var s = new Session(dir, name, pre: false);
                string sid = $"lab:quality:orbit:{kind}";
                s.P.RegisterSynthesisedSoundFloat(sid, pcm, sr);
                const int Id = -500;
                Vector3 At(double t)
                {
                    double a = rate * Math.PI / 180.0 * t;
                    return new Vector3(2f * (float)Math.Sin(a), 0f, 2f * (float)Math.Cos(a));
                }
                var e = Plain(Id, sid, At(0), 1f, 1f, PlaybackMode.Single);
                s.P.PlaySpatialSound(e);
                double t0 = s.Clock.Elapsed.TotalSeconds;
                s.Pump(6.0, t =>
                {
                    var p = At(t - t0);
                    e.Position = p; e.ApparentPosition = p;
                    s.P.UpdateSpatialAttributes(e);
                });
                s.Dispose();
                s.DropSoundCardCopy();
                Console.WriteLine($"  {name}: written");
            }
        Console.WriteLine($"  measure with tools/audio_quality.py orbit {dir}");
        return 0;
    }

    private static float[] Pink(int rate, float seconds, float amp, int seed)
    {
        var rng = new Random(seed);
        var buf = new float[(int)(seconds * rate)];
        float b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, peak = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            float w = (float)(rng.NextDouble() * 2 - 1);
            b0 = 0.99886f * b0 + w * 0.0555179f; b1 = 0.99332f * b1 + w * 0.0750759f;
            b2 = 0.96900f * b2 + w * 0.1538520f; b3 = 0.86650f * b3 + w * 0.3104856f;
            b4 = 0.55000f * b4 + w * 0.5329522f; b5 = -0.7616f * b5 - w * 0.0168980f;
            buf[i] = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362f; b6 = w * 0.115926f;
            peak = MathF.Max(peak, MathF.Abs(buf[i]));
        }
        for (int i = 0; i < buf.Length; i++) buf[i] *= amp / peak * 3f;
        return buf;
    }

    // ── Typical scenes ─────────────────────────────────────────────────────────────────────────

    private static int Scene(string scene, string[] args, string dir, string tag)
    {
        float sec = float.TryParse(Str(args, "sec"), NumberStyles.Float, CultureInfo.InvariantCulture, out float sv) ? sv : 24f;
        string name = $"scene-{scene}-{tag}";
        using var s = new Session(dir, name, pre: true);
        switch (scene)
        {
            case "park": Park(s, sec); break;
            case "fountain":
            {
                // The fountain alone, two metres off, the listener still (walk=1: walking past it at
                // 1.2 m/s, so every sample goes through the resampler): the same model the nature
                // renders write straight to a file, through the whole mixer.
                bool walk = args.Contains("walk=1");
                var v = Nature(-650, "water:park_fountain", new Vector3(0f, 0.6f, 2f));
                s.Pump(sec, t =>
                {
                    var ear = walk ? new Vector3(-6f + 1.2f * (float)(t % 10.0), 1.7f, 0f) : new Vector3(0f, 1.7f, 0f);
                    s.P.UpdateListener(ear, Quaternion.Identity, walk ? new Vector3(1.2f, 0f, 0f) : Vector3.Zero, -1);
                    s.P.PlaySpatialSound(v);
                });
                break;
            }
            case "street": Street(s, sec); break;
            case "thunder": ThunderScene(s, sec, !args.Contains("legacy")); break;
            case "oneshots": OneShots(s, sec, args.Contains("legacy")); break;
            default: Console.WriteLine($"  no scene '{scene}'"); return 1;
        }
        float cpu = s.Cpu.Count > 0 ? s.Cpu.Average() : float.NaN;
        s.Dispose();
        // keepout: keep the sixteen bits the output was handed (dither and all), not just the float tap.
        if (!args.Contains("keepout")) s.DropSoundCardCopy();
        Console.WriteLine($"  {name}: {sec:F0} s, mixer dsp {cpu:F1} % (resampler {MixerQuality.Resampler}, dither {(MasterDither.Enabled ? "on" : "off")}); {dir}/{name}.pre.wav and .post.wav");
        return 0;
    }

    private static SpatialEmitter Nature(int id, string key, Vector3 at)
    {
        string kind = key[..key.IndexOf(':')], preset = key[(key.IndexOf(':') + 1)..];
        var (level, extent, headroom) = kind switch
        {
            "water" => (WaterFeatureSpec.ByName(preset).SourceLevelDb, WaterFeatureSpec.ByName(preset).ExtentMetres, WaterFeatureSpec.ByName(preset).PeakHeadroomDb),
            "foliage" => (FoliageSpec.ByName(preset).SourceLevelDb, FoliageSpec.ByName(preset).ExtentMetres, FoliageSpec.ByName(preset).PeakHeadroomDb),
            "fire" => (FireSpec.ByName(preset).SourceLevelDb, FireSpec.ByName(preset).ExtentMetres, FireSpec.ByName(preset).PeakHeadroomDb),
            _ => throw new ArgumentException(key),
        };
        var (gain, reference) = Loudness.Place(level, extent);
        return new SpatialEmitter
        {
            EntityId = id, Type = EmitterType.EntityAttached, IsSynth = true, PhysicalKey = key, EngineRunning = true,
            SoundId = key, Mode = PlaybackMode.LoopOne,
            Position = at, ApparentPosition = at, Direction = Vector3.UnitZ,
            Volume = gain * PhysicalVoiceState.HeadroomGain(headroom), MinDistance = reference, ExtentMetres = extent,
            Range = MathF.Max(60f, Loudness.AudibleRange(level)), TargetRegionId = -1, LevelDb = level,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
        };
    }

    /// <summary>Elm Park: the fountain eight metres ahead of where the walk ends and six trees round
    /// it. The listener walks in from twelve metres back at a walking pace for ten seconds, then
    /// stands. Wind as the field has it.</summary>
    private static void Park(Session s, float sec)
    {
        var voices = new List<SpatialEmitter> { Nature(-600, "water:park_fountain", new Vector3(0f, 0.6f, 8f)) };
        var trees = new[] { new Vector3(-9f, 0f, 12f), new Vector3(8f, 0f, 15f), new Vector3(-14f, 0f, -3f),
                            new Vector3(12f, 0f, -6f), new Vector3(4f, 0f, -16f), new Vector3(-5f, 0f, 24f) };
        for (int k = 0; k < trees.Length; k++) voices.Add(Nature(-601 - k, "foliage:park_tree", trees[k]));
        var from = new Vector3(0f, 1.7f, -12f);
        const float Walk = 1.2f;
        s.Pump(sec, t =>
        {
            float walked = (float)Math.Min(t, 10.0) * Walk;
            var ear = from + new Vector3(0f, 0f, walked);
            var vel = t < 10.0 ? new Vector3(0f, 0f, Walk) : Vector3.Zero;
            s.P.UpdateListener(ear, Quaternion.Identity, vel, -1);
            foreach (var v in voices) s.P.PlaySpatialSound(v);
        });
    }

    /// <summary>A city street: the listener on the pavement three metres from the near lane, a facade six
    /// metres behind and another across the road; a compact, a midsize, a bus and a straight-six go past
    /// at 35-50 km/h in both directions. Each car as the client voices one close to: its engine at the
    /// tailpipe, the front of it as a voice of its own (EngineTapState) within 25 m, and its reflection
    /// off each facade (EngineEchoState) placed at the mirror image.</summary>
    private static void Street(Session s, float sec)
    {
        var ear = new Vector3(-3f, 1.7f, 0f);
        s.P.UpdateListener(ear, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.Zero, -1);
        var cars = new (string Key, float Kmh, float Lane, float Start, float Extent)[]
        {
            ("i4_compact", 45f, 1.5f, 0.5f, 3.5f),
            ("transit_bus", 35f, -1.5f, 3.0f, 10f),
            ("i4_midsize", 50f, 1.5f, 9.0f, 3.5f),
            ("i6_street", 40f, -1.5f, 13.0f, 3.5f),
        };
        float[] facades = { -9f, 13f };
        const float C = 343f;
        var fronted = new bool[cars.Length];
        s.Pump(sec, t =>
        {
            for (int k = 0; k < cars.Length; k++)
            {
                var c = cars[k];
                int id = -700 - k;
                float v = c.Kmh / 3.6f;
                float dir = c.Lane > 0 ? 1f : -1f;
                float along = -dir * 90f + dir * v * (float)Math.Max(0.0, t - c.Start);
                if (t < c.Start || MathF.Abs(along) > 95f)
                {
                    s.P.StopSound(id); s.P.StopSound(id - 50);
                    for (int w = 0; w < facades.Length; w++) s.P.StopSound(id - 100 - w);
                    continue;
                }
                var p = new Vector3(c.Lane, 0.4f, along);
                var vel = new Vector3(0f, 0f, dir * v);
                var profile = MachineRegistry.VehicleFor(c.Key);
                var (gain, reference) = Loudness.Place(profile.SourceLevelDb, c.Extent);
                s.P.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = id, SoundId = "engine:" + c.Key, IsSynth = true, EngineKey = c.Key,
                    EngineSpeed = v, EngineRunning = true, Type = EmitterType.EntityAttached, Mode = PlaybackMode.LoopOne,
                    Position = p, ApparentPosition = p, Velocity = vel, Direction = new Vector3(0f, 0f, dir),
                    Volume = gain, MinDistance = reference, ExtentMetres = c.Extent, Range = Loudness.AudibleRange(profile.SourceLevelDb),
                    Pitch = 1f, TargetRegionId = -1, LevelDb = profile.SourceLevelDb,
                    EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                });
                float direct = Vector3.Distance(p, ear);
                var front = p + new Vector3(0f, 0f, dir * c.Extent * 0.6f);
                if (direct < 25f || fronted[k])
                {
                    fronted[k] = direct < 30f;
                    if (fronted[k])
                        s.P.PlaySpatialSound(new SpatialEmitter
                        {
                            EntityId = id - 50, SoundId = "front", IsSynth = true, IntakeOfEntity = id,
                            Type = EmitterType.EntityAttached, Mode = PlaybackMode.LoopOne,
                            Position = front, ApparentPosition = front, Velocity = vel, Volume = gain, MinDistance = reference,
                            Range = Loudness.AudibleRange(profile.SourceLevelDb), Pitch = 1f, TargetRegionId = -1,
                            LevelDb = profile.SourceLevelDb, EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                        });
                    else s.P.StopSound(id - 50);
                }
                for (int w = 0; w < facades.Length; w++)
                {
                    var mirror = new Vector3(2f * facades[w] - p.X, p.Y, p.Z);
                    float path = Vector3.Distance(mirror, ear);
                    s.P.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = id - 100 - w, SoundId = "echo", IsSynth = true, EchoOfEntity = id, IsReflection = true,
                        EchoDelaySeconds = MathF.Max(0f, path - direct) / C, EchoGain = 0.6f, EchoScattering = 0.3f,
                        Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
                        Position = mirror, ApparentPosition = mirror, Volume = gain, MinDistance = reference,
                        Range = Loudness.AudibleRange(profile.SourceLevelDb), Pitch = 1f, TargetRegionId = -1,
                        LevelDb = profile.SourceLevelDb - 6f, EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                    });
                }
            }
        });
    }

    /// <summary>A ground flash three kilometres away to the north-east, rendered and played as the
    /// client plays it (WorldAudioPlayer.QueueThunder): each part a float buffer at the part's own
    /// rate, placed out in its direction at its peak level.</summary>
    private static void ThunderScene(Session s, float sec, bool bandLimit)
    {
        var ear = new Vector3(0f, 1.7f, 0f);
        int seed = 4;
        var rng = new Random(seed);
        float b = 45f * MathF.PI / 180f;
        var to = new Vector3(3000f * MathF.Sin(b), 0f, 3000f * MathF.Cos(b));
        var from = to + new Vector3(LightningPhysics.Gaussian(rng) * 800f, 5000f, LightningPhysics.Gaussian(rng) * 800f);
        var strike = new LightningStrike(seed, FlashKind.CloudToGround, from, to, LightningPhysics.GroundFlashEnergyMedian, 2);
        var parts = Thunder.Render(strike, ear, new Thunder.Air(15f, 0.8f, 1013.25f, Vector3.Zero, 0f),
                                   new Thunder.Options { EarAboveGround = 1.7f });
        float first = parts.Count > 0 ? parts.Min(p => p.StartSeconds) : 0f;
        Console.WriteLine($"  thunder: {parts.Count} parts at {(parts.Count > 0 ? parts[0].SampleRate : 0)} Hz, first {first:F1} s after the flash, loudest {(parts.Count > 0 ? parts.Max(p => p.PeakDb) : 0):F0} dB peak at the ear");
        var pending = new List<(double At, SpatialEmitter E)>();
        for (int k = 0; k < parts.Count; k++)
        {
            var part = parts[k];
            if (part.PeakPa <= 0f || part.Pressure.Length == 0) continue;
            var pcm = new float[part.Pressure.Length];
            float g = 1f / part.PeakPa;
            for (int i = 0; i < pcm.Length; i++) pcm[i] = part.Pressure[i] * g;
            string id = $"lab:quality:thunder:{k}";
            // As the client does now (WorldAudioPlayer.HearThunder): at the mixer rate, band-limited.
            int rate = part.SampleRate;
            if (bandLimit && rate != MixerQuality.MixerRate) { pcm = MixerQuality.Resample(pcm, rate, MixerQuality.MixerRate); rate = MixerQuality.MixerRate; }
            if (!s.P.RegisterSynthesisedSoundFloat(id, pcm, rate)) continue;
            float level = WorldAudioPlayer.SkyLevelDb(part.PeakDb);
            var (gain, reference) = Loudness.Place(level);
            var at = ear + part.Direction * WorldAudioPlayer.SkyProxyMetres;
            pending.Add((0.5 + part.StartSeconds - first, new SpatialEmitter
            {
                EntityId = -800 - k, SoundId = id, Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
                Position = at, ApparentPosition = at, Volume = gain, MinDistance = reference,
                Range = Loudness.AudibleRange(level), Pitch = 1f, IsEvent = true, LevelDb = level, TargetRegionId = -1,
                EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
            }));
        }
        s.Pump(sec, t =>
        {
            for (int i = pending.Count - 1; i >= 0; i--)
                if (t >= pending[i].At) { s.P.PlaySpatialSound(pending[i].E); pending.RemoveAt(i); }
        });
    }

    /// <summary>
    /// The game's synthesised one-shots — door models, a car door, a clap, a struck ring — one every
    /// 1.6 s, three metres ahead and to the left. <paramref name="legacy"/> registers each as the client
    /// did before 10-06: sixteen bits at 48 kHz, left to FMOD's resampler. Otherwise as it does now:
    /// float, brought to the mixer's rate band-limited (MixerQuality.Resample).
    /// </summary>
    private static void OneShots(Session s, float sec, bool legacy)
    {
        Environment.SetEnvironmentVariable("OPENFPS_RENDER_CACHE", Path.Combine(Path.GetTempPath(), "openfps-quality-rendercache"));
        int sr = TransientSynth.SampleRate;
        var full = new System.Collections.Concurrent.ConcurrentDictionary<string, float>();
        var keys = WorldAudioPlayer.PrewarmKeys();
        var renders = new List<(string Name, float[] Pcm)>
        {
            ("knob door opening", WorldAudioPlayer.RenderDoorKey(keys[0], full)),
            ("knob door shutting", WorldAudioPlayer.RenderDoorKey(keys[2], full)),
            ("car door shutting", CarDoor.Render(true, sr, 3)),
            ("clap", Applause.RenderClap(sr, 2)),
            ("struck ring", TransientSynth.Render(new TransientSound { Character = SoundCharacter.Ring, Hz = 1400f, DecaySeconds = 1.2f, Noisiness = 0.2f }, 5)),
            ("knock", TransientSynth.Render(new TransientSound { Character = SoundCharacter.Knock, Hz = 220f, DecaySeconds = 0.4f, Noisiness = 0.6f }, 6)),
        };
        int k = 0;
        var at = new Vector3(-1.5f, 0f, 2.6f);
        var due = new List<(double At, SpatialEmitter E)>();
        foreach (var (name, pcm) in renders)
        {
            string id = $"lab:quality:oneshot:{k}";
            bool ok = legacy ? s.P.RegisterSynthesisedSound(id, TransientSynth.ToPcm16(pcm), sr)
                             : s.P.RegisterSynthesisedSoundFloat(id, MixerQuality.Resample(pcm, sr, MixerQuality.MixerRate), MixerQuality.MixerRate);
            if (!ok) continue;
            var (gain, reference) = Loudness.Place(80f);
            due.Add((0.4 + 1.6 * k, Plain(-1000 - k, id, at, gain, 1f, PlaybackMode.Single) with { MinDistance = reference }));
            Console.WriteLine($"  {k}: {name} at {0.4 + 1.6 * k:F1} s");
            k++;
        }
        // Timed from now: the first door render can take seconds, and the clock started with the mixer.
        double t0 = s.Clock.Elapsed.TotalSeconds;
        s.Pump(sec, t =>
        {
            for (int i = due.Count - 1; i >= 0; i--)
                if (t - t0 >= due[i].At) { s.P.PlaySpatialSound(due[i].E); due.RemoveAt(i); }
        });
    }

    // ── A car's reflection ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A car's echo off a wall (EngineEchoState, the reflection path) with the car itself all but
    /// muted, so the capture is the echo: once with the car going past at 60 km/h (its channel pitched
    /// by Doppler), once with the same engine note standing still. A reflection reads back from the
    /// car's PLAY position, which moves by however much the car's channel consumed; a step in that
    /// read at every mixer block would show as a residual locked to the 1024-sample block.
    /// </summary>
    private static int Echo(string[] args, string dir, string tag)
    {
        const string Key = "i4_midsize";
        var profile = MachineRegistry.VehicleFor(Key);
        var (gain, reference) = Loudness.Place(profile.SourceLevelDb, 3.5f);
        var modes = (Str(args, "modes") ?? "moving,still,borrowed,approach,front,frontstill").Split(',');
        foreach (string mode in modes)
        {
            string name = $"echo-{tag}-{mode}";
            using var s = new Session(dir, name, pre: false);
            const float V = 60f / 3.6f;
            s.Pump(7.0, t =>
            {
                bool moving = mode != "still" && mode != "frontstill";
                var p = moving && mode != "approach" ? new Vector3(4f, 0.4f, -50f + V * (float)t) : new Vector3(4f, 0.4f, 10f);
                s.P.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = -900, SoundId = "engine:" + Key, IsSynth = true, EngineKey = Key,
                    EngineSpeed = V, EngineRunning = true, Type = EmitterType.EntityAttached, Mode = PlaybackMode.LoopOne,
                    Position = p, ApparentPosition = p, Velocity = mode == "approach" ? new Vector3(0f, 0f, -V) : moving ? new Vector3(0f, 0f, V) : Vector3.Zero,
                    Direction = Vector3.UnitZ, Volume = gain * 1e-4f, MinDistance = reference, ExtentMetres = 3.5f,
                    Range = 500f, Pitch = 1f, TargetRegionId = -1, LevelDb = profile.SourceLevelDb,
                    EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                });
                if (mode.StartsWith("front"))
                {
                    // The front of the same car as a voice of its own (EngineTapState), the back muted.
                    var f = p + new Vector3(0f, 0f, 2f);
                    s.P.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = -902, SoundId = "front", IsSynth = true, IntakeOfEntity = -900,
                        Type = EmitterType.EntityAttached, Mode = PlaybackMode.LoopOne,
                        Position = f, ApparentPosition = f, Velocity = moving ? new Vector3(0f, 0f, V) : Vector3.Zero,
                        Volume = gain, MinDistance = reference, Range = 500f, Pitch = 1f, TargetRegionId = -1,
                        LevelDb = profile.SourceLevelDb, EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                    });
                    return;
                }
                var mirror = new Vector3(-6f, 0.4f, 10f);
                s.P.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = -901, SoundId = "echo", IsSynth = true, EchoOfEntity = -900, IsReflection = mode != "borrowed",
                    EchoDelaySeconds = 0.03f, EchoGain = 1f, EchoScattering = 0f,
                    Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
                    Position = mirror, ApparentPosition = mirror, Volume = gain, MinDistance = reference,
                    Range = 500f, Pitch = 1f, TargetRegionId = -1, LevelDb = profile.SourceLevelDb,
                    EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                });
            });
            s.Dispose();
            s.DropSoundCardCopy();
            Console.WriteLine($"  {name}: written");
        }
        return 0;
    }

    // ── The voices' soft ceiling ──────────────────────────────────────────────────────────────

    /// <summary>
    /// How often a live voice's samples are bent by SoftCeiling (knee 0.8, ceiling +2 dB), and how much
    /// of its energy the bend is: each bent sample's distance from the straight line it left, recovered
    /// by inverting the bend. Engines at a steady cruise, read back from the voice's ring before the
    /// ground (what the ceiling sees); nature voices and a condenser from their own ring.
    /// </summary>
    private static int Ceiling(string[] args)
    {
        float sec = float.TryParse(Str(args, "sec"), NumberStyles.Float, CultureInfo.InvariantCulture, out float sv) ? sv : 20f;
        static (double Share, double DistDb, float Peak) Measure(Func<int, float> sample, int n)
        {
            float knee = SoftCeiling.Knee, room = SoftCeiling.Ceiling - SoftCeiling.Knee;
            long bent = 0; double e = 0, d = 0; float peak = 0;
            for (int i = 0; i < n; i++)
            {
                float y = sample(i), a = MathF.Abs(y);
                e += y * (double)y; peak = MathF.Max(peak, a);
                if (a <= knee) continue;
                bent++;
                float t = MathF.Min(0.999999f, (a - knee) / room);
                float x = knee + room * 0.5f * MathF.Log((1 + t) / (1 - t));   // atanh
                d += (x - a) * (double)(x - a);
            }
            return (bent / (double)n, d > 0 ? 10 * Math.Log10(d / e) : double.NegativeInfinity, peak);
        }
        int sr = 44100;
        Console.WriteLine($"  {"voice",-28} {"bent",8} {"bend energy",12} {"peak",6}");
        foreach (var (key, kmh) in new[] { ("i4_compact", 45f), ("i4_midsize", 50f), ("transit_bus", 35f), ("i6_street", 40f),
                                           ("v8_muscle", 60f), ("police_interceptor", 80f), ("sportbike", 60f), ("single", 50f), ("diesel_truck", 40f) })
        {
            var v = new EngineVoiceState(MachineRegistry.VehicleFor(key), sr, 3) { TargetSpeed = kmh / 3.6f };
            v.PlaceAtSpeed(kmh / 3.6f);
            var block = new float[1024];
            int n = (int)(sec * sr) / 1024 * 1024;
            for (int i = 0; i < 2 * sr / 1024; i++) v.Render(block);
            long start = v.Played;
            var all = new float[n];
            for (int i = 0; i < n; i += 1024) { v.Render(block); for (int k = 0; k < 1024; k++) all[i + k] = v.ReadAt(start + i + k); }
            var m = Measure(i => all[i], n);
            Console.WriteLine($"  {key + " " + kmh + " km/h",-28} {m.Share * 100,7:F3}% {m.DistDb,9:F1} dB {20 * MathF.Log10(m.Peak),6:F1}");
        }
        foreach (var key in new[] { "water:park_fountain", "foliage:park_tree", "fire:fire_pit", "machine:ac_window" })
        {
            string kind = key[..key.IndexOf(':')], preset = key[(key.IndexOf(':') + 1)..];
            PhysicalVoiceState v = kind switch
            {
                "water" => new WaterVoiceState(WaterFeatureSpec.ByName(preset), sr, 11, Vector3.Zero),
                "foliage" => new FoliageVoiceState(FoliageSpec.ByName(preset), sr, 11, Vector3.Zero),
                "fire" => new FireVoiceState(FireSpec.ByName(preset), sr, 11, Vector3.Zero),
                _ => new MachineVoiceState(SmallMachineSpec.ByName(preset), sr, 5, 11),
            };
            var block = new float[1024];
            for (int i = 0; i < 2 * sr / 1024; i++) v.Render(block);
            int n = (int)(sec * sr) / 1024 * 1024;
            var all = new float[n];
            for (int i = 0; i < n; i += 1024) { v.Render(block); Array.Copy(block, 0, all, i, 1024); }
            var m = Measure(i => all[i], n);
            Console.WriteLine($"  {key,-28} {m.Share * 100,7:F3}% {m.DistDb,9:F1} dB {20 * MathF.Log10(m.Peak),6:F1}");
        }
        return 0;
    }

    // ── Sixteen bits ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one-shot path's sixteen bits (WorldAudioPlayer: TransientSynth.ToPcm16 of a buffer normalised
    /// to its own peak) against the float it came from: the error's level by how far down the sound has
    /// decayed. Truncation, no dither, so below a few LSB the error is the signal's own shape, not noise.
    /// </summary>
    private static int Quant()
    {
        var sounds = new (string Name, TransientSound S)[]
        {
            ("knock 1.2 s", new TransientSound { Character = SoundCharacter.Knock, Hz = 180f, DecaySeconds = 1.2f, Noisiness = 0.4f }),
            ("ring 3 s", new TransientSound { Character = SoundCharacter.Ring, Hz = 900f, DecaySeconds = 3f, Noisiness = 0.1f }),
            ("scrape 1 s", new TransientSound { Character = SoundCharacter.Scrape, Hz = 400f, DecaySeconds = 1f, Noisiness = 0.8f }),
        };
        foreach (var (name, snd) in sounds)
        {
            var x = TransientSynth.Render(snd, 3);
            var b = TransientSynth.ToPcm16(x);
            int win = TransientSynth.SampleRate / 20;
            Console.WriteLine($"  {name}: window (50 ms) by signal level re its peak -> error re signal");
            var seen = new HashSet<int>();
            for (int w = 0; w + win <= x.Length; w += win)
            {
                double es = 0, ee = 0;
                for (int i = w; i < w + win; i++)
                {
                    float q = (short)(b[i * 2] | (b[i * 2 + 1] << 8)) / 32767f;
                    es += x[i] * (double)x[i]; ee += (q - x[i]) * (double)(q - x[i]);
                }
                double sdb = 10 * Math.Log10(Math.Max(1e-30, es / win));
                int band = (int)Math.Floor(sdb / 10);
                if (sdb < -110 || !seen.Add(band)) continue;
                Console.WriteLine($"    signal {sdb,7:F1} dBFS   error {10 * Math.Log10(Math.Max(1e-30, ee / win)),7:F1} dBFS   ({10 * Math.Log10(Math.Max(1e-30, ee / Math.Max(1e-30, es))),6:F1} dB re signal)");
            }
        }
        return 0;
    }

    // ── Small things ───────────────────────────────────────────────────────────────────────────

    private static void WriteFloatWav(string path, float[] x, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 4;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)3); w.Write((short)1);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        foreach (float v in x) w.Write(v);
    }

    private static string DescribeWav(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var h = new byte[44];
            if (f.Read(h, 0, 44) < 44) return "(too short)";
            int fmt = BitConverter.ToInt16(h, 20), ch = BitConverter.ToInt16(h, 22), rate = BitConverter.ToInt32(h, 24), bits = BitConverter.ToInt16(h, 34);
            return $"{(fmt == 3 ? "float" : fmt == 1 ? "PCM" : "format " + fmt)} {bits}-bit, {ch} ch, {rate} Hz";
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    private static string? Str(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
