using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --floor-render [out=DIR] [root=DIR/] [debug]: somebody shouting, talking and walking in the flat above, heard in the flat
/// below, and the other way round, in Selby House on the city map, through the whole client path: the map
/// loaded as the client gets it, a ClientAudioSystem over the FmodAudioProvider, its occlusion worker
/// (Steam Audio when the library is there), the default /levels and master. Steps come through
/// OnPlayerFootstep on carpet, lines through WorldAudioPlayer.Receive, as another player's do: the person is
/// a player entity, so the client follows their voice with the simulator's answer every frame. A shout,
/// a call and talk are the same lines at ANSI S3.5's efforts. The same walker and lines in your own flat,
/// three metres off, are the reference.
/// The beacons are off, in memory (the flat's door beacon is louder than anything heard through a floor).
///
/// Writes DIR/capture.wav (the mixer's output, 16-bit), DIR/capture-float.wav (the same in 32-bit float,
/// to cut renders from) and DIR/segments.csv (name, start, seconds) for cutting.
/// </summary>
public static class FloorRenderSpike
{
    private sealed record Segment(string Name, double Start, double Seconds);

    public static int Run(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out="))?[4..] ?? "/tmp/openfps-floor-render";
        string root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..] ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();

        // The city as the client gets it (PathProbeSpike.LoadAsClient), into the client's own world.
        var snapshot = OpenFPS.Client.Core.AudioEngine.SteamAudio.PathProbeSpike.LoadAsClient(root, "city");
        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        foreach (var e in snapshot.Entities.Values) world.RegisterDefinition(e.Definition, deferAcoustics: true);
        world.SetAcousticMap(snapshot.AcousticMap!);

