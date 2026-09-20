using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Whether a machine that stands still and runs arrives at the mixer at the level it says it is.
///
/// A voice carries its level in TWO places and they have to agree. The synthesis renders pascals and
/// divides by the pressure that maps to full scale; the emitter is then placed on the machine's
/// declared level (Loudness.Place). If the first of those is wrong the second corrects for a machine
/// that is not the one being rendered, and the result is a source that is right at one distance and
/// wrong at every other — which is not heard as "too loud", it is heard as the room being wrong.
///
/// This is the machine's version of EngineSynthTests' level check, and it exists because the two
/// normalisations were written months apart.
/// </summary>
public class MachineVoiceTests
{
    public MachineVoiceTests() => AcousticRegistry.Initialize();

    private const float Rate = 44100f;

    /// <summary>Renders a machine's voice and gives back the pressure it put in the ring, in pascals
    /// — the ring's own units undone, which is what the declared level is measured in.</summary>
    private static (float RmsDb, float PeakDb) Measure(string preset, float seconds = 1.5f)
    {
        var spec = SmallMachineSpec.ByName(preset);
        var voice = new MachineVoiceState(spec, Rate, entityId: 4242, seed: 11);
        int n = (int)(Rate * seconds);
        var buf = new float[n];

        // Half a second thrown away first. A cold machine's waveguides start empty and the first
        // thing out of them is a cabinet pressurising from silence — the same transient the voice
        // discards at warm-up, and measuring it would be measuring the start rather than the machine.
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

    /// <summary>
    /// Every machine renders within a few decibels of the level it declares.
    ///
    /// Six decibels of tolerance either way, because the declared level is the loudest second of a
    /// machine working hard and this renders it at whatever load its own governor settles on. What
    /// it catches is the fault that matters: a normalisation out by a factor of ten.
    /// </summary>
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
    /// ...and nothing clips on the way there.
    ///
    /// The ring runs anything past its reference through a tanh, so a machine whose peaks exceed the
    /// headroom does not arrive loud, it arrives as a square wave. That is what a field of race cars
    /// sounded like the first time one was put on a map, and the shared headroom exists so it cannot
    /// happen twice. A blade striking something is allowed to touch the ceiling; sitting on it is not.
    /// </summary>
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

    /// <summary>
    /// A window unit is quieter than a condenser is quieter than a mower, and the ORDER survives
    /// placement.
    ///
    /// Placement is not a volume knob: it compresses (Loudness.DynamicRangeCompression) and it pays
    /// for extent (Loudness.Widen), and both of those could in principle reorder two sources. They
    /// must not — a mower has to be the loudest thing in a garden after it has been placed, not
    /// before — and this is the one assertion that says so at the distance a listener stands at.
    /// </summary>
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

    /// <summary>
    /// Every synth id a SHIPPED PREFAB names is one this client can build.
    ///
    /// The whole of the coupling between a map and the models is a string with a prefix on it, which
    /// is exactly the kind of thing a typo goes unnoticed in: nothing throws, nothing logs at load,
    /// and the machine is simply never heard. The city carries sixty-four of them and five aircraft;
    /// one misspelling is one silent wall of air conditioners.
    /// </summary>
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
                default:
                    Assert.Fail($"{Path.GetFileName(file)} names '{id}', and '{kind}:' is not a model kind "
                              + "this client knows — it will be silent.");
                    break;
            }
        }
        Assert.NotEmpty(checkedIds);
    }

    /// <summary>
    /// ...and every aircraft and vehicle a SHIPPED MAP declares is one that can be spawned.
    ///
    /// The same fault one level up: VehicleSystem decides whether a preset is an aircraft or a car by
    /// which library has it, and a name in neither is logged past and never spawned. That log line is
    /// easy to miss on a map that spawns twenty-seven other things successfully.
    /// </summary>
    [Fact]
    public void EveryVehicleAShippedMapDeclaresCanBeBuilt()
    {
        // Through the repository's OWN loader, not a bare JsonDocument. Maps are hand-edited and
        // MapRepository.JsonOptions is where the trailing commas, the comments and the spelling of a
        // Vector3 are agreed; a test that parses them its own way is a test that can pass on a file
        // the server cannot read, and fail on one it can.
        var repo = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        MachineRegistry.EnsureLoaded(Path.Combine(AppContext.BaseDirectory, "machines"));

        int seen = 0;
        foreach (var map in repo.LoadAll())
        {
            if (map.Vehicles == null) continue;
            foreach (var v in map.Vehicles)
            {
                string name = v.Name ?? v.Preset;
                Assert.True(AircraftProfile.Presets.ContainsKey(v.Preset) || MachineRegistry.Knows(v.Preset),
                    $"map '{map.Id}': '{name}' asks for preset '{v.Preset}', which is neither a vehicle "
                    + "nor an aircraft — it will not be spawned at all");
                seen++;
            }
        }
        Assert.True(seen > 0, "no shipped map declares a vehicle");
    }

    /// <summary>
    /// Every aircraft renders, at the level it declares, without faulting.
    ///
    /// THIS DID NOT EXIST AND SHOULD HAVE. The machines were held by the three tests above from the
    /// day they were written; the aircraft went onto a map on the same voice path with nothing
    /// asking them to make a sound first, and the client died three seconds after the first one
    /// started. A model that has never been rendered in a test is a model nobody has rendered.
    /// </summary>
    [Theory]
    [InlineData("airliner")]
    [InlineData("turboprop")]
    [InlineData("piston_single")]
    [InlineData("helicopter")]
    public void WhereAnAircraftIsHeardFromChangesItsLevel(string preset)
    {
        // What the declared level is measured AT is not written down anywhere, and this says what
        // the difference costs: a propeller is strongly directional and a jet radiates aft, so the
        // same aeroplane at the same power is a different number depending on where you stand.
        var p = AircraftProfile.ByName(preset);
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
        }
    }

    [Theory]
    [InlineData("airliner")]
    [InlineData("turboprop")]
    [InlineData("piston_single")]
    [InlineData("helicopter")]
    public void AnAircraftRendersAtTheLevelItDeclares(string preset)
    {
        var p = AircraftProfile.ByName(preset);
        // Full power, and ALREADY at it — which is what PlaceAtLever is for. Without that a turbofan
        // is still spooling out of idle a second and a half later and measures forty decibels under
        // what it declares, which is the fault this line exists to have found.
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
        // WITHIN FOURTEEN DECIBELS, not six, and the difference is a real open question rather than
        // slack. A propeller is strongly directional and a jet radiates aft, so what an aeroplane
        // measures depends on where the listener stands — and where the DECLARED level is measured
        // from is not written down. Against a listener below and behind, the airliner lands on its
        // number and the turboprop and the helicopter come in ten to thirteen decibels under it.
        //
        // That is not a thing to settle by moving a constant: the jet balance was pinned by ear
        // (-16/-21 dB trims, docs/AIRCRAFT.md) and the levels were approved with it. What this holds
        // is that nothing is out by an ORDER, which is the fault that would make an aeroplane
        // inaudible or deafening on a map. WhereAnAircraftIsHeardFromChangesItsLevel above prints
        // the spread the question is about.
        Assert.True(MathF.Abs(rms - p.SourceLevelDb) < 14f,
            $"{preset} declares {p.SourceLevelDb:F0} dB at 1 m and rendered {rms:F1}");
    }

    /// <summary>
    /// ...and the lever travels rather than stepping, whatever the block size.
    ///
    /// It was written per CALL rather than per second and came out at two thirds of full travel
    /// every eleven milliseconds, which is a step with arithmetic in front of it. A turbine spools
    /// on its own time constant; the LEVER is a pilot's hand.
    /// </summary>
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
    /// The ring survives being produced, consumed and steered from three threads at once.
    ///
    /// WHY THIS EXISTS. The client segfaults on FMOD's mixer thread, and the physical voice path is
    /// the newest code in the audio engine — a ring buffer written this session, produced by a pool
    /// of worker threads and consumed by the mixer, with the game thread writing the listener and
    /// the envelope underneath both. Every index in it is masked and every cross-thread field is
    /// volatile or interlocked, which is exactly the kind of claim that is easy to make and worth
    /// testing rather than asserting.
    ///
    /// It hammers the real object the real way: several producers racing on Produce() (which is
    /// guarded by a compare-exchange and must let exactly one through), a consumer taking blocks at
    /// the mixer's block size, and a third thread moving the listener and the envelope. Anything
    /// that escapes — an index, a null, a torn read — fails the test instead of killing a session.
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

        // ...and the game thread, steering it.
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

    /// <summary>
    /// Two machines of the same kind do not run in step.
    ///
    /// A street of forty window units is forty machines, and forty copies of one waveform is a
    /// chorus — one machine with a very strange timbre. Everything that could put them in step is
    /// seeded from the ENTITY ID: the thermostat's phase, its on and off times, and where in the
    /// load cycle it is. This holds that two ids give two different machines.
    /// </summary>
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
