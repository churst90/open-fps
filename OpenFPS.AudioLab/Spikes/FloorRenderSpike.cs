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
/// OnPlayerFootstep on carpet, lines through WorldAudioPlayer.Receive, as another player's do (a shout is
/// the same lines at ShoutDb). The same walker and lines in your own flat, three metres off, are the
/// reference. Steps are airborne only: what a footfall sends through the structure is not modelled.
/// Run with XDG_CONFIG_HOME pointing at a scratch folder whose openfps/beacons.json turns the beacons
/// off: the flat's door beacon is louder than anything heard through a floor, and the real settings stay
/// untouched.
///
/// Writes DIR/capture.wav (the mixer's output) and DIR/segments.csv (name, start, seconds) for cutting.
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
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);

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
            double start = clock.Elapsed.TotalSeconds;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)");
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

        // Walking a line 3 m long and back, a step every 0.52 s, the feet 12 cm either side of it.
        void Walk(string name, Vector3 from, double seconds)
        {
            double start = clock.Elapsed.TotalSeconds, next = start + 0.2;
            int k = 0;
            perFrame = t =>
            {
                if (t < next) return;
                next += 0.52;
                float along = (k % 12) < 6 ? (k % 6) * 0.6f : (6 - k % 6) * 0.6f;
                float side = (k & 1) == 0 ? 0.12f : -0.12f;
                audio.OnPlayerFootstep(from + new Vector3(side, 0f, along - 1.5f), "Carpet", "0");
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
        WorldAudioEvent Line(Speech.Take take, Vector3 mouth, float effortDb = Speech.NormalDb) => new()
        {
            SourceEntityId = -1, Label = "speech", Seed = 1,
            Sounds = new List<TransientSound> { new TransientSound
            {
                Character = SoundCharacter.Hiss, Position = mouth, LevelDb = Speech.LevelDb(effortDb),
                DecaySeconds = take.Seconds, Noisiness = 0.5f, SynthKey = Speech.Key(take.Voice, take.Line),
            } },
        };
        void Talk(string name, Vector3 feet, double seconds, float effortDb = Speech.NormalDb)
        {
            var mouth = feet + new Vector3(0f, Speech.MouthHeight, 0f);
            double start = clock.Elapsed.TotalSeconds;
            int k = 0; double next = start + 0.2;
            perFrame = t =>
            {
                if (t < next || k >= takes.Count) return;
                audio.WorldAudio.Receive(Line(takes[k], mouth, effortDb), AudioClock.Now);
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
            foreach (var take in takes) audio.WorldAudio.Receive(Line(take, below + new Vector3(0f, Speech.MouthHeight, 3f)), AudioClock.Now);
            Pump(3.0);
            Record("silence start", 1.0);

            // In flat 11F, facing north; the walker and talker over you, half a metre north and east.
            Stand(below, 0f);
            Pump(2.0);
            Talk("below: shouting in the flat above", above + new Vector3(0.5f, 0f, 0.5f), 8.0, Speech.ShoutDb);
            Talk("below: talking in the flat above", above + new Vector3(0.5f, 0f, 0.5f), 8.0);
            Walk("below: footsteps in the flat above", above + new Vector3(0.5f, 0f, 0.5f), 7.0);
            Talk("below: talking in your own flat 3 m off", below + new Vector3(0f, 0f, 3f), 8.0);
            Walk("below: footsteps in your own flat 3 m off", below + new Vector3(0f, 0f, 3f), 7.0);

            // In flat 21F, the other way round.
            Stand(above, 0f);
            Pump(3.0);
            Talk("above: shouting in the flat below", below + new Vector3(0.5f, 0f, 0.5f), 8.0, Speech.ShoutDb);
            Talk("above: talking in the flat below", below + new Vector3(0.5f, 0f, 0.5f), 8.0);
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
