using System.Globalization;
using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --room-change [walk=X,Y,Z:X,Y,Z:...] [speed=1.4] [laps=2] [hold=4] [out=DIR]: the listener walked
/// from room to room on the city map through the whole client path (the map as the client gets it, a
/// ClientAudioSystem over the FmodAudioProvider, its occlusion worker and the traced reverb), and the
/// longest frame of each leg printed: the update and the audio thread's tick, which place every source.
/// Points are as /tp takes them: x east, y north, z the floor's height. The default is Marlow Tower's
/// front door, from the street (-7 -103 0.5) into the stairwell (-12 -103 0.5) and out again.
/// </summary>
public static class RoomChangeSpike
{
    public static int Run(string[] args)
    {
        string? Arg(string key) => args.FirstOrDefault(a => a.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
        string outDir = Arg("out=") ?? Path.Combine(Path.GetTempPath(), "openfps-room-change");
        Directory.CreateDirectory(outDir);
        float speed = float.TryParse(Arg("speed="), NumberStyles.Float, CultureInfo.InvariantCulture, out float sp) ? sp : 1.4f;
        int laps = int.TryParse(Arg("laps="), out int l) ? l : 2;
        double hold = double.TryParse(Arg("hold="), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 4.0;
        var points = (Arg("walk=") ?? "-7,-103,0.5:-12,-103,0.5").Split(':').Select(p =>
        {
            var c = p.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(c[0], c[2], c[1]);       // x east, y north, z height -> the engine's x, up, z north
        }).ToList();
        AcousticRegistry.Initialize();

        var snapshot = OpenFPS.Client.Core.AudioEngine.SteamAudio.PathProbeSpike.LoadAsClient(AppContext.BaseDirectory, "city");
        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        foreach (var e in snapshot.Entities.Values) world.RegisterDefinition(e.Definition, deferAcoustics: true);
        world.SetAcousticMap(snapshot.AcousticMap!);

        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var beacons = BeaconPreferences.InMemory();
        foreach (var category in Beacons.Categories) beacons.Set(category, false);
        world.Clock = facade.Clock; world.Hearing = facade.Hearing;
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds, beacons: beacons);

        double worstFrameMs = 0;
        Action<double>? perFrame = null;
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                worstFrameMs = Math.Max(worstFrameMs, System.Diagnostics.Stopwatch.GetElapsedTime(f0).TotalMilliseconds);
                Thread.Sleep(4);
            }
        }
        string Where() => $"region {provider.ListenerRegionForLab}, {provider.ReverbBusCountForLab} bus(es), "
                        + $"{provider.TracedStageCountForLab} traced stage(s), traced reverb {(provider.TracedActive ? "on" : "off")}, "
                        + $"{provider.Traced.Stats().Rooms} room trace(s)";
        void Stand(Vector3 feet, float yaw)
        {
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }

        try
        {
            Stand(points[0], 0f);
            // The map's scene built and traced before the walk: the first leg is a room change, not a map load.
            double waited = 0;
            while (!provider.TracedActive && waited < 60) { Pump(0.5); waited += 0.5; }
            Pump(hold);
            Console.WriteLine($"  start: {Where()} (worst frame settling {worstFrameMs:F0} ms)");
            for (int lap = 0; lap < laps; lap++)
                for (int leg = 0; leg < points.Count; leg++)
                {
                    var from = points[leg];
                    var to = points[(leg + 1) % points.Count];
                    float length = Vector3.Distance(from, to);
                    double seconds = Math.Max(0.5, length / speed);
                    var dir = to - from;
                    float yaw = MathF.Atan2(-dir.X, -dir.Z);
                    double t0 = clock.Elapsed.TotalSeconds;
                    perFrame = t =>
                    {
                        float a = (float)Math.Clamp((t - t0) / seconds, 0, 1);
                        Stand(Vector3.Lerp(from, to, a), yaw);
                    };
                    worstFrameMs = 0;
                    Pump(seconds);
                    perFrame = null;
                    Stand(to, yaw);
                    Pump(hold);
                    Console.WriteLine($"  lap {lap + 1} leg {leg + 1}: {length:F1} m in {seconds:F1} s, then {hold:F1} s standing: worst frame {worstFrameMs:F0} ms; {Where()}");
                }
        }
        finally
        {
            facade.Dispose();
        }
        return 0;
    }
}
