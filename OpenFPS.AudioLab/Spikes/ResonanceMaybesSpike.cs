using System.Numerics;
using System.Reflection;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Sound;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --resonance-maybes outlet|overdue|close [out=DIR]: three faults Resonance found in its copy of the sound code
/// (inbox/2026-10-17-resonance-audio-open-fps-lacks), tried on open-fps's own mixer through FMOD.
/// - outlet (3.2): a voice reading another's synth (a machine's front outlet, a cabin path, a wood's or a fire's outer
///   place) stopped and started again under its id in one tick, as a budget or a registry does. Whether the source goes
///   on rendering what the new voice reads, and whether the old voice, let go a moment later, takes the new one's share.
/// - overdue (3.1): a few hundred recorded one-offs (lengths, pitches, delays, reflections, replaced and stopped ones)
///   watched for any still playing past the latest it could end (at its lowest pitch, after its delay) plus 2 s.
/// - close (3.4): sounds opened in the background (MODE.NONBLOCKING, every recording's first play) and the device closed
///   before they finish, many times over: FMOD's loader read a file after Resonance's tests closed the device.
/// The recordings are read from ASSETS/SOUNDS beside the lab's binary (a link to the checkout's will do).
///
/// Measured 2026-10-17 on main 54d5dcdd:
/// - outlet: REPRODUCES. A front outlet or cabin path restarted under its id up to 200-250 ms after its stop (80 ms with
///   the outright stop before 0893c9b8) is undone when the old voice is released: EngineTapState.HandBack is a flag, not
///   a count, so the engine's own voice carries the share again beside the new tap, 4.3 dB louder at 20 m/s with the
///   taps muted. The engine's own voice restarted in one tick leaves the front tap reading the released engine, whose
///   play position has stopped. A fire's middle restarted in one tick leaves its outer places on the old synth, which
///   burns on when the fire is put out.
/// - overdue: does not reproduce: 1,470 one-offs in 120 s, every one playing, none held past its latest end.
/// - close: does not reproduce: 40 devices closed with 3,120 sounds still opening, no crash.
/// </summary>
public static class ResonanceMaybesSpike
{
    private static readonly OpenFPS.Sound.Hearing.HearingSettings LabHearing = OpenFPS.Sound.Hearing.HearingSettings.FromEnvironment();

    public static int Run(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-resonance-maybes";
        Directory.CreateDirectory(outDir);
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        AcousticRegistry.Initialize();
        if (args.Contains("outlet")) return Outlet(outDir);
        if (args.Contains("overdue")) return Overdue(outDir, args);
        if (args.Contains("close")) return Close(args);
        Console.WriteLine("--resonance-maybes outlet|overdue|close [out=DIR]");
        return 2;
    }

    // ── The rig ──────────────────────────────────────────────────────────────────────────────

    private static FmodAudioProvider Provider(string outDir, string name)
    {
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, name + ".fmodout.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, name + ".post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        var p = new FmodAudioProvider { Hearing = LabHearing };
        if (!p.Initialize()) throw new InvalidOperationException("provider init failed");
        p.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
        for (int i = 0; i < 20; i++) { p.Update(); Thread.Sleep(5); }
        return p;
    }

    /// <summary>Runs the game's audio loop at its 250 Hz for <paramref name="seconds"/>, <paramref name="each"/> first.</summary>
    private static void Pump(FmodAudioProvider p, System.Diagnostics.Stopwatch clock, double seconds, Action<double>? each = null)
    {
        double end = clock.Elapsed.TotalSeconds + seconds;
        while (clock.Elapsed.TotalSeconds < end)
        {
            each?.Invoke(clock.Elapsed.TotalSeconds);
            p.Update();
            Thread.Sleep(4);
        }
    }

