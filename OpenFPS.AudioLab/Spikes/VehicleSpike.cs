using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A V8 sports car with Flowmaster 40s: started, driven past, brought back hard, parked and switched
/// off — synthesized from its own mechanism, and rendered as a RIG of emitters rather than one sound.
///
/// Three emitters, because a car is three sources in three places. The exhaust is at the back, the
/// intake and engine bay at the front, three and a half metres apart, and the tyres are at the axles
/// between them. At close range that separation is plainly audible and it tells you which way the car
/// is pointing; on a pass, the front reaches you a beat before the back, and each Dopplers on its own
/// schedule because each is at a different place. None of that has to be authored — it falls out of
/// putting the sources where they actually are.
/// </summary>
public static class VehicleSpike
{
    /// <summary>The listener stands on the pavement, a few metres off the centre line.</summary>
    private static readonly Vector3 Ear = new(4.5f, 1.7f, 0f);

    /// <summary>
    /// The same engine with three camshafts, back to back: stock, mild street, and a big lumpy one.
    ///
    /// The lope is not dialled in — it is DERIVED from the cam's duration, because that is where it
    /// comes from. A long-duration cam has a lot of valve overlap, and at low engine speed there is not
    /// enough exhaust velocity to stop reversion contaminating the intake charge, so cylinders fill
    /// unevenly and some firing events come out weak. That unevenness is the lope. It fades as the revs
    /// rise because above a couple of thousand the overlap starts helping rather than hurting, which is
    /// exactly why a cammed engine is rough at idle and clean at the top.
    ///
    /// So a player building an engine picks a duration and gets the right idle for free.
    /// </summary>
    public static int RunCamComparison(bool live)
    {
        AcousticRegistry.Initialize();
        var cams = new (string Name, float Duration)[]
        {
            ("stock", 268f),
            ("mild street", 288f),
            ("big and lumpy", 310f),
        };

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES");
        Directory.CreateDirectory(dir);
        var files = new List<(string Name, string Path, float Lope)>();

        Console.WriteLine("  The same 6.2 V8 with three camshafts. Idle, then one pull to 5500.\n");
        foreach (var (name, duration) in cams)
        {
            var baseE = EngineProfile.V8SportsFlowmaster40;
            var engine = baseE with
            {
                ExhaustCam = baseE.ExhaustCam with { DurationDegrees = duration },
                IntakeCam = baseE.IntakeCam with { DurationDegrees = duration - 4f },
            };
            var v = VehicleProfile.V8Sports with { Engine = engine };
            var orders = new List<DriveOrder>
            {
                new(DriverAction.Idling, 4.5f),
                new(DriverAction.Revving, 2.6f, 5500f),
                new(DriverAction.Idling, 1.8f),
            };
            var r = VehicleSynth.Render(v, orders, seed: 5);
            string path = Path.Combine(dir, $"cam_{name.Replace(' ', '_')}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(r.Exhaust));
            files.Add((name, path, engine.CamLope));
            Console.WriteLine($"    {name,-14} {duration:F0} deg duration -> overlap {engine.OverlapDegrees:F0} deg, lope {engine.CamLope:F2}");
            foreach (var line in r.Log)
                if (line.Contains("Idling")) Console.WriteLine($"                   {line.Trim()}");
        }

        if (!live)
        {
            Console.WriteLine($"\n  wrote {dir}\n  --vehicle-cams-live plays them.");
            return 0;
        }

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 25; i++) { provider.Update(); Thread.Sleep(8); }

            Console.WriteLine("\n  LIVE. HEADPHONES. Each one idles, then pulls to 5500.\n");
            int id = -97000;
            var (gain, reference) = Loudness.Place(110f);
            foreach (var (name, path, lope) in files)
            {
                Console.WriteLine($"    {name} (lope {lope:F2})");
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = id--,
                    SoundId = path,
                    Type = EmitterType.WorldLocked,
                    Mode = PlaybackMode.Single,
                    Position = new Vector3(0f, 0.55f, 6f),
                    Volume = gain,
                    Range = Loudness.AudibleRange(110f),
                    MinDistance = MathF.Max(reference, 3f),
                    Pitch = 1.0f,
                    TargetRegionId = AcousticConstants.GlobalRegionId,
                    EnableReverb = true,
                    IsEvent = true,
                });
                var until = DateTime.UtcNow.AddSeconds(9.4);
                while (DateTime.UtcNow < until)
                {
                    provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero,
                                            AcousticConstants.GlobalRegionId);
                    provider.Update();
                    Thread.Sleep(6);
                }
            }
            return 0;
        }
        finally { provider.Dispose(); }
    }

    /// <summary>
    /// The GAME's path: the engine runs live inside an FMOD DSP and follows a road speed, exactly as
    /// a vehicle entity does in the world. The car idles up the road, then passes the listener three
    /// times at three speeds, turning round out of earshot each time. Nothing is pre-rendered.
    /// </summary>
    public static int RunLive(string preset, float[]? speedsKmh)
    {
        AcousticRegistry.Initialize();
        var v = VehicleProfile.ByName(preset);
        var speeds = speedsKmh is { Length: > 0 } ? speedsKmh : new[] { 30f, 60f, 90f };
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 25; i++) { provider.Update(); Thread.Sleep(8); }

            Console.WriteLine($"\n  LIVE. {v.Name} — engine synthesized in the mixer, following the road speed.");
            Console.WriteLine($"  You are on the pavement, {Ear.X:F1} m off the lane. Passes at {string.Join("/", speeds)} km/h.\n");

            const int Id = -91000;
            float roadHalf = 48f;
            var pos = new Vector3(-roadHalf, 0.6f, 0f);
            float heading = MathF.PI / 2f;                   // driving +x
            var (gain, reference) = Loudness.Place(116f);
            SpatialEmitter Make(Vector3 p, Vector3 vel, float speed) => new()
            {
                EntityId = Id,
                SoundId = "engine:" + preset,
                IsSynth = true,
                EngineKey = preset,
                EngineSpeed = speed,
                EngineRunning = true,
                Type = EmitterType.WorldLocked,
                Mode = PlaybackMode.LoopOne,
                Position = p + Vector3.Transform(new Vector3(0f, 0.3f, v.ExhaustOffsetZ * 0.6f), Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f)),
                Velocity = vel,
                Volume = gain,
                Range = Loudness.AudibleRange(116f),
                MinDistance = MathF.Max(reference, 3f),
                Pitch = 1f,
                TargetRegionId = AcousticConstants.GlobalRegionId,
                EnableReverb = true,
            };
            provider.PlaySpatialSound(Make(pos, Vector3.Zero, 0f));

            // The same little state machine the server runs.
            int pass = 0; float speed = 0f; float progress = 0f; string state = "wait"; float phase = 0f;
            float from = -roadHalf, to = roadHalf;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double last = 0; float lastReport = -10f;
            while (pass < speeds.Length)
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)(now - last); last = now;
                phase += dt;
                float target = speeds[pass] / 3.6f;
                float dir = MathF.Sign(to - from);
                if (state == "wait")
                {
                    if (phase > 4f) { state = "drive"; phase = 0f; progress = 0f; }
                }
                else if (state == "drive")
                {
                    float total = MathF.Abs(to - from);
                    float remaining = MathF.Max(0f, total - progress);
                    float allowed = MathF.Sqrt(MathF.Max(0f, 2f * 5.5f * remaining));
                    float want = MathF.Min(target, allowed);
                    speed = want > speed ? MathF.Min(want, speed + 3.2f * dt) : MathF.Max(want, speed - 5.5f * dt);
                    progress += speed * dt;
                    if (progress >= total - 0.05f && speed < 0.3f)
                    {
                        speed = 0f; state = "turn"; phase = 0f; (from, to) = (to, from); pass++;
                    }
                }
                else if (state == "turn")
                {
                    heading += MathF.PI * dt / 2.5f;
                    if (phase > 2.5f) { state = "wait"; phase = 0f; }
                }
                pos = new Vector3(from + dir * MathF.Min(progress, MathF.Abs(to - from)), 0.6f, 0f);
                if (state != "drive") pos = new Vector3(from, 0.6f, 0f);
                var vel = state == "drive" ? new Vector3(dir * speed, 0f, 0f) : Vector3.Zero;
                provider.UpdateSpatialAttributes(Make(pos, vel, speed));
                if (now - lastReport >= 2.0)
                {
                    lastReport = (float)now;
                    Console.WriteLine($"    {now,5:F1}s  {state,-5}  {Vector3.Distance(pos, Ear),5:F0} m away  {speed * 3.6f,5:F0} km/h");
                }
                provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
                provider.Update();
                Thread.Sleep(6);
            }
            provider.StopSound(Id);
            return 0;
        }
        finally { provider.Dispose(); }
    }

    /// <summary>
    /// Concrete Row, with cars in it. The listener stands on the pavement of the battle spike's street
    /// — six-storey blocks both sides, side streets cut through — while vehicles drive past live.
    /// Each car's engine is the same DSP the game uses; the buildings answer it with first- and
    /// second-order image-source echoes, each a delayed copy of the engine placed at its mirrored
    /// source, tracked facade by facade as the car moves so the echoes slide and Doppler with it.
    /// On top sits the street's own reverb decay and the near-field boundary probes.
    /// </summary>
    public static int RunStreet(string[] presets, float[]? speedsKmh)
    {
        AcousticRegistry.Initialize();
        var boxes = BattleSpike.StreetBoxes();
        var surfaces = BattleSpike.StreetSurfaces(boxes);
        var speeds = speedsKmh is { Length: > 0 } ? speedsKmh : new[] { 50f, 100f };
        const float C = 340f;
        var ear = new Vector3(-9f, 1.7f, 0f);                // the pavement, west side, near the wall

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.SetAcousticMap(BattleSpike.StreetMap());
            provider.SetSimulatedReverbDecay(1400f);
            provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            var probes = new BoundaryProbe[8];
            for (int i = 0; i < 30; i++) { provider.Update(); Thread.Sleep(8); }

            Console.WriteLine("\n  LIVE. Concrete Row: 24 m street, six-storey blocks both sides, side streets through.");
            Console.WriteLine($"  You stand on the west pavement, 3 m from the wall. Passes at {string.Join("/", speeds)} km/h.\n");

            int nextEcho = -92000;
            foreach (var presetKey in presets)
            {
                var v = VehicleProfile.ByName(presetKey);
                Console.WriteLine($"  ── {v.Name}");
                const int Id = -91000;
                const float laneX = 3f;                       // the near lane, driving north (+z)
                float roadStart = -110f, roadEnd = 130f;
                var (gain, reference) = Loudness.Place(116f);
                float heading = 0f;

                SpatialEmitter Engine(Vector3 p, Vector3 vel, float speed) => new()
                {
                    EntityId = Id, SoundId = "engine:" + presetKey, IsSynth = true, EngineKey = presetKey,
                    EngineSpeed = speed, EngineRunning = true,
                    Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
                    Position = p + Vector3.Transform(new Vector3(0f, 0.3f, v.ExhaustOffsetZ * 0.6f), Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f)),
                    Velocity = vel, Volume = gain, Range = Loudness.AudibleRange(116f),
                    MinDistance = MathF.Max(reference, 3f), Pitch = 1f,
                    TargetRegionId = AcousticConstants.GlobalRegionId, EnableReverb = true,
                };
                SpatialEmitter Echo(int id, Reflection r, float g) => new()
                {
                    EntityId = id, SoundId = "echo", IsSynth = true, EchoOfEntity = Id,
                    EchoDelaySeconds = r.DelaySeconds, EchoGain = g,
                    Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
                    Position = r.ApparentPosition, Velocity = Vector3.Zero,
                    Volume = gain, Range = Loudness.AudibleRange(116f),
                    MinDistance = MathF.Max(reference, 3f), Pitch = 1f,
                    TargetRegionId = AcousticConstants.GlobalRegionId, EnableReverb = false,
                };

                var pos = new Vector3(laneX, 0.6f, roadStart);
                provider.PlaySpatialSound(Engine(pos, Vector3.Zero, 0f));
                // Echo voices, keyed by the facade they answer for.
                var voices = new Dictionary<int, (int VoiceId, float Silent)>();
                Span<Reflection> first = stackalloc Reflection[8];
                Span<Reflection> second = stackalloc Reflection[8];

                int pass = 0; float speed = 0f, progress = 0f, phase = 0f; string state = "wait";
                float from = roadStart, to = roadEnd;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double last = 0; float lastReport = -10f;
                while (pass < speeds.Length)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    float dt = (float)(now - last); last = now;
                    phase += dt;
                    float target = speeds[pass] / 3.6f;
                    float dir = MathF.Sign(to - from);
                    if (state == "wait") { if (phase > 3f) { state = "drive"; phase = 0f; progress = 0f; } }
                    else if (state == "drive")
                    {
                        float total = MathF.Abs(to - from);
                        float remaining = MathF.Max(0f, total - progress);
                        float allowed = MathF.Sqrt(MathF.Max(0f, 2f * 5.5f * remaining));
                        float want = MathF.Min(target, allowed);
                        speed = want > speed ? MathF.Min(want, speed + 3.5f * dt) : MathF.Max(want, speed - 5.5f * dt);
                        progress += speed * dt;
                        if (progress >= total - 0.05f && speed < 0.3f)
                        { speed = 0f; state = "turn"; phase = 0f; (from, to) = (to, from); pass++; }
                    }
                    else if (state == "turn") { heading += MathF.PI * dt / 2.5f; if (phase > 2.5f) { state = "wait"; phase = 0f; } }
                    if (state == "drive") { pos = new Vector3(laneX, 0.6f, from + dir * MathF.Min(progress, MathF.Abs(to - from))); }
                    else pos = new Vector3(laneX, 0.6f, from);
                    var vel = state == "drive" ? new Vector3(0f, 0f, dir * speed) : Vector3.Zero;
                    var engineEmitter = Engine(pos, vel, speed);
                    provider.UpdateSpatialAttributes(engineEmitter);

                    // The buildings answering. Reflections carry their own distance in Gain; FMOD
                    // attenuates the mirrored position itself, so hand it only the surface's share.
                    float direct = MathF.Max(1f, Vector3.Distance(engineEmitter.Position, ear));
                    int nf = ImageSource.FirstOrder(surfaces, engineEmitter.Position, ear, C, first);
                    int ns = ImageSource.SecondOrder(surfaces, engineEmitter.Position, ear, C, second);
                    var seen = new HashSet<int>();
                    void Track(Reflection r, float level)
                    {
                        float g = Math.Clamp(r.Gain * r.PathLength / direct * level, 0f, 1f);
                        if (g < 0.03f) return;
                        int key = r.SurfaceId;
                        if (!seen.Add(key)) return;
                        if (!voices.TryGetValue(key, out var vc))
                        {
                            if (voices.Count >= 6) return;
                            vc = (nextEcho--, 0f);
                            voices[key] = vc;
                            provider.PlaySpatialSound(Echo(vc.VoiceId, r, 0f));
                        }
                        provider.UpdateSpatialAttributes(Echo(vc.VoiceId, r, g));
                        voices[key] = (vc.VoiceId, 0f);
                    }
                    for (int i = 0; i < nf; i++) Track(first[i], 0.7f);
                    for (int i = 0; i < ns; i++) Track(second[i], 0.5f);
                    // Facades that no longer answer fade out and are let go.
                    foreach (var key in new List<int>(voices.Keys))
                    {
                        if (seen.Contains(key)) continue;
                        var vc = voices[key];
                        vc.Silent += dt;
                        if (vc.Silent > 0.4f) { provider.StopSound(vc.VoiceId); voices.Remove(key); continue; }
                        provider.UpdateSpatialAttributes(new SpatialEmitter { EntityId = vc.VoiceId, IsSynth = true, EchoOfEntity = Id, EchoDelaySeconds = 0.05f, EchoGain = 0f, Position = ear + new Vector3(0, 0, 30f), Volume = gain, Range = 300f, MinDistance = 3f, Pitch = 1f, TargetRegionId = AcousticConstants.GlobalRegionId });
                        voices[key] = vc;
                    }

                    if (now - lastReport >= 2.0)
                    {
                        lastReport = (float)now;
                        Console.WriteLine($"    {now,5:F1}s  {state,-5}  {Vector3.Distance(pos, ear),5:F0} m away  {speed * 3.6f,5:F0} km/h   {voices.Count} facades answering");
                    }
                    int np = BattleSpike.Probes(ear, boxes, probes);
                    provider.UpdateBoundaries(probes.AsSpan(0, np));
                    provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
                    provider.Update();
                    Thread.Sleep(6);
                }
                foreach (var vc in voices.Values) provider.StopSound(vc.VoiceId);
                provider.StopSound(Id);
                for (int i = 0; i < 60; i++) { provider.Update(); Thread.Sleep(8); }
            }
            return 0;
        }
        finally { provider.Dispose(); }
    }

    public static int Run(bool live, bool stationary = false, bool muscle = false, string? preset = null,
                          bool withBody = true, float? coupling = null, bool withShell = true,
                          float? shellLevel = null, string? shellCase = null, float? shellLoss = null,
                          float? shellWiden = null, string[]? knobs = null)
    {
        AcousticRegistry.Initialize();
        var v = preset != null ? VehicleProfile.ByName(preset) : muscle ? VehicleProfile.V8Muscle : VehicleProfile.V8Sports;
        // The same key=value sweep every other engine tool takes. A layer that can only be judged by
        // rebuilding cannot be bracketed, and bracketing is how everything here gets settled.
        if (knobs != null) v = EngineOrderSpike.Override(v, knobs);
        // body=off renders the same car with its shell taken away, so the two files can be played
        // against each other. A demo of a new layer that cannot be turned off is not a demo of it.
        if (!withBody) v = v with { Body = VehicleBody.None };
        // coupling=X overrides how much of the engine gets into the structure. It is the one number
        // in the body model still set by judgement rather than measured, so it is the one a listening
        // test has to be able to move.
        else if (coupling.HasValue && v.Body != null) v = v with { Body = v.Body with { Coupling = coupling.Value } };
        // shell=off silences the muffler CAN while leaving everything the gas does untouched, which
        // is the only way to hear what the metal is contributing on its own.
        // case=bright swaps the can's big face for its small spans, which is the difference between
        // ring at 73 Hz and ring across the whole sound.
        if (shellCase != null)
            v = v with { Engine = v.Engine with { Exhaust = v.Engine.Exhaust with {
                Muffler = v.Engine.Exhaust.Muffler with {
                    Shell = shellCase == "deep" ? VehicleBody.DeepMufflerCase : VehicleBody.MufflerCase } } } };
        // ring=X is the case's loss factor: how LONG it rings, as against how loud. "More aggressive"
        // can mean either, and they are different knobs with different sounds.
        // wide=X scales the case's free spans, which moves the PITCH of its ring without touching how
        // long it rings for. A longer tube and a wider tube are different things: ring length is the
        // tube's Q, span is its note.
        if (shellWiden.HasValue && v.Engine.Exhaust.Muffler.Shell is { } wb)
        {
            var spans = wb.PanelSpansM.Select(x => x * shellWiden.Value).ToArray();
            v = v with { Engine = v.Engine with { Exhaust = v.Engine.Exhaust with {
                Muffler = v.Engine.Exhaust.Muffler with { Shell = wb with { PanelSpansM = spans } } } } };
        }
        if (shellLoss.HasValue && v.Engine.Exhaust.Muffler.Shell != null)
            v = v with { Engine = v.Engine with { Exhaust = v.Engine.Exhaust with {
                Muffler = v.Engine.Exhaust.Muffler with {
                    Shell = v.Engine.Exhaust.Muffler.Shell with { PanelLoss = shellLoss.Value } } } } };
        if (!withShell || shellLevel.HasValue)
            v = v with { Engine = v.Engine with { Exhaust = v.Engine.Exhaust with {
                Muffler = v.Engine.Exhaust.Muffler with { ShellLevel = withShell ? shellLevel!.Value : 0f } } } };
        var e = v.Engine;
        var gb = v.Gearbox;

        Console.WriteLine($"  {v.Name}\n");
        var probe = new EngineSynth(e, VehicleSynth.SampleRate);
        foreach (var line in probe.Describe()) Console.WriteLine($"    {line}");
        Console.WriteLine($"    gearbox       {gb.TopGear}-speed manual, shift in {gb.ShiftSeconds * 1000:F0} ms, up at {gb.UpshiftRpm:F0}");
        Console.Write($"    geared for    ");
        for (int g = 1; g <= gb.TopGear; g++)
            Console.Write($"{g}:{gb.SpeedFor(gb.UpshiftRpm, g) * 3.6f:F0} ");
        Console.WriteLine("km/h at the shift point\n");

        // Sitting in front of you, worked through the rev range in neutral. No distance, no Doppler,
        // no tyres — just the engine, which is the only way to judge what it actually sounds like.
        if (stationary)
        {
            var revs = new List<DriveOrder>
            {
                new(DriverAction.Off, 0.5f),
                new(DriverAction.Cranking, 0.9f),
                new(DriverAction.Idling, 3.0f),
                new(DriverAction.Revving, 1.6f, e.RedlineRpm * 0.35f),
                new(DriverAction.Idling, 1.0f),
                new(DriverAction.Revving, 1.8f, e.RedlineRpm * 0.55f),
                new(DriverAction.Idling, 1.0f),
                new(DriverAction.Revving, 2.0f, e.RedlineRpm * 0.75f),
                new(DriverAction.Idling, 1.0f),
                new(DriverAction.Revving, 2.4f, e.RedlineRpm * 0.97f),
                new(DriverAction.Idling, 2.0f),
                new(DriverAction.ShuttingDown, 1.6f),
            };
            Console.WriteLine("  Stationary: start, four blips up the rev range in neutral, shut off.\n");
            var r = VehicleSynth.Render(v, revs);
            foreach (var line in r.Log) Console.WriteLine($"    {line}");
            string sd = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES");
            Directory.CreateDirectory(sd);
            File.WriteAllBytes(Path.Combine(sd, "v8_rev_exhaust.wav"), VehicleSynth.ToWav16(r.Exhaust));
            File.WriteAllBytes(Path.Combine(sd, "v8_rev_intake.wav"), VehicleSynth.ToWav16(r.Intake));
            Console.WriteLine($"\n  {r.Seconds:F1}s rendered -> {sd}");
            return live ? PlayStationary(v, sd) : 0;
        }

        // The drive.
        var orders = new List<DriveOrder>
        {
            new(DriverAction.Off, 0.6f),
            new(DriverAction.Cranking, 0.9f),
            new(DriverAction.Idling, 3.2f),
            new(DriverAction.Accelerating, 7.5f, 22f),    // pulls away, up through the gears, past you
            new(DriverAction.Coasting, 2.0f),             // off the throttle — this is where it crackles
            new(DriverAction.Accelerating, 6.5f, 45f),    // and back hard
            new(DriverAction.Braking, 3.4f),
            new(DriverAction.Idling, 2.2f),               // pulled up alongside
            new(DriverAction.ShuttingDown, 1.6f),
        };

        Console.WriteLine("  Rendering...");
        var render = VehicleSynth.Render(v, orders);
        foreach (var line in render.Log) Console.WriteLine($"    {line}");

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "v8_exhaust.wav"), VehicleSynth.ToWav16(render.Exhaust));
        File.WriteAllBytes(Path.Combine(dir, "v8_intake.wav"), VehicleSynth.ToWav16(render.Intake));
        File.WriteAllBytes(Path.Combine(dir, "v8_tyres.wav"), VehicleSynth.ToWav16(render.Tyres));
        File.WriteAllBytes(Path.Combine(dir, "v8_block.wav"), VehicleSynth.ToWav16(render.Block));
        Console.WriteLine($"\n  {render.Seconds:F1} s rendered -> {dir}");

        if (!live)
        {
            Console.WriteLine("  --vehicle-live drives it past you.");
            return 0;
        }
        return Play(v, render, dir);
    }

    /// <summary>
    /// The car parked six metres in front of the listener, revved in neutral.
    ///
    /// Deliberately stripped: no movement, so no Doppler; no tyres, because it is not rolling; and
    /// only two emitters, exhaust behind and intake in front. What is left is the engine, which is the
    /// only condition in which small changes to the synthesis can actually be judged. A drive-by
    /// changes distance, direction, Doppler and tyre noise all at once, and buries the thing under test.
    /// </summary>
    private static int PlayStationary(VehicleProfile v, string dir)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 25; i++) { provider.Update(); Thread.Sleep(8); }

            // Six metres ahead, facing away, so the exhaust is the near end — which is how you would
            // stand behind a car someone is revving.
            var body = new Vector3(0f, 0.55f, 6f);
            Console.WriteLine("\n  LIVE. HEADPHONES. Parked six metres in front of you, facing away.\n");
            Console.WriteLine("    exhaust at the back (nearest you), intake at the front");
            Console.WriteLine("    no tyres, no movement, no Doppler — just the engine.\n");

            int id = -95000;
            foreach (var (file, offset, db) in new[]
            {
                ("v8_rev_exhaust.wav", v.ExhaustOffsetZ, 110f),
                ("v8_rev_intake.wav", v.IntakeOffsetZ, 98f),
            })
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                var (gain, reference) = Loudness.Place(db);
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = id--,
                    SoundId = path,
                    Type = EmitterType.WorldLocked,
                    Mode = PlaybackMode.Single,
                    Position = body + new Vector3(0f, 0f, offset),
                    Volume = gain,
                    Range = Loudness.AudibleRange(db),
                    MinDistance = MathF.Max(reference, 3f),
                    Pitch = 1.0f,
                    TargetRegionId = AcousticConstants.GlobalRegionId,
                    EnableReverb = true,
                    IsEvent = true,
                });
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 20.5)
            {
                provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero,
                                        AcousticConstants.GlobalRegionId);
                provider.Update();
                Thread.Sleep(6);
            }
            return 0;
        }
        finally { provider.Dispose(); }
    }

    /// <summary>
    /// Drives the rendered car along the road past the listener.
    ///
    /// The three buffers start together and are then moved, every frame, to where that part of the car
    /// actually is. Doppler, distance, air absorption and the building reflections all come from the
    /// engine's ordinary spatial path — the same one the gunshots use — because the car is not a
    /// special case, it is three emitters that happen to be moving.
    /// </summary>
    private static int Play(VehicleProfile v, VehicleRender render, string dir)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 25; i++) { provider.Update(); Thread.Sleep(8); }

            // Lay the drive out in space so every phase happens within earshot.
            //
            // Driven as one straight line the car simply left: the second pass and the pull-up
            // happened five hundred metres away, which is correct arithmetic and a useless demo. So
            // the drive is two legs. The first comes up the road, passes, and carries on away while
            // coasting; the turn happens out there where it is quiet and distant. The second comes
            // BACK the other way, fast, and brakes to a halt beside the listener — which is why the
            // start of that leg is computed from how far the car is about to travel rather than
            // chosen: it has to end up here.
            const float LaneX = 0f;
            const float ParkedZ = -38f;
            const float RestZ = -9f;

            int legBOrder = 5;                                   // the second Accelerating
            int legBSample = render.OrderStart[legBOrder];
            float legBDistance = render.Distance[legBSample];
            float finalDistance = render.Distance[^1];
            float legBStartZ = RestZ + (finalDistance - legBDistance);

            Vector3 Body(int sample)
            {
                float d = render.Distance[sample];
                return sample < legBSample
                    ? new Vector3(LaneX, 0.55f, ParkedZ + d)
                    : new Vector3(LaneX, 0.55f, legBStartZ - (d - legBDistance));
            }

            Console.WriteLine($"  the car starts {MathF.Abs(ParkedZ):F0} m up the road, passes you, " +
                              $"turns out at {legBStartZ:F0} m, comes back and stops {MathF.Abs(RestZ):F0} m away");

            var parts = new (string File, float OffsetZ, float Db, string What)[]
            {
                // An aftermarket V8 exhaust measures around 110 dB at a metre under load — it is one
                // of the loudest things on an ordinary street. 96 was far too modest and left it sitting
                // under the tyres.
                ("v8_exhaust.wav", v.ExhaustOffsetZ, 110f, "exhaust, at the back"),
                ("v8_intake.wav",  v.IntakeOffsetZ,   97f, "intake and engine bay, at the front"),
                ("v8_tyres.wav",   v.RearAxleZ,       v.Tyres.ReferenceDb, "tyres, at the axles"),
            };

            Console.WriteLine("\n  LIVE. HEADPHONES. You are on the pavement, four and a half metres off " +
                              "the lane.\n");
            foreach (var p in parts) Console.WriteLine($"    {p.What}");
            Console.WriteLine("\n  Listen for: the starter churn before it catches, the flare and settle,");
            Console.WriteLine("  the gap in the note on each change, the crackle when he lifts, and the");
            Console.WriteLine("  front of the car reaching you a moment before the back on the pass.\n");

            int id = -90000;
            var ids = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var (gain, reference) = Loudness.Place(parts[i].Db);
                ids[i] = id--;
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = ids[i],
                    SoundId = Path.Combine(dir, parts[i].File),
                    Type = EmitterType.WorldLocked,
                    Mode = PlaybackMode.Single,
                    Position = Body(0) + new Vector3(0f, 0f, parts[i].OffsetZ),
                    Volume = gain,
                    Range = Loudness.AudibleRange(parts[i].Db),
                    MinDistance = MathF.Max(reference, 3f),
                    Pitch = 1.0f,
                    TargetRegionId = AcousticConstants.GlobalRegionId,
                    EnableReverb = true,
                    IsEvent = true,
                });
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var lastBody = Body(0);
            float lastT = 0f, lastReport = -10f;
            while (clock.Elapsed.TotalSeconds < render.Seconds + 0.7f)
            {
                float t = (float)clock.Elapsed.TotalSeconds;
                int sample = Math.Clamp((int)(t * render.SampleRate), 0, render.Distance.Length - 1);
                var body = Body(sample);

                // Velocity from the PATH, so Doppler is the car's own motion — including its sign, so
                // the shift inverts correctly when it turns round and comes back the other way.
                var vel = (body - lastBody) / MathF.Max(1e-4f, t - lastT);
                lastBody = body; lastT = t;
                if (vel.Length() > 90f) vel = Vector3.Zero;      // the one frame across the turn

                for (int i = 0; i < parts.Length; i++)
                {
                    provider.UpdateSpatialAttributes(new SpatialEmitter
                    {
                        EntityId = ids[i],
                        Position = body + new Vector3(0f, 0f, parts[i].OffsetZ),
                        Velocity = vel,
                        Volume = Loudness.Place(parts[i].Db).Gain,
                        Range = Loudness.AudibleRange(parts[i].Db),
                        MinDistance = MathF.Max(Loudness.Place(parts[i].Db).ReferenceDistance, 3f),
                        Pitch = 1.0f,
                        TargetRegionId = AcousticConstants.GlobalRegionId,
                        EnableReverb = true,
                    });
                }

                if (t - lastReport >= 2.0f)
                {
                    lastReport = t;
                    float range = Vector3.Distance(body, Ear);
                    Console.WriteLine($"    {t,5:F1}s  {range,5:F0} m away, " +
                                      $"{vel.Length() * 3.6f,5:F0} km/h" +
                                      $"{(body.Z > Ear.Z ? "   past you" : "")}");
                }

                provider.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero,
                                        AcousticConstants.GlobalRegionId);
                provider.Update();
                Thread.Sleep(6);
            }
            return 0;
        }
        finally { provider.Dispose(); }
    }
}
