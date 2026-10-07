using System;
using System.IO;
using System.Linq;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --fire hrtf out=DIR: what the game's head (Steam Audio's default HRTF, bilinear) makes of independent
/// noises from given bearings, written as stereo WAVs for tools that measure the two ears' coherence. Each
/// set is a fire's places as a listener sees them (or a ring round the head, or one source); every source
/// its own white noise, at the same level. Nothing else of the game is in the path: no distance, no ground,
/// no reverb. This is the floor and the slope of IACC that the head itself gives.
/// </summary>
public static class FireHrtfProbe
{
    private const int Fs = 48000, Sub = 256;

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        var au = new Phonon.IPLAudioSettings { samplingRate = Fs, frameSize = Sub };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);

        void Render(string name, float[] bearings, float[]? gains = null, float[]? elevations = null)
        {
            int n = Fs * 12, N = bearings.Length;
            var bs = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
            var fx = new IntPtr[N];
            for (int b = 0; b < N; b++) Phonon.iplBinauralEffectCreate(ctx, ref au, ref bs, out fx[b]);
            var mono = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(ctx, 1, Sub, ref mono);
            var st = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(ctx, 2, Sub, ref st);
            var m = new float[Sub]; var s2 = new float[Sub * 2];
            var outp = new float[n * 2];
            var rngs = Enumerable.Range(0, N).Select(i => new Random(77 + i)).ToArray();
            for (int at = 0; at + Sub <= n; at += Sub)
                for (int b = 0; b < N; b++)
                {
                    double th = bearings[b] * Math.PI / 180, el = (elevations?[b] ?? 0f) * Math.PI / 180;
                    // Steam Audio's listener frame: x right, y up, -z ahead.
                    var d = new Phonon.IPLVector3 { x = (float)(Math.Sin(th) * Math.Cos(el)), y = (float)Math.Sin(el), z = (float)(-Math.Cos(th) * Math.Cos(el)) };
                    float g = gains?[b] ?? 1f / MathF.Sqrt(N);
                    for (int k = 0; k < Sub; k++) m[k] = (float)(rngs[b].NextDouble() * 2 - 1) * 0.3f * g;
                    Phonon.iplAudioBufferDeinterleave(ctx, m, ref mono);
                    var ep = new Phonon.IPLBinauralEffectParams { direction = d, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf };
                    Phonon.iplBinauralEffectApply(fx[b], ref ep, ref mono, ref st);
                    Phonon.iplAudioBufferInterleave(ctx, ref st, s2);
                    for (int k = 0; k < Sub * 2; k++) outp[at * 2 + k] += s2[k];
                }
            RunningWaterSpike.WriteFloatWav(Path.Combine(dir, name + ".wav"), outp, 2);
            Console.WriteLine($"wrote {name}: {N} sources at " + string.Join(" ", bearings.Select(v => $"{v:F1}")));
        }

        Render("one_ahead", new[] { 0f });
        Render("one_right_30", new[] { 30f });
        Render("two_pm30", new[] { -30f, 30f });
        // A house at 30 m: its seven places' bearings, then spread two and four times as wide.
        float[] house = { 0f, 6.9f, 1.9f, -5.0f, -6.9f, -1.9f, 5.0f };
        foreach (float k in new[] { 1f, 2f, 4f }) Render($"house_30m_x{k:0}", house.Select(b => b * k).ToArray());
        Render("ring_24", Enumerable.Range(0, 24).Select(i => i * 15f).ToArray());

        // Each round-2 render's fire as the game places it: its places where the client puts them (the
        // entity turned by yaw, at the flames' height), the spread the client gives them at that distance,
        // each place's share of the fire's power as the synth renders it (measured, 10 s), 1/r, the listener
        // standing on the near side with its ears at 1.7 m, facing the middle.
        void Scene(string name, string preset, float distance, float yaw = 0f)
        {
            var spec = FireSpec.ByName(preset);
            var layout = FireSynth.Layout(spec);
            var synth = new FireSynth(spec, Fs, 5, layout.Length) { Spread = 1f };
            var pw = new double[layout.Length];
            var buf = new float[layout.Length];
            for (int i = 0; i < Fs * 10; i++)
            {
                if (i % 256 == 0) { synth.Wind = 3f; synth.Control(256f / Fs); }
                synth.NextPlaces(buf);
                if (i > Fs * 2) for (int p = 0; p < buf.Length; p++) pw[p] += buf[p] * (double)buf[p];
            }
            var rot = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, yaw * MathF.PI / 180f);
            var middle = new System.Numerics.Vector3(0f, FireSpike.Height(spec), 0f);
            var ear = new System.Numerics.Vector3(0f, 1.7f, -distance);
            float spread = ExtendedSources.SpreadFor(ExtendedSources.Reach(layout), System.Numerics.Vector3.Distance(ear, middle));
            int n = layout.Length;
            double total = pw.Sum();
            var bear = new float[n]; var elev = new float[n]; var gain = new float[n];
            for (int p = 0; p < n; p++)
            {
                // Merged by the spread: what is not at a place is heard from the middle.
                double share = pw[p] / total;
                double here = p == 0 ? share + (1 - spread) * (1 - share) : spread * share;
                var at = middle + System.Numerics.Vector3.Transform(layout[p], rot) - ear;
                float r = MathF.Max(1f, at.Length());
                bear[p] = MathF.Atan2(at.X, at.Z) * 180f / MathF.PI;
                elev[p] = MathF.Asin(at.Y / r) * 180f / MathF.PI;
                gain[p] = (float)Math.Sqrt(here) / r;
            }
            float norm = 1f / MathF.Sqrt(gain.Sum(v => v * v));
            for (int p = 0; p < n; p++) gain[p] *= norm;
            Console.WriteLine($"  {name}: spread {spread:F2}, widest place {bear.Max(v => MathF.Abs(v)):F1} deg, half-angle of the area "
                            + $"{MathF.Atan2(0.5f * MathF.Max(spec.AreaWidth, spec.AreaDepth), distance) * 180f / MathF.PI:F1} deg");
            Render("expected_" + name, bear, gain, elev);
        }
        Scene("campfire_2m", "campfire", 2f);
        Scene("fire_pit_2m", "fire_pit", 2f);
        Scene("bonfire_5m", "bonfire", 5f);
        Scene("burning_car_10m", "burning_car", 10f, 90f);
        Scene("house_fire_12m", "house_fire", 12f);
        Scene("house_fire_30m", "house_fire", 30f);
        Scene("house_fire_150m", "house_fire", 150f);
        Scene("burning_trees_50m", "burning_trees", 50f);
        Scene("crown_fire_300m", "crown_fire", 300f);
        Scene("crown_fire_1000m", "crown_fire", 1000f);
        Scene("walk_start_143m", "burning_trees", 143f);
        Scene("walk_middle_80m", "burning_trees", 80f);
        Scene("walk_end_17m", "burning_trees", 17f);
        return 0;
    }
}
