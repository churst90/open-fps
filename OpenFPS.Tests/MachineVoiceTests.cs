using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// A standing machine arrives at the mixer at the level it declares. The level lives in two places that
/// must agree: the synthesis divides pascals by its full-scale pressure, and the emitter is placed on the
/// declared level (Loudness.Place); if the first is wrong the source is right at one distance only, heard
/// as the room being wrong. The machines' counterpart of EngineSynthTests' level check.
/// </summary>
public class MachineVoiceTests
{
    public MachineVoiceTests() => AcousticRegistry.Initialize();

    private const float Rate = 44100f;

    /// <summary>A machine's voice in pascals (the ring's units undone), as its declared level is
    /// measured: RMS and peak in dB SPL.</summary>
    private static (float RmsDb, float PeakDb) Measure(string preset, float seconds = 1.5f)
    {
        var spec = SmallMachineSpec.ByName(preset);
        var voice = new MachineVoiceState(spec, Rate, entityId: 4242, seed: 11);
        // A mower's declared level is the mower being pushed, at the 0.95 m/s its levels were measured at.
        if (spec.Cutting != null) voice.TargetGroundSpeed = 0.95f;
        // An air conditioner's is the machine on a hot day, its compressor running (Thermostat).
        if (spec.Compressor != null) voice.AmbientCelsius = 36f;
        int n = (int)(Rate * seconds);
        var buf = new float[n];

        // Half a second discarded: a cold machine's first output is its cabinet pressurising from
        // silence, the transient the voice discards at warm-up.
        var warm = new float[(int)(Rate * 0.5f)];
        voice.Render(warm);
        voice.Render(buf);

        double sum = 0;
        float peak = 0;
        foreach (float v in buf)
        {
            float pa = v * voice.PascalsAtFullScale;
            sum += (double)pa * pa;
            peak = MathF.Max(peak, MathF.Abs(pa));
        }
        float rms = MathF.Sqrt((float)(sum / n));
        return (20f * MathF.Log10(MathF.Max(rms, 1e-9f) / 20e-6f),
                20f * MathF.Log10(MathF.Max(peak, 1e-9f) / 20e-6f));
    }

    /// <summary>Every machine renders within 6 dB of its declared level (the loudest second of hard work,
    /// rendered here at whatever load its governor settles on): it catches a normalisation out by ten.</summary>
    [Theory]
    [InlineData("mower_push")]
    [InlineData("mower_riding")]
    [InlineData("ac_condenser")]
    [InlineData("ac_window")]
    public void AMachineRendersAtTheLevelItDeclares(string preset)
    {
        var spec = SmallMachineSpec.ByName(preset);
        var (rms, _) = Measure(preset);
        Assert.True(MathF.Abs(rms - spec.SourceLevelDb) < 6f,
            $"{preset} declares {spec.SourceLevelDb:F0} dB at 1 m and rendered {rms:F1}");
    }

    /// <summary>
    /// A mower being pushed works harder than one standing at the end of its strip (Cody: "the lawnmowers
    /// don't move"; the voice ran a constant ground speed). Grass under the deck takes torque; the
    /// governor holds rpm within about 1 %, so the throttle opening is what to measure, not the revs.
    /// </summary>
    [Theory]
    [InlineData("mower_push", 1.1f)]
    [InlineData("mower_riding", 1.9f)]
    public void AMowerWorksHarderMovingThanStanding(string preset, float walkingPace)
    {
        var spec = SmallMachineSpec.ByName(preset);
        var voice = new MachineVoiceState(spec, Rate, entityId: 4242, seed: 11);
        var block = new float[(int)(Rate * 0.1f)];
        float MeanThrottle(float speed)
        {
            voice.TargetGroundSpeed = speed;
            for (int i = 0; i < 60; i++) voice.Render(block);    // settle
            double sum = 0;
            for (int i = 0; i < 40; i++) { voice.Render(block); sum += voice.Machine.Throttle; }
            return (float)(sum / 40);
        }
        float standing = MeanThrottle(0f);
        float moving = MeanThrottle(walkingPace);
        float standingAgain = MeanThrottle(0f);
        Assert.True(moving > standing + 0.05f,
            $"{preset}: throttle {moving:F2} moving against {standing:F2} standing ({standingAgain:F2} after) — the grass took nothing");
        Assert.True(standingAgain < moving - 0.05f,
            $"{preset}: throttle {standingAgain:F2} stopped again against {moving:F2} moving ({standing:F2} before) — it never let off");
    }

