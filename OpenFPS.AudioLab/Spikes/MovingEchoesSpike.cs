using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Sound;
using OpenFPS.Materials;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --moving-echoes [door|pass] [sound=ring|patio] [before] [out=DIR] [root=DIR/]: a one-off that moves as it plays,
/// through the whole client path on the city map (ClientAudioSystem over the FmodAudioProvider), and its copies off the
/// walls round it (OneOffSounds.FollowCopies; Resonance's 2.6). Every 100 ms: where the sound is, and each copy's
/// bearing from the ears, its path's delay behind the sound as it is now, its level, and whether its wall still answers.
/// Then the capture's top octaves through the run, in 2 ms frames: a copy moved with a click would stand out of them.
/// - door: 1 Elm Street's patio door ("1 Elm Street back door" in city.json) slid open, its handle carried across the doorway as DoorSystem
///   sends the run (MovesTo), heard from the living room 2.6 m in.
/// - pass: the same sound carried 12 m along Selby House's south wall at 8 m/s, as a one-off on a passing vehicle would be
///   if it were sent moving; no vehicle sends one moving today (a vehicle's one-offs are short knocks, its speech and
///   its window motor followed without copies).
/// - sound=ring: a 500 Hz ring of four seconds, which shows a click; patio: the patio door's own render.
/// - before: copies left where the sound began, as before 2026-10-17.
/// Writes DIR/capture-float.wav.
/// </summary>
public static class MovingEchoesSpike
{
    private static readonly OpenFPS.Sound.Hearing.HearingSettings LabHearing = OpenFPS.Sound.Hearing.HearingSettings.FromEnvironment();