    private static readonly MethodInfo FindActiveMethod =
        typeof(FmodAudioProvider).GetMethod("FindActive", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static FmodAudioProvider.ActiveSound? Active(FmodAudioProvider p, int id)
        => (FmodAudioProvider.ActiveSound?)FindActiveMethod.Invoke(p, new object[] { id });

    private static readonly FieldInfo CabinLiveField =
        typeof(EngineVoiceState).GetField("_cabinTapLive", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static bool CabinLive(EngineVoiceState e, int path) => ((int[])CabinLiveField.GetValue(e)!)[path] != 0;

    // ── 3.2: a voice on a shared outlet swapped in one tick ──────────────────────────────────────

    private static int Outlet(string outDir)
    {
        const string Key = "i4_midsize";
        const int Car = -900, Front = -902, Cabin = -903;
        const float Speed = 20f;
        var profile = MachineRegistry.VehicleFor(Key);
        var (gain, reference) = LabHearing.Place(profile.SourceLevelDb, 3.5f);
        var at = new Vector3(4f, 0.4f, 10f);
        // Off: the taps muted and the engine's own voice heard. On: the other way round.
        bool loud = false;
        SpatialEmitter Engine() => new()
        {
            EntityId = Car, Seed = Car, SoundId = "engine:" + Key, IsSynth = true, EngineKey = Key,
            EngineSpeed = Speed, EngineRunning = true, Type = EmitterType.EntityAttached, Mode = PlaybackMode.LoopOne,
            Position = at, ApparentPosition = at, Velocity = Vector3.Zero, Direction = Vector3.UnitZ,
            Volume = loud ? gain * 1e-4f : gain, MinDistance = reference, ExtentMetres = 3.5f, Range = 500f, Pitch = 1f, TargetRegionId = -1,
            LevelDb = profile.SourceLevelDb, EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
        };
        // The front outlet, as AudioWorld.FrontVoice makes it; muted, so the capture hears only what the engine's own
        // voice carries: its front share comes back into it if the outlet is taken for gone.
        SpatialEmitter FrontTap() => new()
        {
            EntityId = Front, SoundId = "engine-intake", IsSynth = true, IntakeOfEntity = Car, EngineKey = "",
            Type = EmitterType.EntityAttached, Mode = PlaybackMode.LoopOne,
            Position = at + new Vector3(0f, 0f, 2f), ApparentPosition = at + new Vector3(0f, 0f, 2f),
            Volume = loud ? gain : gain * 1e-4f, MinDistance = reference, Range = 500f, Pitch = 1f, TargetRegionId = -1,
            LevelDb = profile.SourceLevelDb, EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
        };
        SpatialEmitter CabinTap() => FrontTap() with { EntityId = Cabin, SoundId = "cabin", IntakeOfEntity = 0, CabinOfEntity = Car, CabinPath = 1, Volume = gain * 1e-4f };

        var p = Provider(outDir, "outlet");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<(string Name, double From, double To)>();
        try
        {
            bool front = true, cabin = true;
            void Each(double _)
            {
                p.PlaySpatialSound(Engine());
                if (front) p.PlaySpatialSound(FrontTap());
                if (cabin) p.PlaySpatialSound(CabinTap());
            }
            Pump(p, clock, 2.5, Each);
            var engine = Active(p, Car)?.EngineState ?? throw new InvalidOperationException("no engine voice");
            if (Active(p, Front)?.TapState == null || Active(p, Cabin)?.TapState == null) throw new InvalidOperationException("no tap voices");
            Console.WriteLine($"  both taps playing:                        front split off the engine {engine.SplitVoices}, cabin path 1 tapped {CabinLive(engine, 1)}");
            double t0 = clock.Elapsed.TotalSeconds;
            marks.Add(("taps playing", t0 - 1.0, t0));

            // One tick: each tap stopped and started again under its id, as the registry restarts a voice it had let go.
            p.StopSound(Front); p.PlaySpatialSound(FrontTap());
            p.StopSound(Cabin); p.PlaySpatialSound(CabinTap());
            var newFront = Active(p, Front)?.TapState;
            var newCabin = Active(p, Cabin)?.TapState;
            Console.WriteLine($"  just after the swap (new voices {(newFront != null ? "started" : "MISSING")}/{(newCabin != null ? "started" : "MISSING")}): front split {engine.SplitVoices}, cabin tapped {CabinLive(engine, 1)}");
            Pump(p, clock, 0.6, Each);
            Console.WriteLine($"  0.6 s on, the old voices released:        front split {engine.SplitVoices}, cabin tapped {CabinLive(engine, 1)}");
            Pump(p, clock, 1.9, Each);
            double t1 = clock.Elapsed.TotalSeconds;
            marks.Add(("after the swap", t1 - 1.0, t1));
            Console.WriteLine($"  2.5 s on, both new taps still playing:    front split {engine.SplitVoices}, cabin tapped {CabinLive(engine, 1)}  (should be True, True)");
            Console.WriteLine($"  the new taps are still the live voices:   front {ReferenceEquals(Active(p, Front)?.TapState, newFront)}, cabin {ReferenceEquals(Active(p, Cabin)?.TapState, newCabin)}");

            // The control: let go, wait out the leave, then start: the new tap takes the share as it should.
            front = cabin = false;
            p.StopSound(Front); p.StopSound(Cabin);
            Pump(p, clock, 1.0, Each);
            front = cabin = true;
            Pump(p, clock, 2.5, Each);
            double t2 = clock.Elapsed.TotalSeconds;
            marks.Add(("restarted after a pause", t2 - 1.0, t2));
            Console.WriteLine($"  control, restarted 1 s after the stop:    front split {engine.SplitVoices}, cabin tapped {CabinLive(engine, 1)}");

            // How long after a stop a start is still undone by the old voice's release: the window, by the stop the game
            // makes and by the outright stop before 2026-10-17 (LabLevers.HardStops).
            foreach (bool hard in new[] { false, true })
            {
                p.Lab.HardStops = hard;
                var line = new System.Text.StringBuilder($"  {(hard ? "outright stop (before 0893c9b8)" : "faded stop (now)"),-32} restart after ");
                foreach (int ms in new[] { 0, 20, 50, 80, 120, 160, 200, 250, 300, 400 })
                {
                    p.StopSound(Front);
                    front = false;
                    Pump(p, clock, ms / 1000.0, Each);
                    front = true;
                    Pump(p, clock, 0.8, Each);
                    line.Append($"{ms} ms: {(engine.SplitVoices ? "ok" : "UNDONE")}  ");
                }
                Console.WriteLine(line.ToString());
            }
            p.Lab.HardStops = false;

            // The other way round: the engine's own voice swapped in one tick under the taps. Now the engine is muted and
            // the front outlet heard, so the capture is what the tap reads.
            loud = true;
            Pump(p, clock, 2.0, Each);
            double t3 = clock.Elapsed.TotalSeconds;
            marks.Add(("front tap, engine live", t3 - 1.0, t3));
            p.StopSound(Car); p.PlaySpatialSound(Engine());
            Pump(p, clock, 0.5, Each);
            double t4 = clock.Elapsed.TotalSeconds;
            Pump(p, clock, 1.0, Each);
            marks.Add(("front tap, engine swapped", t4, t4 + 1.0));
            Console.WriteLine($"  the engine's voice swapped in one tick: the front tap reads the live engine {ReferenceEquals(Active(p, Front)?.TapState?.Source, Active(p, Car)?.EngineState)}, "
                            + $"the old engine's play position frozen at {engine.Played} (the live one's {Active(p, Car)?.EngineState?.Played})");
            loud = false;

            // A wood's outer place reads the synth of its middle's live voice; the middle swapped in one tick.
            NaturePlaces(p, clock);
        }
        finally { p.Dispose(); }

        // What the engine's own voice put out, the taps muted: its level before and after the swap.
        var (l, r, sr) = OpenFPS.Client.Core.AudioEngine.Fmod.ClapRoomSpike.ReadStereo(Path.Combine(outDir, "outlet.post.wav"));
        double captureStart = (l.Length / (double)sr) - clock.Elapsed.TotalSeconds;
        foreach (var (name, from, to) in marks)
        {
            int a = Math.Max(0, (int)((from + captureStart) * sr)), b = Math.Min(l.Length, (int)((to + captureStart) * sr));
            double sum = 0; for (int i = a; i < b; i++) sum += l[i] * (double)l[i] + r[i] * (double)r[i];
            Console.WriteLine($"  the engine's own voice, {name,-24}: {10 * Math.Log10(Math.Max(1e-20, sum / Math.Max(1, 2 * (b - a)))):F1} dBFS");
        }
        return 0;
    }

    private static void NaturePlaces(FmodAudioProvider p, System.Diagnostics.Stopwatch clock)
    {
        const int Middle = -950, Outer = -951;
        var at = new Vector3(-6f, 0f, 6f);
        SpatialEmitter Fire(int id, int place) => new()
        {
            EntityId = id, Seed = Middle, SoundId = "fire:fire_pit", IsSynth = true, PhysicalKey = "fire:fire_pit",
            EngineRunning = true, Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
            Position = at + new Vector3(place * 0.5f, 0f, 0f), ApparentPosition = at + new Vector3(place * 0.5f, 0f, 0f),
            Volume = 0.05f, MinDistance = 1f, Range = 120f, Pitch = 1f, TargetRegionId = -1, LevelDb = 67f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f, Spread = 1f,
            PlaceOfEntity = place == 0 ? 0 : Middle, Place = place,
        };
        void Each(double _) { p.PlaySpatialSound(Fire(Middle, 0)); p.PlaySpatialSound(Fire(Outer, 1)); }
        Pump(p, clock, 1.5, Each);
        var middle = Active(p, Middle)?.MachineState as NaturePlaceState;
        var outer = Active(p, Outer)?.MachineState as NaturePlaceState;
        if (middle == null || outer == null) { Console.WriteLine($"  fire: places not made (middle {middle != null}, outer {outer != null}; places {middle?.Shared.Places})"); return; }
        Console.WriteLine($"  fire, {middle.Shared.Places} place(s): the outer place reads its middle's synth: {ReferenceEquals(outer.Shared, middle.Shared)}");
        p.StopSound(Middle); p.PlaySpatialSound(Fire(Middle, 0));
        Pump(p, clock, 1.0, Each);
        var middle2 = Active(p, Middle)?.MachineState as NaturePlaceState;
        var outer2 = Active(p, Outer)?.MachineState as NaturePlaceState;
        Console.WriteLine($"  fire, the middle swapped in one tick: the outer place reads the live middle's synth: {ReferenceEquals(outer2?.Shared, middle2?.Shared)}");
        // Put out: the middle's voice carries Running to its synth; an outer place on the old synth burns on.
        void Out(double _) { p.PlaySpatialSound(Fire(Middle, 0) with { EngineRunning = false }); p.PlaySpatialSound(Fire(Outer, 1) with { EngineRunning = false }); }
        Pump(p, clock, 0.5, Out);
        Console.WriteLine($"  fire put out: the live middle's synth running {middle2?.Shared.Running}, the synth the outer place reads running {outer2?.Shared.Running}");
    }

    // ── 3.1: one-offs held past their end ──────────────────────────────────────────────────────

    private static int Overdue(string outDir, string[] args)
    {
        double seconds = double.Parse(args.FirstOrDefault(a => a.StartsWith("sec=", StringComparison.Ordinal))?[4..] ?? "90",
                                      System.Globalization.CultureInfo.InvariantCulture);
        var p = Provider(outDir, "overdue");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int Rate = 48000;
        var rng = new Random(17);
        // Recordings of a few lengths, as a bird's call, a rustle, a step, a slam.
        var lengths = new[] { 0.04, 0.15, 0.4, 0.9, 1.7, 3.2 };
        for (int k = 0; k < lengths.Length; k++)
        {
            var pcm = new float[(int)(lengths[k] * Rate)];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)((rng.NextDouble() * 2 - 1) * 0.2 * Math.Sin(Math.PI * i / pcm.Length));
            p.RegisterSynthesisedSoundFloat($"lab:oneoff{k}", pcm, Rate);
        }
        // And real recordings off disk, opened in the background as every first play is.
        var bank = new List<(string Id, double Seconds)>();
        string sounds = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS");
        if (Directory.Exists(sounds))
            foreach (var f in Directory.EnumerateFiles(sounds, "*.*", SearchOption.AllDirectories)
                                       .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(f => f, StringComparer.Ordinal).Where((_, i) => i % 7 == 0).Take(120))
            {
                string id = Path.GetRelativePath(sounds, f).Replace('\\', '/');
                if (p.TryDecode(id, out var pcm, out int ch, out int sr) && ch > 0 && sr > 0)
                    bank.Add((id, pcm.Length / (double)ch / sr));
            }
        Console.WriteLine($"  {lengths.Length} made one-offs, {bank.Count} recordings from {sounds}");

        var due = new Dictionary<int, (double LatestEnd, string What)>();
        var overdue = new Dictionary<int, (double By, string What)>();
        var heard = new HashSet<int>();
        int next = 20000, started = 0, replaced = 0, stopped = 0;
        double nextStart = 0;
        Pump(p, clock, seconds, t =>
        {
            while (t >= nextStart)
            {
                nextStart = t + 0.02 + rng.NextDouble() * 0.12;
                int id = next++;
                bool fromBank = bank.Count > 0 && rng.NextDouble() < 0.5;
                var (sid, len) = fromBank ? bank[rng.Next(bank.Count)] : ($"lab:oneoff{rng.Next(lengths.Length)}", 0.0);
                if (!fromBank) len = lengths[int.Parse(sid["lab:oneoff".Length..], System.Globalization.CultureInfo.InvariantCulture)];
                float pitch = 0.5f + (float)rng.NextDouble() * 1.5f;
                float delay = rng.NextDouble() < 0.3 ? (float)(rng.NextDouble() * 400) : 0f;
                float r = 2f + (float)rng.NextDouble() * 40f, az = (float)(rng.NextDouble() * Math.PI * 2);
                var pos = new Vector3(MathF.Cos(az) * r, (float)rng.NextDouble() * 3f, MathF.Sin(az) * r);
                bool reflection = rng.NextDouble() < 0.2;
                var e = new SpatialEmitter
                {
                    EntityId = id, Seed = id, SoundId = sid, Type = EmitterType.WorldLocked, Mode = PlaybackMode.Single, IsEvent = true,
                    Position = pos, ApparentPosition = pos, Velocity = new Vector3((float)rng.NextDouble() * 20f - 10f, 0f, 0f),
                    Volume = 0.3f, MinDistance = 1f, Range = 200f, Pitch = pitch, DelayMs = delay, TargetRegionId = -1, LevelDb = 70f,
                    EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f, IsReflection = reflection,
                    EchoScattering = reflection ? 0.4f : 0f, ReflectionOf = reflection && id > 20000 ? id - 1 : 0,
                };
                p.PlaySpatialSound(e);
                started++;
                // The slowest it can end: its delay, its length at half pitch (Doppler can halve it again), 2 s more.
                due[id] = (t + delay / 1000.0 + len / Math.Min(pitch, 0.5) * 2.0 + 2.0, sid);
                // Now and then a voice is replaced under its id, or stopped mid-play.
                double roll = rng.NextDouble();
                if (roll < 0.05 && id > 20001) { p.PlaySpatialSound(e with { EntityId = id - 1, SoundId = "lab:oneoff2" }); replaced++; due[id - 1] = (t + 2 * 0.4 / 0.5 + 2.0, "lab:oneoff2 (replacing)"); }
                else if (roll < 0.10 && id > 20001) { p.StopSound(id - 1); stopped++; }
            }
            foreach (var (id, (latest, what)) in due)
                if (p.IsPlaying(id)) heard.Add(id);
            foreach (var (id, (latest, what)) in due)
                if (t > latest && p.IsPlaying(id) && !overdue.ContainsKey(id)) overdue[id] = (t - latest, what);
            if (due.Count > 4000) foreach (var k in due.Where(d => t > d.Value.LatestEnd + 30).Select(d => d.Key).ToList()) due.Remove(k);
        });
        double now = clock.Elapsed.TotalSeconds;
        int stillHeld = due.Count(d => now > d.Value.LatestEnd && p.IsPlaying(d.Key));
        var census = p.VoiceCensus();
        p.Dispose();
        Console.WriteLine($"  {seconds:F0} s: {started} one-offs started ({replaced} replaced under an id, {stopped} stopped mid-play)");
        Console.WriteLine($"  {heard.Count} of them seen playing; {census.Refused} refused a binaural voice");
        Console.WriteLine($"  held past the latest they could end: {overdue.Count} (still held at the end: {stillHeld}); voices at the end {census.Active}");
        foreach (var (id, (by, what)) in overdue.Take(20)) Console.WriteLine($"    {id} {what}: {by:F1} s past");
        return 0;
    }

    /// <summary>How many of the provider's sounds are still being opened in the background.</summary>
    private static int StillOpening(FmodAudioProvider p)
    {
        var resources = typeof(FmodAudioProvider).GetField("_resources", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;
        var cache = (Dictionary<string, FMOD.Sound>)resources.GetType().GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(resources)!;
        int n = 0;
        foreach (var sound in cache.Values)
            if (sound.getOpenState(out var state, out _, out _, out _) == FMOD.RESULT.OK
                && state is FMOD.OPENSTATE.LOADING or FMOD.OPENSTATE.CONNECTING or FMOD.OPENSTATE.BUFFERING) n++;
        return n;
    }

    // ── 3.4: the device closed while sounds open in the background ─────────────────────────────

    private static int Close(string[] args)
    {
        int rounds = int.Parse(args.FirstOrDefault(a => a.StartsWith("rounds=", StringComparison.Ordinal))?[7..] ?? "30",
                               System.Globalization.CultureInfo.InvariantCulture);
        string sounds = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS");
        var ids = Directory.Exists(sounds)
            ? Directory.EnumerateFiles(sounds, "*.*", SearchOption.AllDirectories)
                       .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                       .Select(f => Path.GetRelativePath(sounds, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
        if (ids.Count == 0) { Console.WriteLine($"  no recordings under {sounds}"); return 1; }
        var rng = new Random(5);
        int loading = 0;
        for (int round = 0; round < rounds; round++)
        {
            Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", null);
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", null);
            var p = new FmodAudioProvider { Hearing = LabHearing };
            if (!p.Initialize()) { Console.WriteLine("  provider init failed"); return 1; }
            p.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
            // Played for the first time, so each is opened in the background (MODE.NONBLOCKING) and its play waits on
            // the open (DeferredPlay); then closed at once, mid-read.
            int n = 40 + rng.Next(80);
            for (int i = 0; i < n; i++)
                p.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = 30000 + i, SoundId = ids[rng.Next(ids.Count)], Type = EmitterType.WorldLocked, Mode = PlaybackMode.Single,
                    IsEvent = true, Position = new Vector3(2f, 0f, 2f), ApparentPosition = new Vector3(2f, 0f, 2f), Volume = 0.2f,
                    MinDistance = 1f, Range = 100f, Pitch = 1f, TargetRegionId = -1, EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                });
            if (rng.Next(2) == 0) p.Update();
            loading += StillOpening(p);
            p.Dispose();
            Console.Write(round % 10 == 9 ? $"{round + 1}\n" : ".");
        }
        Console.WriteLine($"  {rounds} devices closed, {loading} sounds still opening in the background as each was: no crash");
        return 0;
    }
}