    /// <summary>No machine clips: the ring runs anything past its reference through a tanh, so peaks over
    /// the headroom arrive as a square wave (the first race field on a map did). A blade strike may touch
    /// the ceiling but not sit on it.</summary>
    [Theory]
    [InlineData("mower_push")]
    [InlineData("mower_riding")]
    [InlineData("ac_condenser")]
    [InlineData("ac_window")]
    public void AMachineDoesNotLiveOnTheCeiling(string preset)
    {
        var spec = SmallMachineSpec.ByName(preset);
        var voice = new MachineVoiceState(spec, Rate, entityId: 99, seed: 3);
        var warm = new float[(int)(Rate * 0.5f)];
        voice.Render(warm);
        var buf = new float[(int)(Rate * 1.5f)];
        voice.Render(buf);

        int hot = buf.Count(v => MathF.Abs(v) > 0.8f);
        Assert.True(hot < buf.Length / 200,
            $"{preset} spent {hot * 100.0 / buf.Length:F1}% of a second and a half inside the soft ceiling");
        Assert.True(buf.Any(v => MathF.Abs(v) > 1e-4f), $"{preset} rendered silence");
    }

    /// <summary>A window unit is quieter than a condenser, which is quieter than a mower, and placement
    /// (compression and Loudness.Widen) keeps that order at a listener's distance.</summary>
    [Fact]
    public void PlacementKeepsMachinesInTheOrderTheirLevelsPutThemIn()
    {
        static float At(string preset, float metres)
        {
            var s = SmallMachineSpec.ByName(preset);
            var (gain, reference) = Loudness.Place(s.SourceLevelDb, s.ExtentMetres);
            return Loudness.RenderedGain(gain, reference, Loudness.AudibleRange(s.SourceLevelDb), metres);
        }

        foreach (float d in new[] { 3f, 10f, 30f })
        {
            Assert.True(At("mower_push", d) > At("ac_condenser", d),
                $"at {d} m a mower placed below a condenser");
            Assert.True(At("ac_condenser", d) > At("ac_window", d),
                $"at {d} m a condenser placed below a window unit");
        }
    }

    /// <summary>Every synth id a shipped prefab names is one this client can build: a misspelt id throws
    /// nothing, logs nothing and is never heard.</summary>
    [Fact]
    public void EverySynthIdAShippedPrefabNamesIsAModelThatExists()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        Assert.True(Directory.Exists(dir), $"no prefabs beside the tests at {dir}");