    public static int Run(string[] args)
    {
        bool before = args.Contains("before"), pass = args.Contains("pass");
        string sound = Arg(args, "sound") ?? "ring";
        string outDir = Arg(args, "out") ?? $"/tmp/openfps-moving-echoes-{(pass ? "pass" : "door")}-{(before ? "before" : "after")}";
        string root = Arg(args, "root") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();

        var snapshot = PathProbeSpike.LoadAsClient(root, "city");
        var world = new ClientWorldState();
        world.Geometry.Runner = work => work();
        world.Clear(new Vector3(4000, 400, 4000));
        foreach (var e in snapshot.Entities.Values) world.RegisterDefinition(e.Definition, deferAcoustics: true);
        world.SetAcousticMap(snapshot.AcousticMap!);

        // Engine coordinates: x east, y up, z north.
        Vector3 ears, from, to, face = Vector3.Zero;
        float moveSeconds;
        int doorId = 6274;
        float width = 0.9f, height = 2.1f, travel = 1.4f;
        if (pass)
        {
            // Selby House's south wall: carried west to east 4 m off it, the ears 10 m out.
            ears = new Vector3(15f, 1.7f, 138f);
            from = new Vector3(9f, 1.2f, 144f);
            to = new Vector3(21f, 1.2f, 144f);
            moveSeconds = 1.5f;
        }
        else
        {
            // The leaf where the map has it (the client's ids are not the map file's).
            var leaf = snapshot.Entities.Values.Select(e => e.Definition)
                .First(d => Vector3.Distance(d.Transform.Position, new Vector3(-415.4f, 1.09f, -110.675f)) < 0.05f);
            doorId = leaf.EntityId;
            var c = leaf.Transform.Position;
            var q = leaf.Transform.Rotation;
            var size = leaf.Collider.Size.X > 0f ? leaf.Collider.Size : new Vector3(width, height, 0.024f);
            width = size.X; height = size.Y;
            var along = Vector3.Transform(Vector3.UnitX, q);
            var normal = Vector3.Transform(Vector3.UnitZ, q);
            // The handle in from the latch edge by its backset, carried across the doorway as the panel slides, kept in
            // the doorway as DoorSystem.OnTheLeaf keeps it.
            from = c + along * (width / 2f - 0.06f);
            to = c - along * (width / 2f - 0.06f);
            face = normal * (size.Z / 2f + 0.02f);
            moveSeconds = travel;
            ears = new Vector3(-414.2f, 1.75f, -108.0f);
        }

        string wav = Path.Combine(outDir, "capture-float.wav");
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", wav);
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider { Hearing = LabHearing };
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        facade.InitializeSteppedByHand(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        double tapStart = clock.Elapsed.TotalSeconds;
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var beacons = BeaconPreferences.InMemory();
        foreach (var category in Beacons.Categories) beacons.Set(category, false);
        world.Clock = facade.Clock; world.Hearing = facade.Hearing;
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds, beacons: beacons);
        var oneOffs = audio.WorldAudio.Sounds;
        oneOffs.CopiesFollow = !before;
        player.Position = ears - new Vector3(0f, 1.65f, 0f);
        player.Yaw = 0f;
        player.Rotation = Quaternion.Identity;

        var ring = new TransientSound
        {
            Character = SoundCharacter.Ring, Hz = 500f, LevelDb = 80f, DecaySeconds = 4f, Noisiness = 0.05f,
            Position = from, MovesTo = to, MoveSeconds = moveSeconds, FaceNormal = face,
        };
        if (sound == "patio")
        {
            const SlidingDoor.Kind Kind = SlidingDoor.Kind.Patio;
            string key = SlidingDoor.Key(Kind, closing: false, doorId, travel, width, height);
            ring.SynthKey = key;
            ring.Character = SoundCharacter.Knock; ring.Hz = 300f; ring.Noisiness = 1f;
            ring.LevelDb = SlidingDoor.OpenLevelDb(Kind, doorId);
            ring.DecaySeconds = SlidingDoor.Seconds(Kind, false, travel);
            oneOffs.PrewarmDoors(new[] { key });
        }
        var c0 = provider.Atmosphere.SpeedOfSound;
        double received = 0;
        var rows = new List<string>();
        try
        {
            void Pump(double seconds, Action<double>? each = null)
            {
                var until = clock.Elapsed.TotalSeconds + seconds;
                while (clock.Elapsed.TotalSeconds < until)
                {
                    audio.Update(world.GetSnapshot());
                    facade.StepByHand();
                    each?.Invoke(clock.Elapsed.TotalSeconds);
                    Thread.Sleep(4);
                }
            }
            Pump(4.0);
            while (oneOffs.RendersOutstanding > 0) Pump(0.2);
            // Heard once first, so the run below is the second hearing, played on time.
            audio.WorldAudio.Receive(new WorldAudioEvent { SourceEntityId = -1, Label = "warm", Sounds = new() { ring with { Position = from + new Vector3(0, 40, 0), MovesTo = to + new Vector3(0, 40, 0) } }, Seed = 1 }, provider.Clock.Now);
            Pump(5.0);
            received = clock.Elapsed.TotalSeconds - tapStart;
            double t0 = clock.Elapsed.TotalSeconds;
            int findsBefore = oneOffs.CopyFinds;
            long ticksBefore = oneOffs.CopyFindTicks;
            audio.WorldAudio.Receive(new WorldAudioEvent { SourceEntityId = -1, Label = "run", Sounds = new() { ring }, Seed = 1 }, provider.Clock.Now);
            double next = t0;
            Console.WriteLine($"  {(pass ? "carried past Selby House" : "1 Elm Street's patio door")}, {sound}, copies {(before ? "left where it began (before)" : "following it")}");
            Console.WriteLine($"  ears at x {ears.X:F2} east, z {ears.Z:F2} north, {ears.Y:F2} up; the sound from ({from.X:F2}, {from.Z:F2}) to ({to.X:F2}, {to.Z:F2}) over {moveSeconds:F1} s");
            Console.WriteLine("     t s | sound x     z | copy: bearing deg, path delay ms now, volume dB, answered");
            Pump(ring.DecaySeconds, now =>
            {
                if (now < next) return;
                next += 0.1;
                float t = (float)(now - t0);
                var at = TransientSound.FacingListener(TransientSound.Along(from, to, moveSeconds, t), face, ears);
                float direct = Vector3.Distance(at, ears);
                var parts = new List<string>();
                foreach (var cp in oneOffs.MovingCopies().OrderBy(c => c.Voice))
                {
                    var d = cp.Position - ears;
                    float bearing = (MathF.Atan2(d.X, d.Z) * 180f / MathF.PI + 360f) % 360f;
                    float delayMs = (d.Length() - direct) / c0 * 1000f;
                    parts.Add($"{bearing,5:F1} {delayMs,5:F1} {20f * MathF.Log10(MathF.Max(1e-6f, cp.Volume)),6:F1} {(cp.Lost ? "lost" : "yes ")}");
                }
                string row = $"  {t,6:F2} | {at.X,8:F2} {at.Z,8:F2} | {string.Join(" | ", parts)}";
                rows.Add(row);
                Console.WriteLine(row);
            });
            int finds = oneOffs.CopyFinds - findsBefore;
            double ms = (oneOffs.CopyFindTicks - ticksBefore) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine($"  copies found again {finds} times, {ms:F2} ms in all, {(finds > 0 ? ms / finds : 0):F3} ms each");
            Pump(0.5);
        }
        finally { facade.Dispose(); }
        File.WriteAllLines(Path.Combine(outDir, "copies.txt"), rows);
        return Clicks(wav, received, 4.0, moveSeconds);
    }

    /// <summary>
    /// The capture's top octaves from the run's onset for <paramref name="seconds"/> (<see cref="TopOctaveSteps"/>): each
    /// 2 ms frame against the 50 ms round it, a round never quieter than 60 dB under the run. The ring's own top is
    /// steady, so a copy moved with a step would stand out of its neighbours; the worst frames, when, and how far under
    /// the run's loudest frame each is.
    /// </summary>
    private static int Clicks(string path, double received, double seconds, double moving)
    {
        var (l, r, sr) = ClapRoomSpike.ReadStereo(path);
        if (l.Length == 0) { Console.WriteLine($"  FAIL: nothing in {path}"); return 1; }
        int search = Math.Max(2, (int)(received * sr)), searchEnd = Math.Min(l.Length, search + (int)(1.5 * sr));
        double peak = 0;
        for (int i = search; i < searchEnd; i++) peak = Math.Max(peak, Math.Abs(l[i]) + Math.Abs(r[i]));
        int onset = -1;
        for (int i = search; i < searchEnd; i++) if (Math.Abs(l[i]) + Math.Abs(r[i]) >= 0.1 * peak) { onset = i; break; }
        if (peak < 1e-5 || onset < 0) { Console.WriteLine("  FAIL: the run is not in the capture"); return 1; }
        int from = onset + sr / 50, to = Math.Min(l.Length, onset + (int)(seconds * sr));
        double lead = (from - onset) / (double)sr;
        var worst = TopOctaveSteps.Measure(l, r, sr, from, to).Select(f => f with { Seconds = f.Seconds + lead }).ToList();
        Console.WriteLine($"  top octaves, onset {onset / (double)sr - received:F3} s after it was received; 2 ms frames over the 50 ms round them"
                        + $" (a round no quieter than {-TopOctaveSteps.SilenceDb:F0} dB under the run), and under the run's loudest:");
        foreach (var (name, a, b) in new[] { ("while it moves", 0.05, moving + 0.2), ("after", moving + 0.2, seconds) })
        {
            var part = worst.Where(w => w.Seconds >= a && w.Seconds < b).ToList();
            if (part.Count == 0) continue;
            var top = part.OrderByDescending(w => w.OverRoundDb).Take(3);
            Console.WriteLine($"    {name,-15} worst " + string.Join(", ", top.Select(w => $"{w.OverRoundDb:F1} dB at {w.Seconds:F2} s ({w.UnderLoudestDb:F0})"))
                            + $"; {part.Count(w => w.OverRoundDb > 10)} of {part.Count} over 10 dB");
        }
        return 0;
    }

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
