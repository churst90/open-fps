using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --nan-mix [map=city] [from=x,y,z] [to=x,y,z] [seconds=10] [door=x,y,z]: Cody's walk of 2026-10-03
/// through the whole mixer. The listener goes from the Marlow Tower corridor, floor 0, into flat
/// 00B; the door is opened on the way (the scene rebuilt without its leaf, as the worker does);
/// claps and footsteps at the listener, steady sources out in the street, every 0.3 s a one-off
/// somewhere near. The traced reverb, the late field and the reverb buses run as in the game. Then
/// it reports every unit the non-finite guard caught ([NONFINITE]) and the master's state, read
/// from the capture.
/// </summary>
public static class NanMixSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = Str(args, "map") ?? "city";
        static Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var from = P(Str(args, "from") ?? "-17.5,1.7,-102.52");
        var to = P(Str(args, "to") ?? "-25.0,1.7,-102.52");
        var door = P(Str(args, "door") ?? "-21.14,1.0,-102.52");
        double seconds = double.TryParse(Str(args, "seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sv) ? sv : 10;
        string outPath = Str(args, "out") ?? "/tmp/openfps-nan-mix.wav";

        var world = PathProbeSpike.LoadAsClient(OpenFPS.AudioLab.LabPaths.Server(), mapId);
        var acoustics = new SpatialAcoustics();
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        // The door leaf: the thin boxes at the doorway. Opened, it is not there.
        var leaf = boxes.Where(b => Vector3.Distance(new Vector3(b.Center.X, door.Y, b.Center.Z), door) < 0.7f
                                    && MathF.Min(b.Size.X, b.Size.Z) < 0.12f && b.Size.Y > 1.5f && b.Size.Y < 2.5f).ToList();
        Console.WriteLine($"  {boxes.Count} boxes; the door leaf: {leaf.Count} box(es) {string.Join(", ", leaf.Select(b => $"{b.Material} at ({b.Center.X:F2}, {b.Center.Y:F2}, {b.Center.Z:F2})"))}");
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        var scene = new SteamAudioScene(ctx); scene.Build(boxes);
        var lscene = new SteamAudioScene(ctx); lscene.Build(SteamAudioScene.WithoutOpenGround(boxes));
        TracedReverbSet.Configure(ctx, scene, lscene);
        TracedReverbSet.SetListener(from, acoustics.GetRegionAt(world, from));

        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", outPath);
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        SteamAudioScene? openScene = null, openListener = null;
        var lines = new List<string>();
        try
        {
            if (world.AcousticMap != null) provider.SetAcousticMap(world.AcousticMap);
            int sr = TransientSynth.SampleRate;
            var clap = Applause.RenderClap(sr, 1);
            provider.RegisterSynthesisedSound("synth:clap:lab", TransientSynth.ToPcm16(clap), sr);
            var rng = new Random(7);
            var hum = new float[sr * 4];
            for (int i = 0; i < hum.Length; i++)
                hum[i] = 0.3f * MathF.Sin(MathF.Tau * 61f * i / sr) + 0.2f * (float)(rng.NextDouble() * 2 - 1);
            provider.RegisterSynthesisedSound("synth:lab:engine", TransientSynth.ToPcm16(hum), sr);

            // Steady sources in the street and in the corridor, as the cars and the beacons are.
            var steady = new[] { new Vector3(-10f, 0.5f, -120f), new Vector3(-30f, 0.5f, -125f), new Vector3(0f, 0.5f, -110f),
                                 new Vector3(-21.1f, 1.6f, -102.4f), new Vector3(-19f, 1.2f, -103f) };
            for (int k = 0; k < steady.Length; k++)
            {
                var at = steady[k];
                var placed = Loudness.Place(k < 3 ? 95f : 60f);
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = -60 - k, SoundId = "synth:lab:engine", Mode = PlaybackMode.LoopOne, Type = EmitterType.WorldLocked,
                    Position = at, ApparentPosition = at, Volume = placed.Gain, MinDistance = placed.ReferenceDistance,
                    Range = 300f, Pitch = 1f, ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                    EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f, Essential = true,
                    TargetRegionId = acoustics.GetRegionAt(world, at), LevelDb = k < 3 ? 95f : 60f, IsEvent = true,
                });
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double nextClap = 1.0;
            int voice = -1000;
            bool opened = false;
            int lastRegion = int.MinValue;
            while (sw.Elapsed.TotalSeconds < seconds + 3)
            {
                double t = sw.Elapsed.TotalSeconds;
                float u = (float)Math.Clamp((t - 1.5) / seconds, 0, 1);
                var ear = Vector3.Lerp(from, to, u);
                int region = acoustics.GetRegionAt(world, ear);
                if (region != lastRegion) { lines.Add($"  {t:F2} s: region {region} at ({ear.X:F2}, {ear.Y:F2}, {ear.Z:F2})"); lastRegion = region; }
                provider.UpdateListener(ear, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new Vector3(-0.75f, 0, 0), region);
                TracedReverbSet.SetListener(ear, region);
                if (!opened && Vector3.Distance(ear, new Vector3(door.X, ear.Y, door.Z)) < 1.6f)
                {
                    // The door opens: both scenes rebuilt without its leaf, handed over in the background.
                    opened = true;
                    var rest = boxes.Where(b => !leaf.Contains(b)).ToList();
                    openScene = new SteamAudioScene(ctx); openScene.Build(rest);
                    openListener = new SteamAudioScene(ctx); openListener.Build(SteamAudioScene.WithoutOpenGround(rest));
                    TracedReverbSet.ConfigureInBackground(ctx, openScene, openListener);
                    lines.Add($"  {t:F2} s: the door opens");
                }
                if (t >= nextClap)
                {
                    nextClap += 0.3;
                    var hands = ear + new Vector3((float)(rng.NextDouble() - 0.5), -0.4f - (float)rng.NextDouble(), (float)(rng.NextDouble() - 0.5));
                    // atear: every other one exactly where the ear is, as a beacon on a door face at face
                    // height is when you walk through the doorway.
                    if (args.Contains("atear") && ((int)(t / 0.3) & 1) == 0) hands = ear;
                    var placed = Loudness.Place(85f);
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = voice--, SoundId = args.Contains("chime") ? "SYNTH/beacon_door_chime" : "synth:clap:lab",
                        Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
                        Position = hands, ApparentPosition = hands, Volume = placed.Gain, Range = 200f, MinDistance = placed.ReferenceDistance,
                        Pitch = 1f, ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f, EqLow = 1f, EqMid = 1f, EqHigh = 1f,
                        ApertureFactor = 1f, IsEvent = true, Essential = true, TargetRegionId = region, LevelDb = 85f,
                        EffectiveDistance = Vector3.Distance(ear, hands),
                    });
                }
                provider.Update();
                Thread.Sleep(10);
            }
        }
        finally
        {
            provider.Dispose(); TracedReverbSet.Dispose();
            scene.Dispose(); lscene.Dispose(); openScene?.Dispose(); openListener?.Dispose();
            Phonon.iplContextRelease(ref ctx);
        }
        foreach (var l in lines) Console.WriteLine(l);
        Console.WriteLine($"  non-finite blocks zeroed: {NonFinite.Blocks}");
        return NonFinite.Blocks == 0 ? 0 : 1;
    }

    private static string? Str(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