        var checkedIds = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (Path.GetFileName(file) == "prefab-schema.json") continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("SoundId", out var idProp)) continue;
            string id = idProp.GetString() ?? "";
            if (!id.Contains(':')) continue;

            string kind = id[..id.IndexOf(':')];
            string preset = id[(id.IndexOf(':') + 1)..];
            checkedIds.Add(id);
            switch (kind.ToLowerInvariant())
            {
                case "machine":
                    Assert.True(SmallMachineSpec.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names machine '{preset}', which is not a preset");
                    break;
                case "engine":
                    Assert.True(VehicleProfile.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names engine '{preset}', which is not a preset");
                    break;
                case "aircraft":
                    Assert.True(AircraftProfile.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names aircraft '{preset}', which is not a preset");
                    break;
                case "water":
                    // A tap of a feature, "water:<preset>/<feature>/<tap>", names its preset and a tap it has.
                    if (OpenFPS.Client.AudioEngine.Fmod.WaterFeatureVoice.ParseKey(id, out string waterPreset, out _, out int tap))
                    {
                        Assert.True(WaterFeatureSpec.Presets.ContainsKey(waterPreset),
                            $"{Path.GetFileName(file)} names water '{waterPreset}', which is not a preset");
                        Assert.True(tap < WaterFeatureSpec.Presets[waterPreset]().Taps.Length,
                            $"{Path.GetFileName(file)} names tap {tap} of '{waterPreset}', which has {WaterFeatureSpec.Presets[waterPreset]().Taps.Length}");
                        break;
                    }
                    Assert.True(WaterFeatureSpec.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names water '{preset}', which is not a preset");
                    break;
                case "fire":
                    Assert.True(FireSpec.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names fire '{preset}', which is not a preset");
                    break;
                case "foliage":
                    Assert.True(FoliageSpec.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names foliage '{preset}', which is not a preset");
                    break;
                case "flow":
                    Assert.True(RunningWaterSpec.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names running water '{preset}', which is not a preset");
                    break;
                case "shore":
                    Assert.True(ShoreSpec.Presets.ContainsKey(preset),
                        $"{Path.GetFileName(file)} names shore '{preset}', which is not a preset");
                    break;
                case "stove":
                    // A gas hob's key may carry its knobs' settings ("stove:hob4/0000>3000@..."); a prefab names it bare.
                    Assert.True(HobKey.TryParse(id, out var hob) && GasHobSpec.Presets.ContainsKey(hob.Preset),
                        $"{Path.GetFileName(file)} names gas hob '{preset}', which is not a preset");
                    break;
                default:
                    Assert.Fail($"{Path.GetFileName(file)} names '{id}', and '{kind}:' is not a model kind "
                              + "this client knows — it will be silent.");
                    break;
            }
        }
        Assert.NotEmpty(checkedIds);
    }

    /// <summary>Every aircraft and vehicle a shipped map declares can be spawned: VehicleSystem picks the
    /// library by name, and a name in neither is only logged.</summary>
    [Fact]
    public void EveryVehicleAShippedMapDeclaresCanBeBuilt()
    {
        // The server's own loader options (trailing commas, comments, Vector3), so the test reads a map
        // exactly as the server does.
        var repo = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        MachineRegistry.EnsureLoaded(Path.Combine(AppContext.BaseDirectory, "machines"));

        int seen = 0;
        foreach (var map in repo.LoadAll())
        {
            if (map.Vehicles == null) continue;
            foreach (var v in map.Vehicles)
            {
                string name = v.Name ?? v.Preset;
                // The same four kinds VehicleSystem.Spawn accepts: a road vehicle, an aircraft, a
                // small machine that is worked along a line (a mower), and a person walking.
                Assert.True(AircraftProfile.Presets.ContainsKey(v.Preset) || MachineRegistry.Knows(v.Preset)
                            || SmallMachineSpec.Presets.ContainsKey(v.Preset)
                            || string.Equals(v.Preset, "walker", StringComparison.OrdinalIgnoreCase),
                    $"map '{map.Id}': '{name}' asks for preset '{v.Preset}', which is neither a vehicle, "
                    + "an aircraft, a small machine nor a walker — it will not be spawned at all");
                seen++;
            }
            foreach (var t in map.Trains ?? new())
            {
                Assert.True(TrainProfile.Presets.ContainsKey(t.Preset),
                    $"map '{map.Id}': train '{t.Name}' asks for preset '{t.Preset}', which no TrainProfile knows");
                Assert.True(map.Tracks != null && map.Tracks.Exists(k => string.Equals(k.Id, t.Track, StringComparison.OrdinalIgnoreCase)),
                    $"map '{map.Id}': train '{t.Name}' asks for track '{t.Track}', which the map does not lay");
            }
        }
        Assert.True(seen > 0, "no shipped map declares a vehicle");
    }

    /// <summary>Every aircraft renders without faulting from every side, and louder from some sides than
    /// others. The aircraft went onto a map untested and the client died three seconds after the first
    /// one started.</summary>
    [Theory]
    [InlineData("airliner")]
    [InlineData("turboprop")]
    [InlineData("piston_single")]
    [InlineData("helicopter")]
    public void WhereAnAircraftIsHeardFromChangesItsLevel(string preset)
    {
        // Where the declared level is measured from is not written down. The listener sets a direction
        // only (no distance law), so every difference below is the source's directivity.
        var p = AircraftProfile.ByName(preset);
        var levels = new Dictionary<string, float>();
        foreach (var (name, at) in new (string, System.Numerics.Vector3)[]
                 {
                     ("in the disc plane", new System.Numerics.Vector3(120f, 0f, 0f)),
                     ("dead ahead",       new System.Numerics.Vector3(0f, 0f, 200f)),
                     ("dead astern",      new System.Numerics.Vector3(0f, 0f, -200f)),
                     ("below and behind", new System.Numerics.Vector3(40f, -180f, -120f)),
                     ("directly below",   new System.Numerics.Vector3(0f, -200f, 0f)),
                 })
        {
            var v = new AircraftVoiceState(p, Rate, seed: 5, lever: 1f);
            v.SetListener(at);
            var w = new float[(int)(Rate * 0.4f)];
            v.Render(w);
            var b = new float[(int)(Rate * 0.6f)];
            v.Render(b);
            double sum = 0;
            foreach (float x in b) { float pa = x * v.PascalsAtFullScale; sum += (double)pa * pa; }
            float db = 20f * MathF.Log10(MathF.Max(MathF.Sqrt((float)(sum / b.Length)), 1e-9f) / 20e-6f);
            Assert.True(db > 60f, $"{preset} {name}: {db:F1} dB — nothing is there");
            levels[name] = db;
        }

        // Measured 2026-10-07: airliner 9.7 dB between its quietest and loudest direction, helicopter
        // 3.2, turboprop 2.8, piston single 0.9.
        string all = string.Join(", ", levels.Select(kv => $"{kv.Key} {kv.Value:F1}"));
        Assert.True(levels.Values.Max() - levels.Values.Min() > 0.5f,
            $"{preset}: the same level from every direction ({all}); the listener's direction reaches nothing");
        // A jet's mixing noise peaks aft of the exhaust axis (JetStream.Directivity).
        if (p.Power == AircraftPower.Turbofan)
            Assert.True(levels["dead astern"] > levels["dead ahead"] + 3f, $"{preset}: the jet does not radiate aft ({all})");
        // A propeller's thickness noise is loudest in its disc's plane and falls on its axis.
        if (p.Power is AircraftPower.Piston or AircraftPower.Turboprop)
            Assert.True(levels["in the disc plane"] > levels["dead ahead"] && levels["in the disc plane"] > levels["dead astern"],
                $"{preset}: the propeller's disc plane is not its loudest side ({all})");
    }

    [Theory]
    [InlineData("airliner")]
    [InlineData("turboprop")]
    [InlineData("piston_single")]
    [InlineData("helicopter")]
    public void AnAircraftRendersAtTheLevelItDeclares(string preset)
    {
        var p = AircraftProfile.ByName(preset);
        // Placed at full power: spooling from idle, a turbofan measures 40 dB under its declared level
        // 1.5 s later.
        var voice = new AircraftVoiceState(p, Rate, seed: 5, lever: 1f);
        voice.SetListener(new System.Numerics.Vector3(40f, -180f, -120f));   // below and behind

        var warm = new float[(int)(Rate * 0.5f)];
        voice.Render(warm);
        var buf = new float[(int)(Rate * 1.0f)];
        voice.Render(buf);

        double sum = 0;
        float peak = 0;
        foreach (float v in buf)
        {
            Assert.False(float.IsNaN(v) || float.IsInfinity(v), $"{preset} rendered a non-finite sample");
            float pa = v * voice.PascalsAtFullScale;
            sum += (double)pa * pa;
            peak = MathF.Max(peak, MathF.Abs(pa));
        }
        float rms = 20f * MathF.Log10(MathF.Max(MathF.Sqrt((float)(sum / buf.Length)), 1e-9f) / 20e-6f);
        Assert.True(buf.Any(v => MathF.Abs(v) > 1e-4f), $"{preset} rendered silence");
        // Within 14 dB, not 6: below and behind, the airliner lands on its number and the turboprop and
        // helicopter come 10-13 dB under. The jet balance is pinned by ear (docs/AIRCRAFT.md), so this
        // holds only that nothing is out by an order.
        // TODO: say where an aircraft's declared level is measured from.
        Assert.True(MathF.Abs(rms - p.SourceLevelDb) < 14f,
            $"{preset} declares {p.SourceLevelDb:F0} dB at 1 m and rendered {rms:F1}");
    }

    /// <summary>The power lever travels per second, not per render call, whatever the block size: per
    /// call it moved two thirds of full travel every 11 ms.</summary>
    [Fact]
    public void ThePowerLeverTravelsRatherThanStepping()
    {
        var voice = new AircraftVoiceState(AircraftProfile.ByName("airliner"), Rate, seed: 1, lever: 1f)
        {
            TargetLever = 0f,
        };
        var buf = new float[512];
        // A tenth of a second of blocks: a lever that steps is at zero by now, one that travels is not.
        for (int i = 0; i < 9; i++) voice.Render(buf);
        Assert.True(voice.Aircraft.Lever > 0.4f,
            $"the lever fell to {voice.Aircraft.Lever:F2} in 100 ms; 1.5 s is a thrust lever's travel");
        for (int i = 0; i < 200; i++) voice.Render(buf);
        Assert.True(voice.Aircraft.Lever < 0.05f, $"the lever stalled at {voice.Aircraft.Lever:F2}");
    }

    /// <summary>
    /// The ring survives being produced, consumed and steered from three threads at once: producers
    /// racing on Produce() (a compare-exchange must let exactly one through), a consumer at the mixer's
    /// block size, and the game thread moving the listener and envelope. Written while the client was
    /// crashing on FMOD's mixer thread (docs/THE_MIXER_THREAD_CRASH.md).
    /// </summary>
    [Fact]
    public void TheRingSurvivesProducersAndAConsumerAtOnce()
    {
        var spec = SmallMachineSpec.ByName("mower_push");
        var voice = new MachineVoiceState(spec, Rate, entityId: 7, seed: 2);

        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var stop = new System.Threading.CancellationTokenSource();
        var threads = new System.Collections.Generic.List<System.Threading.Thread>();

        // Four producers, the way EngineRenderPool hands one voice to whichever worker reaches it.
        for (int p = 0; p < 4; p++)
        {
            var t = new System.Threading.Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { voice.Produce(); System.Threading.Thread.Sleep(0); } }
                catch (Exception ex) { faults.Enqueue(ex); }
            }) { IsBackground = true };
            threads.Add(t); t.Start();
        }

        // The game thread, steering it.
        var steer = new System.Threading.Thread(() =>
        {
            try
            {
                var rng = new Random(5);
                while (!stop.IsCancellationRequested)
                {
                    voice.SetListener(new System.Numerics.Vector3(
                        (float)(rng.NextDouble() * 40 - 20), (float)(rng.NextDouble() * 4),
                        (float)(rng.NextDouble() * 40 - 20)));
                    voice.Running = rng.Next(8) != 0;
                    voice.TargetEnvelope = rng.Next(6) == 0 ? 0f : 1f;
                    if (rng.Next(20) == 0) voice.Revive();
                    System.Threading.Thread.Sleep(1);
                }
            }
            catch (Exception ex) { faults.Enqueue(ex); }
        }) { IsBackground = true };
        threads.Add(steer); steer.Start();

        // The mixer: 1024-sample blocks, for a couple of seconds of wall clock.
        var block = new float[1024];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalSeconds < 2.0)
            {
                voice.Consume(block);
                foreach (float v in block)
                    Assert.False(float.IsNaN(v) || float.IsInfinity(v), "the ring handed the mixer a non-finite sample");
                System.Threading.Thread.Sleep(0);
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var t in threads) t.Join(2000);
        }

        Assert.True(faults.IsEmpty, faults.TryDequeue(out var first) ? first.ToString() : "");
    }

    /// <summary>Two machines of the same kind do not run in step: the thermostat's phase, its on and off
    /// times and the load cycle are seeded from the entity id, since forty copies of one waveform is a
    /// chorus.</summary>
    [Fact]
    public void TwoOfTheSameMachineDoNotRunInStep()
    {
        var spec = SmallMachineSpec.ByName("ac_window");
        var a = new MachineVoiceState(spec, Rate, entityId: 1001, seed: 5);
        var b = new MachineVoiceState(spec, Rate, entityId: 1002, seed: 5);
        var ba = new float[4096];
        var bb = new float[4096];
        a.Render(ba);
        b.Render(bb);

        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < ba.Length; i++) { dot += ba[i] * bb[i]; na += ba[i] * ba[i]; nb += bb[i] * bb[i]; }
        double correlation = Math.Abs(dot) / Math.Sqrt(Math.Max(na * nb, 1e-12));
        Assert.True(correlation < 0.9, $"two window units correlated at {correlation:F2} — they are one machine twice");
    }
}
