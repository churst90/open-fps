using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --map-travel [from=city] [to=speedway] [voices=40]: Cody's trip of 2026-10-05 through the whole
/// mixer. The first map's acoustics are loaded (every room gets its reverb bus, and each bus borrows
/// a binaural stage from the pool), then the second map's replace them, as /join does. Then a row of
/// steady sources plays three metres to the listener's right, and each one's two ears are read off
/// its binaural stage. A source on the right is louder in the right ear; one that is not is mono.
/// </summary>
public static class MapTravelSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string from = Str(args, "from") ?? "city", to = Str(args, "to") ?? "speedway";
        int voices = int.TryParse(Str(args, "voices"), out int nv) ? nv : 40;

        var first = PathProbeSpike.LoadAsClient(OpenFPS.AudioLab.LabPaths.Server(), from);
        var second = PathProbeSpike.LoadAsClient(OpenFPS.AudioLab.LabPaths.Server(), to);
        Console.WriteLine($"  {from}: {first.AcousticMap?.Regions.Count ?? 0} region(s); {to}: {second.AcousticMap?.Regions.Count ?? 0}");

        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", "/tmp/openfps-map-travel.wav");
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        int mono = 0, placed = 0, silent = 0;
        try
        {
            var ear = new Vector3(0f, 1.7f, 0f);
            // Facing +Z, so +X is the right ear's side (the game's frame).
            var facing = Quaternion.Identity;
            int listenerRegion = -1;
            void Run(double seconds)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds) { provider.UpdateListener(ear, facing, Vector3.Zero, listenerRegion); provider.Update(); Thread.Sleep(10); }
            }

            int sr = TransientSynth.SampleRate;
            var rng = new Random(7);
            var hiss = new float[sr * 20];
            for (int i = 0; i < hiss.Length; i++) hiss[i] = 0.3f * (float)(rng.NextDouble() * 2 - 1);
            provider.RegisterSynthesisedSound("synth:lab:hiss", TransientSynth.ToPcm16(hiss), sr);

            // The first map, with something sounding in a room of each of many regions: a room gets its
            // reverb bus (and the bus its binaural stage) when a sound is in it, as the city's cars,
            // walkers and doors put one in hundreds of places.
            if (first.AcousticMap != null) provider.SetAcousticMap(first.AcousticMap);
            var rooms = first.AcousticMap?.RegionPositions.Take(50).ToList() ?? new();
            // Standing in the first of them: the room you are in passes its stereo through (blend 0),
            // and so does every room whose field has no direction to come from.
            if (rooms.Count > 0) { listenerRegion = rooms[0].Key; ear = rooms[0].Value; }
            for (int k = 0; k < rooms.Count; k++)
                provider.PlaySpatialSound(Hiss(-2000 - k, rooms[k].Value, rooms[k].Key));
            Run(1.0);
            Console.WriteLine($"  {from}: {rooms.Count} sources in as many rooms, {provider.ReverbBusCountForLab} reverb bus(es), their stages' blends: {provider.ReverbBlendsForLab}");
            // Travel: the old map's sounds go, then its acoustics.
            for (int k = 0; k < rooms.Count; k++) provider.StopSound(-2000 - k);
            Run(0.3);
            if (second.AcousticMap != null) provider.SetAcousticMap(second.AcousticMap);
            listenerRegion = -1; ear = new Vector3(0f, 1.7f, 0f);
            Run(0.5);
            for (int k = 0; k < voices; k++)
            {
                var at = ear + new Vector3(3f, 0f, 0.05f * k);
                provider.PlaySpatialSound(Hiss(-500 - k, at, -1));
            }
            Run(1.5);

            for (int k = 0; k < voices; k++)
            {
                if (!provider.TryGetBinauralLevels(-500 - k, out float l, out float r, out float blend)) { silent++; continue; }
                if (l + r < 1e-5f) { silent++; continue; }
                float db = 20f * MathF.Log10(Math.Max(r, 1e-9f) / Math.Max(l, 1e-9f));
                bool isMono = MathF.Abs(db) < 0.5f;
                if (isMono) mono++; else placed++;
                if (isMono || k < 3)
                    Console.WriteLine($"  voice {k,2}: L {l:F4}  R {r:F4}  right ear {db:+0.0;-0.0} dB  blend {blend:F2}{(isMono ? "  MONO" : "")}");
            }
        }
        finally { provider.Dispose(); }
        Console.WriteLine($"  after {from} -> {to}: {placed} placed, {mono} mono, {silent} silent or without a binaural stage, of {voices}");
        return mono == 0 && placed > 0 ? 0 : 1;
    }

    private static SpatialEmitter Hiss(int id, Vector3 at, int region)
    {
        var p = Loudness.Place(70f);
        return new SpatialEmitter
        {
            EntityId = id, SoundId = "synth:lab:hiss", Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
            Position = at, ApparentPosition = at, Volume = p.Gain, MinDistance = p.ReferenceDistance,
            Range = 100f, Pitch = 1f, ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f, Essential = true,
            TargetRegionId = region, LevelDb = 70f, IsEvent = true,
        };
    }

    private static string? Str(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
