using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Does the decay written to the reverb unit come out of the mixer?
///
///   --tailcheck [decay=6000] [enclosure=0.95] [mfp=4] [surface=1735] [out=path]
///
/// Written because every number upstream of the ear was right and the ear still said no. The survey
/// measures each room differently (garage 5107 ms, stairwell 2061, corridor 427), the runtime applies
/// exactly those (`Room: ... reverb 6119 ms`, `reverb 1999 ms`), the send reaches the right bus at the
/// right distance (`wettest voice sent 105 % (at 1.6 m)`) — and the report is *"the tail on the
/// reverb is the same no matter where I am in the stairs, corridor or parking garage"*.
///
/// When every parameter reads correct and the result is wrong, the thing that has not been measured is
/// the OUTPUT. So this forces one decay onto the unit, holds it long enough for the slew to settle,
/// drops a single footstep, and then measures the decay of the captured mix — the actual sound, not
/// the parameter that was supposed to make it. Run it at several decays: if the measured tails do not
/// move with the configured ones, nothing downstream of the parameter is listening to it.
/// </summary>
public static class TailCheckSpike
{
    private const int RoomId = 310;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float decay     = Num(args, "decay", 6000f);
        float enclosure = Num(args, "enclosure", 0.95f);
        float mfp       = Num(args, "mfp", 4.0f);
        float surface   = Num(args, "surface", 1735f);
        string outPath  = Str(args, "out") ?? $"/tmp/openfps-tail-{decay:F0}.wav";
        // FMOD's EARLYLATEMIX is the blend of LATE REVERB TO EARLY REFLECTIONS: 0 is all early, 100 is
        // all late. Exposed here so the meaning can be settled by measurement rather than by reading.
        string? el = Str(args, "earlylate");
        if (el != null) FmodAudioProvider.ReverbEarlyLateOverride = float.Parse(el, CultureInfo.InvariantCulture);