        string wav = Path.Combine(outDir, "capture.wav");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", wav);
        // The WAV writer's file is 16-bit and undithered: a sound through a floor at -70 dBFS is a dozen
        // steps tall in it, and turned up it is grain and holes that the mix does not have. The tap beside
        // it is the same mix in 32-bit float (MasterTap), what the renders are cut from.
        string floatWav = Path.Combine(outDir, "capture-float.wav");
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", floatWav);
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
        // Every beacon off, in memory: the flat's door beacon is louder than anything heard through a
        // floor, and the player's beacons.json is neither read nor written.
        var beacons = BeaconPreferences.InMemory();
        foreach (var category in Beacons.Categories) beacons.Set(category, false);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds, beacons: beacons);

        var segments = new List<Segment>();
        Action<double>? perFrame = null;
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                Thread.Sleep(4);
            }
        }
        void Record(string name, double seconds)
        {
            double start = clock.Elapsed.TotalSeconds, audioStart = AudioClock.Now;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds));
            // The audio clock too, to line the capture up with the debug log's timed lines.
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s; audio clock {audioStart:F3})");
        }
        void Stand(Vector3 feet, float yaw)
        {
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }

        // Selby House, the front flats of slot 1: 11F on floor 1 (carpet top 3.29 m), 21F over it on floor 2
        // (6.29 m). The flat runs x 9.85-18.85, z 166.6-184.9; the middle of it is clear of the bed and sofa.
        const float Floor1 = 3.29f, Floor2 = 6.29f;
        var below = new Vector3(14f, Floor1, 176f);
        var above = new Vector3(14f, Floor2, 176f);

        // The other person is another player, as the server sends one: an entity whose lines carry its id,
        // so the client follows their voice with the simulator's answer for that source every frame, and
        // whose footsteps leave its own body out of the way.
        const int Talker = 990_001;
        void Place(Vector3 feet) => world.RegisterDefinition(new EntityDefinition
        {
            EntityId = Talker,
            Type = EntityType.Player,
            Transform = new Transform { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One },
        });

        // Walking a line 3 m long and back, a step every 0.52 s, the feet 12 cm either side of it.
        void Walk(string name, Vector3 from, double seconds)
        {
            Place(from);
            double start = clock.Elapsed.TotalSeconds, next = start + 0.2;
            int k = 0;
            perFrame = t =>
            {
                if (t < next) return;
                next += 0.52;
                float along = (k % 12) < 6 ? (k % 6) * 0.6f : (6 - k % 6) * 0.6f;
                float side = (k & 1) == 0 ? 0.12f : -0.12f;
                audio.OnPlayerFootstep(from + new Vector3(side, 0f, along - 1.5f), "Carpet", "0", StepSlope.Level, Talker);
                k++;
            };
            Record(name, seconds);
            perFrame = null;
            Pump(1.5);
        }

        var takes = Speech.Takes.Where(t => t.Line.StartsWith("greet", StringComparison.Ordinal)
                                         && File.Exists(LabPaths.Sounds("VOICES", t.Voice, t.Line + ".ogg")))
                               .GroupBy(t => t.Voice).Select(g => g.First()).Take(4).ToList();
        Console.WriteLine($"  {takes.Count} lines, from {LabPaths.Sounds("VOICES")}");
        WorldAudioEvent Line(Speech.Take take, Vector3 mouth, float effortDb, int source) => new()
        {
            SourceEntityId = source, Label = "speech", Seed = 1,
            Sounds = new List<TransientSound> { new TransientSound
            {
                Character = SoundCharacter.Hiss, Position = mouth, LevelDb = Speech.LevelDb(effortDb),
                DecaySeconds = take.Seconds, Noisiness = 0.5f, SynthKey = Speech.Key(take.Voice, take.Line),
            } },
        };
        void Talk(string name, Vector3 feet, double seconds, float effortDb)
        {
            Place(feet);
            var mouth = feet + new Vector3(0f, Speech.MouthHeight, 0f);
            double start = clock.Elapsed.TotalSeconds;
            int k = 0; double next = start + 0.2;
            perFrame = t =>
            {
                if (t < next || k >= takes.Count) return;
                audio.WorldAudio.Receive(Line(takes[k], mouth, effortDb, Talker), AudioClock.Now);
                next += takes[k].Seconds + 0.4;
                k++;
            };
            Record(name, seconds);
            perFrame = null;
            Pump(1.5);
        }

        try
        {
            // The worker builds the scene and the rooms settle; every line is sent once to prime its render.
            Stand(below, 0f);
            Pump(10.0);
            foreach (var take in takes)
                audio.WorldAudio.Receive(Line(take, below + new Vector3(0f, Speech.MouthHeight, 3f), Speech.NormalDb, -1), AudioClock.Now);
            Pump(3.0);
            Record("silence start", 1.0);

            // In flat 11F, facing north; the person over you, half a metre north and east.
            var over = above + new Vector3(0.5f, 0f, 0.5f);
            Stand(below, 0f);
            Pump(2.0);
            Talk("below: shouting in the flat above", over, 8.0, Speech.ShoutDb);
            Talk("below: calling in the flat above", over, 8.0, Speech.LoudDb);
            Talk("below: talking in the flat above", over, 8.0, Speech.NormalDb);
            Walk("below: footsteps in the flat above", over, 7.0);
            Talk("below: talking in your own flat 3 m off", below + new Vector3(0f, 0f, 3f), 8.0, Speech.NormalDb);
            Walk("below: footsteps in your own flat 3 m off", below + new Vector3(0f, 0f, 3f), 7.0);

            // In flat 21F, the other way round.
            var under = below + new Vector3(0.5f, 0f, 0.5f);
            Stand(above, 0f);
            Pump(3.0);
            Talk("above: shouting in the flat below", under, 8.0, Speech.ShoutDb);
            Talk("above: calling in the flat below", under, 8.0, Speech.LoudDb);
            Talk("above: talking in the flat below", under, 8.0, Speech.NormalDb);
            Walk("above: footsteps in the flat below", under, 7.0);
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }
        var sb = new StringBuilder("name,start,seconds\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {wav} and segments.csv ({segments.Count} segments); compression {Loudness.DynamicRangeCompression:F2}");
        return 0;
    }
}