        string soundRoot = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS");
        if (!Directory.Exists(Path.Combine(soundRoot, "FOOTSTEPS")))
        {
            Console.WriteLine($"  FAIL: no FOOTSTEPS under {soundRoot}. Link the client's sets in first.");
            return 1;
        }

        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", outPath);
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }

        var eye = new Vector3(5f, 1.6f, 13f);
        try
        {
            provider.SetAcousticMap(BuildMap());

            // Three seconds of standing in the room with this decay forced on, so the per-bus slew has
            // arrived before anything is measured. Measuring during the ramp would measure the ramp.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool dropped = false, shownEarly = false, shownLate = false, keepAliveStarted = false;
            bool keepalive = Array.Exists(args, a => a == "keepalive=on");
            string early = "(not sampled)", late = "(not sampled)", chain = "(not sampled)";
            double nextSample = 3.0;
            var trace = new System.Collections.Generic.List<(string At, float In, float Out, int Sends)>();
            var placed = Loudness.Place(Loudness.FootstepDb);
            while (sw.Elapsed.TotalSeconds < 12.0)
            {
                double t = sw.Elapsed.TotalSeconds;
                provider.UpdateListener(eye, Quaternion.Identity, Vector3.Zero, RoomId);
                provider.SetSimulatedReverbDecay(decay, enclosure, 1f, 1f);
                provider.SetListenerReverbField(new Vector3(0, 0, 1), 0.1f, mfp, surface);

                // keepalive=on: a looping voice, effectively silent, sending into the same bus from
                // the start — so the reverb unit never runs out of inputs. If the footstep's tail
                // survives only when this is present, what truncates it is the unit losing its last
                // input, not the decay it was given.
                if (keepalive && !keepAliveStarted && t >= 1.0)
                {
                    keepAliveStarted = true;
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = -999, SoundId = "BEACONS/megaphone",
                        Mode = PlaybackMode.LoopOne, Type = EmitterType.WorldLocked,
                        Position = eye, ApparentPosition = eye,
                        Volume = 0.00003f, Range = 30f, MinDistance = 1f, Pitch = 1f,
                        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                        EnableReverb = true, TargetRegionId = RoomId,
                    });
                }

                if (!dropped && t >= 3.0)
                {
                    dropped = true;
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = -100,
                        SoundId = "FOOTSTEPS/Wood/Wood0/1",
                        Mode = PlaybackMode.Single,
                        Type = EmitterType.WorldLocked,
                        Position = eye with { Y = 0.1f },
                        ApparentPosition = eye with { Y = 0.1f },
                        FollowsListener = true,
                        ListenerOffset = new Vector3(0f, 0.1f - 1.6f, 0f),
                        Volume = placed.Gain, Range = 15f, MinDistance = placed.ReferenceDistance, Pitch = 1f,
                        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                        IsEvent = true, Essential = true,
                        EnableReverb = true, TargetRegionId = RoomId,
                    });
                }
                provider.Update();
                // Every stage of the chain, sampled a quarter of a second and two seconds after the
                // step. Two seconds into a six-second decay the unit should still be well up.
                // Called EVERY frame from well before the step, because FMOD's metering reports the
                // last mixed block: enabling it and reading it in the same call reads zero whatever the
                // signal is, which is a way to prove any stage silent.
                if (t >= 2.0) chain = provider.DescribeReverbChain(RoomId);
                if (dropped && !shownEarly && t >= 3.25) { shownEarly = true; early = chain; }
                if (dropped && !shownLate  && t >= 5.0)  { shownLate  = true; late  = chain; }
                // The unit's own level, every tenth of a second after the footfall: whether a tail is
                // generated at all, how loud, and whether it survives the voice that fed it.
                if (dropped && t >= nextSample && trace.Count < 30)
                {
                    nextSample += 0.1;
                    provider.TryMeterReverbChain(RoomId, out float uIn, out float uOut, out _, out _, out int sn);
                    trace.Add(($"{t - 3.0:F1}", uIn, uOut, sn));
                }
                Thread.Sleep(16);
            }
            Console.WriteLine($"  configured {decay,6:F0} ms | unit reports {provider.SimulatedReverbDecayMs,6:F0} ms "
                            + $"at {provider.OutdoorReverbWetDb:F1} dB wet");
            Console.WriteLine($"    +0.25 s  {early}");
            Console.WriteLine($"    +2.00 s  {late}");
            Console.WriteLine("    unit in/out, dB, from the footfall:");
            foreach (var row in trace)
                Console.WriteLine($"      +{row.At,4} s   in {row.In,7:F1}   out {row.Out,7:F1}   sends {row.Sends}");
        }
        finally { provider.Dispose(); }

        Measure(outPath, decay);
        return 0;
    }

    /// <summary>The decay of what actually came out, from the captured mix.</summary>
    private static void Measure(string path, float configured)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  FAIL: no {path}"); return; }
        var (samples, sr) = ReadWavMono(path);
        if (samples.Length == 0) { Console.WriteLine("  FAIL: empty capture"); return; }

        // RMS in 20 ms windows, in dB.
        int win = sr / 50;
        int n = samples.Length / win;
        var db = new float[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < win; j++) { float v = samples[i * win + j]; sum += v * v; }
            db[i] = 10f * MathF.Log10((float)Math.Max(1e-12, sum / win));
        }

        int peak = 0;
        for (int i = 0; i < n; i++) if (db[i] > db[peak]) peak = i;

        // From 5 dB below the peak to 25 dB below it — the standard T20 window, which stays clear of
        // both the direct sound and the noise floor — and the slope of that extrapolated to 60 dB.
        float top = db[peak] - 5f, bottom = db[peak] - 25f;
        int iTop = -1, iBottom = -1;
        for (int i = peak; i < n; i++)
        {
            if (iTop < 0 && db[i] <= top) iTop = i;
            if (iTop >= 0 && db[i] <= bottom) { iBottom = i; break; }
        }
        float winSec = win / (float)sr;
        if (iTop < 0 || iBottom < 0 || iBottom <= iTop)
        {
            Console.WriteLine($"  measured: no decay found in the capture (peak {db[peak]:F1} dB, "
                            + $"floor {Floor(db):F1} dB) — the tail is not in the output at all.");
            return;
        }
        float t20 = (iBottom - iTop) * winSec;
        float rt60 = t20 * 3f;
        Console.WriteLine($"  MEASURED IN THE MIX: T20 {t20 * 1000f,6:F0} ms -> RT60 {rt60 * 1000f,6:F0} ms "
                        + $"(configured {configured:F0} ms, ratio {rt60 * 1000f / configured:F2})");
        // ...and, because a T20 taken from the peak can be measuring the SAMPLE rather than the room,
        // what is left at fixed times after the footfall. A six-second tail has to be somewhere at two
        // seconds; if it is at the noise floor, the unit is not reaching the mix at all.
        Console.Write("  after the step: ");
        foreach (float at in new[] { 0.25f, 0.5f, 1f, 2f, 4f })
        {
            int i = peak + (int)(at / winSec);
            string lvl = i < n ? $"{db[i] - db[peak],6:F1}" : "   -  ";
            Console.Write($"+{at:F2}s {lvl} dB   ");
        }
        Console.WriteLine($"| floor {Floor(db) - db[peak]:F1} dB");
    }

    private static float Floor(float[] db)
    {
        var copy = (float[])db.Clone(); Array.Sort(copy);
        return copy[copy.Length / 20];
    }

    private static (float[] Samples, int Rate) ReadWavMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12, channels = 1, bits = 16, rate = 48000, dataAt = -1, dataLen = 0;
        while (pos + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int len = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, pos + 10);
                rate = BitConverter.ToInt32(bytes, pos + 12);
                bits = BitConverter.ToInt16(bytes, pos + 22);
            }
            else if (id == "data") { dataAt = pos + 8; dataLen = Math.Min(len, bytes.Length - dataAt); break; }
            pos += 8 + len + (len & 1);
        }
        if (dataAt < 0 || bits != 16) return (Array.Empty<float>(), rate);
        int frames = dataLen / (2 * channels);
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(bytes, dataAt + (i * channels + c) * 2) / 32768f;
            mono[i] = sum / channels;
        }
        return (mono, rate);
    }

    private static AcousticMap BuildMap()
    {
        var map = new AcousticMap(new Vector3(100, 40, 100), new Vector3(-50, 0, -50))
        {
            GlobalEnvironmentId = AcousticConstants.GlobalRegionId
        };
        int concrete = AcousticRegistry.GetProperties("Concrete").ResonanceIndex;
        map.Regions[RoomId] = new RegionComponent
        {
            FriendlyName = "Tail Room",
            IsIndoor = true,
            RoomSize = new Vector3(21, 2.5f, 28),
            ReverbTimeScale = 1.0f,
            Materials = new[] { concrete, concrete, concrete, concrete, concrete, concrete }
        };
        map.RegionPositions[RoomId] = new Vector3(5f, 1.25f, 13f);
        map.RegionRotations[RoomId] = Quaternion.Identity;
        return map;
    }

    private static string? Str(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }

    private static float Num(string[] args, string key, float fallback)
        => float.TryParse(Str(args, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
