using System.Numerics;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Hearing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The loudspeaker (LoudspeakerSpec, LoudspeakerChain, Radiator): a public-address horn and a megaphone
/// as amplifier, driver, horn and mouth, held to their datasheets. Cody, 2026-10-10: "make the resulting
/// audio actually low quality and sound like a megaphone speaker too". Before, the recording played
/// clean and the cone was one flat gain, so behind the horn was the same voice 9 dB down.
/// </summary>
public class LoudspeakerTests
{
    private readonly ITestOutputHelper _o;
    public LoudspeakerTests(ITestOutputHelper o) => _o = o;
    private const int Rate = 48000;

    private static double Db(double x) => 20.0 * Math.Log10(Math.Max(1e-15, x));

    // ── The datasheets ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Atlas AP-15T: 106 dB at 1 W / 1 m averaged over 500-6000 Hz, ±5 dB over that band, 70 degrees
    /// at -6 dB in the 2 kHz octave. The sensitivity is calibrated, the response and the coverage are
    /// not: they come from the driver's passband and a 200 mm mouth.
    /// </summary>
    [Fact]
    public void ThePagingHornMeetsItsDatasheet()
    {
        var spec = LoudspeakerSpec.PaHorn;
        var band = Enumerable.Range(0, 49).Select(i => 500.0 * Math.Pow(2.0, i / 12.0)).Where(f => f <= 6000).ToArray();
        var h = LoudspeakerChain.Response(spec, Rate, band);
        double meanPower = h.Average(x => x * x);
        double oneWatt = 10 * Math.Log10(meanPower * spec.LoadOhms) - Db(20e-6);
        _o.WriteLine($"1 W, 500-6000 Hz average: {oneWatt:F2} dB");
        Assert.InRange(oneWatt, 105.5, 106.5);

        // ±5 dB about the band's mean in the datasheet; the 500 Hz edge sits 2 dB further out, under the
        // mouth's cutoff (546 Hz), and is allowed it. Third-octave averages, as a response is plotted.
        double meanDb = 10 * Math.Log10(meanPower);
        foreach (double centre in new[] { 500.0, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000 })
        {
            var third = Enumerable.Range(-2, 5).Select(k => centre * Math.Pow(2.0, k / 15.0)).ToArray();
            double db = 10 * Math.Log10(LoudspeakerChain.Response(spec, Rate, third).Average(x => x * x)) - meanDb;
            _o.WriteLine($"{centre,6:0} Hz {db:+0.0;-0.0} dB");
            Assert.InRange(db, centre <= 500 ? -7.5 : -5.5, 5.5);
        }

        var rad = Radiator.For(spec);
        float cover = 0f;
        for (float d = 0; d <= 90; d += 0.5f) if (rad.Gain(3, d * MathF.PI / 180f) >= 0.5012f) cover = 2f * d;
        _o.WriteLine($"coverage at -6 dB, 2 kHz octave: {cover:F0} degrees");
        Assert.InRange(cover, 60f, 82f);
    }

    /// <summary>The TOA ER-1206: 450-6,000 Hz at -20 dB. Nothing of a voice's chest below 300 Hz.</summary>
    [Fact]
    public void TheMegaphoneIsTwentyDownAtItsDatasheetEdges()
    {
        var spec = LoudspeakerSpec.Megaphone;
        double[] f = { 200, 300, 450, 1250, 1600, 2000, 2500, 6000 };
        var h = LoudspeakerChain.Response(spec, Rate, f);
        double peak = LoudspeakerChain.Response(spec, Rate, Enumerable.Range(0, 60).Select(i => 400.0 * Math.Pow(2.0, i / 12.0)).ToArray()).Max();
        for (int i = 0; i < f.Length; i++) _o.WriteLine($"{f[i],6:0} Hz {Db(h[i] / peak):+0.0;-0.0} dB");
        Assert.True(Db(h[2] / peak) < -14, "450 Hz should be near the datasheet's -20 dB");
        Assert.True(Db(h[7] / peak) < -12, "6 kHz should be near the datasheet's -20 dB");
        Assert.True(Db(h[1] / peak) < -25 && Db(h[0] / peak) < -35, "a megaphone has no bottom end");
        for (int i = 3; i <= 6; i++) Assert.True(Db(h[i] / peak) > -8, $"{f[i]} Hz is the megaphone's band");
    }

    // ── The mouth ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mouth radiates all round while it is small against the wavelength and beams as it grows; behind
    /// a horn in free air the bass is nearly all there, the top almost gone. A ceiling speaker gives
    /// nothing behind its baffle. On the axis every band is unity: the render is the on-axis pressure.
    /// </summary>
    [Fact]
    public void AHornIsDarkBehindAndAllRoundInTheBass()
    {
        var horn = new Radiator(0.2f, LoudspeakerMount.Horn, -60f);
        float back = MathF.PI;
        for (int b = 0; b < Radiator.Bands; b++) Assert.Equal(1f, horn.Gain(b, 0f), 3);
        Assert.True(Db(horn.Gain(0, back)) > -3, "250 Hz goes all round a 200 mm mouth");
        Assert.True(Db(horn.Gain(2, back)) is < -6 and > -16, "1 kHz is the thin spill behind it");
        Assert.True(Db(horn.Gain(3, back)) < -15, "2 kHz is beamed");
        Assert.True(Db(horn.Gain(5, back)) < -30, "8 kHz is gone behind it");
        // Narrower with frequency, and the directivity index rising with it.
        for (int b = 1; b < Radiator.Bands; b++)
        {
            Assert.True(horn.Gain(b, MathF.PI / 3f) < horn.Gain(b - 1, MathF.PI / 3f) + 1e-4f);
            Assert.True(horn.DirectivityIndexDb(b) > horn.DirectivityIndexDb(b - 1));
        }
        var ceiling = new Radiator(0.2f, LoudspeakerMount.Baffled, -60f);
        Assert.True(Db(ceiling.Gain(0, back)) < -40, "nothing behind a baffle");
        Assert.True(Db(ceiling.Gain(0, MathF.PI * 0.45f)) > -1, "a hemisphere in the bass");
    }

    /// <summary>The six bands sum to the input (an all-pass of it) at equal gains, and a horn's power
    /// all round then its beam over that power is its beam: the two units a voice carries.</summary>
    [Fact]
    public void TheBandsSumToTheVoiceAndPowerThenBeamIsTheBeam()
    {
        var rad = Radiator.For(LoudspeakerSpec.PaHorn);
        foreach (double hz in new[] { 150.0, 400, 1000, 2500, 4500, 9000 })
        {
            Assert.InRange(Db(SineThrough(hz, new[] { Unit() })), -0.1, 0.1);

            var power = new RadiatorState(); power.Configure(Rate);
            var beam = new RadiatorState(); beam.Configure(Rate);
            Span<float> g = stackalloc float[Radiator.Bands];
            rad.PowerGains(g); power.Start(g);
            float cos = MathF.Cos(MathF.PI * 0.75f);
            rad.BeamOverPower(cos, g); beam.Start(g);
            var direct = new RadiatorState(); direct.Configure(Rate);
            rad.Gains(cos, g); direct.Start(g);
            double cascade = Db(SineThrough(hz, new[] { power, beam })), single = Db(SineThrough(hz, new[] { direct }));
            _o.WriteLine($"{hz,6:0} Hz: power then beam {cascade:F1} dB, beam alone {single:F1} dB");
            Assert.InRange(cascade - single, -1.5, 1.5);
        }

        static RadiatorState Unit() { var s = new RadiatorState(); s.Configure(Rate); return s; }
    }

    private static double SineThrough(double hz, RadiatorState[] chain)
    {
        int n = Rate;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = MathF.Sin((float)(2 * Math.PI * hz * i / Rate));
        foreach (var s in chain)
        {
            var y = new float[n];
            s.Process(x, y, 1);
            x = y;
        }
        double sum = 0;
        for (int i = n / 2; i < n; i++) sum += x[i] * (double)x[i];
        return Math.Sqrt(sum / (n / 2)) / Math.Sqrt(0.5);
    }

    /// <summary>The mixer's callback: no allocation, so no collection can start on the mixer thread.</summary>
    [Fact]
    public void TheBeamAllocatesNothing()
    {
        var s = new RadiatorState();
        s.Configure(Rate);
        var input = new float[2048];
        var output = new float[2048];
        for (int i = 0; i < input.Length; i++) input[i] = MathF.Sin(i * 0.1f);
        Span<float> g = stackalloc float[Radiator.Bands];
        var rad = Radiator.For(LoudspeakerSpec.Megaphone);
        for (int k = 0; k < 50; k++) { rad.Gains(0.3f, g); s.SetTargets(g); s.Process(input, output, 2); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 200; k++)
        {
            rad.Gains(MathF.Cos(k * 0.05f), g);
            s.SetTargets(g);
            s.Process(input, output, 2);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── The level that comes out ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The PA's level is what the chain makes of the program at 15 W: speech about ten decibels under the
    /// datasheet's 116 dB rated output, not the 117 dB the old "full scale to 12 m" stood for. The buffer
    /// is kept as a speech line is, its gated RMS at -28 dBFS with its full scale declared.
    /// </summary>
    [Fact]
    public void TheDeclaredLevelComesOutOfTheModel()
    {
        var (program, rate) = Program("ANNOUNCE", "st_louis_welcome");
        var r = LoudspeakerChain.Render("test", LoudspeakerSpec.PaHorn, program, rate, Rate);
        _o.WriteLine($"PA: {r.SplDb:F1} dB SPL gated at 1 m, peaks {r.PeakSplDb:F1}, declared {r.LevelDb:F1}, clipped {r.ClippedShare:P1}, "
                   + $"excursion {r.MaxExcursion:F2}, room {Db(r.RadiatedGain):F1} dB");
        Assert.InRange(r.SplDb, 100f, 112f);
        Assert.True(r.PeakSplDb < LoudspeakerSpec.PaHorn.RatedSplDb + 10f, "peaks bounded by the amplifier");
        Assert.InRange(Timbre.GatedRms(r.Pcm, r.Rate), Speech.BufferRmsDbfs - 0.5f, Speech.BufferRmsDbfs + 0.5f);
        Assert.InRange(r.LevelDb - r.SplDb, -Speech.BufferRmsDbfs - 0.5f, -Speech.BufferRmsDbfs + 0.5f);
        Assert.True(r.Pcm.All(float.IsFinite) && r.Pcm.Max(MathF.Abs) < 1f);
        // Placed by the law from that level: a reference distance, not the authored 12 m.
        var (gain, reference) = Loudness.Place(r.LevelDb);
        _o.WriteLine($"placed: gain {gain:F2}, reference {reference:F1} m");
        Assert.True(reference is > 5f and < 25f);
    }

    /// <summary>
    /// Pushed, a megaphone's battery amplifier clips and its driver runs out of travel: a 1 kHz tone
    /// shouted into it comes out with far more of its power in harmonics than the same tone spoken.
    /// </summary>
    [Fact]
    public void PushingAMegaphoneDistortsIt()
    {
        var tone = new float[Rate];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000f * i / Rate);
        double Harmonics(LoudspeakerSpec spec)
        {
            var y = LoudspeakerChain.RenderPascals(spec, tone, Rate, Rate, loop: true, out var stats);
            var mid = y.AsSpan(Rate / 4, Rate / 2).ToArray();
            double total = mid.Sum(v => (double)v * v);
            double fundamental = Goertzel(mid, 1000.0, Rate);
            _o.WriteLine($"{spec.Name}: clipped {stats.ClippedShare:P1}, harmonics {100 * (1 - fundamental / total):F1} % of the power");
            return 1 - fundamental / total;
        }
        double spoken = Harmonics(LoudspeakerSpec.Megaphone with { PeakOverClipDb = -6f, CompressorRatio = 1f });
        double shouted = Harmonics(LoudspeakerSpec.MegaphoneShouted);
        // Half volume is not clean either: the driver is near its travel at 1 kHz (about 5 %).
        Assert.True(spoken < 0.08, "under the clip a megaphone is a band-pass, not a fuzz box");
        Assert.True(shouted > 0.15 && shouted > 5 * spoken, "shouted into, it distorts");
    }

    private static double Goertzel(float[] x, double hz, int rate)
    {
        double w = 2 * Math.PI * hz / rate, re = 0, im = 0;
        for (int i = 0; i < x.Length; i++) { re += x[i] * Math.Cos(w * i); im -= x[i] * Math.Sin(w * i); }
        return 2 * (re * re + im * im) / x.Length;
    }

    // ── In the game ────────────────────────────────────────────────────────────────────────────

    /// <summary>The shipped PA and megaphone play through their models, and say nothing of a cone or a
    /// reference distance.</summary>
    [Fact]
    public void TheShippedSpeakersPlayThroughTheirModels()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        foreach (var (prefab, speaker) in new[] { ("pa_speaker", "pa_horn"), ("space_megaphone", "megaphone") })
        {
            var t = PrefabRepository.FromJson(File.ReadAllText(Path.Combine(dir, prefab + ".json")));
            Assert.Equal(speaker, t.Loudspeaker);
            var result = PrefabValidator.Validate(t);
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
            Assert.True(ModelLibrary.Knows(ModelLibrary.Kinds.Loudspeaker, speaker));
            Assert.Null(t.ConeInsideAngle);
            Assert.Null(t.MinDistance);
        }
        var unknown = Assert.Throws<ArgumentException>(() => PrefabRepository.FromJson(
            """{ "Id": "x", "Name": "x", "HasEmitter": true, "SoundId": "A/b", "Loudspeaker": "gramophone" }"""));
        Assert.Contains("gramophone", unknown.Message);
        var placed = PrefabRepository.FromJson("""{ "Id": "x", "Name": "x", "HasEmitter": true, "SoundId": "A/b", "Loudspeaker": "megaphone", "MinDistance": 4 }""");
        Assert.Contains(PrefabValidator.Validate(placed).Warnings, w => w.Contains("MinDistance is ignored"));
    }

    /// <summary>Components go positionally: the speaker is appended, and survives the wire.</summary>
    [Fact]
    public void ALoudspeakerSurvivesTheWire()
    {
        var e = new SoundEmitterComponent { SoundId = "ANNOUNCE/x", Loudspeaker = "pa_horn", WindowsOpen = 0.5f };
        var back = MemoryPackSerializer.Deserialize<SoundEmitterComponent>(MemoryPackSerializer.Serialize(e));
        Assert.Equal("pa_horn", back.Loudspeaker);
        Assert.Equal(0.5f, back.WindowsOpen);
        var last = typeof(SoundEmitterComponent).GetProperties().Where(p => p.CanWrite).OrderBy(p => p.MetadataToken).Last();
        Assert.Equal(nameof(SoundEmitterComponent.Loudspeaker), last.Name);
    }

    /// <summary>
    /// A copy off a wall left the horn toward the wall, not toward you: a horn aimed down a street, with
    /// you behind it, still throws its top at the wall ahead and that copy comes back bright.
    /// </summary>
    [Fact]
    public void ACopyOffAWallLeftTheHornTowardTheWall()
    {
        var (program, rate) = Program("ANNOUNCE", "st_louis_welcome");
        var r = LoudspeakerChain.Render("test", LoudspeakerSpec.PaHorn, program, rate, Rate);
        var horn = new EntitySnapshot
        {
            Id = 7,
            Definition = new EntityDefinition { SoundEmitter = new SoundEmitterComponent { Direction = Vector3.UnitZ } },
            Transform = new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        var listener = new Vector3(0f, 0f, -5f);
        // A wall 10 m ahead: the image is 20 m ahead and the sound left along the axis.
        var ahead = OpenFPS.Client.Core.ClientAudioSystem.CopyBeam(r, horn, new Vector3(0f, 0f, 20f), listener);
        // A wall 5 m behind the listener: the image is behind too, and the sound left backwards.
        var behind = OpenFPS.Client.Core.ClientAudioSystem.CopyBeam(r, horn, new Vector3(0f, 0f, -20f), listener);
        _o.WriteLine($"wall ahead {Db(ahead.Low):F1}/{Db(ahead.Mid):F1}/{Db(ahead.High):F1} dB, behind {Db(behind.Low):F1}/{Db(behind.Mid):F1}/{Db(behind.High):F1}");
        Assert.True(ahead.High > 0.9f && ahead.Mid > 0.9f);
        Assert.True(Db(behind.High) < -20 && behind.Low > 0.7f);
    }

    /// <summary>
    /// A repeating announcement is placed afresh between its firings: the budget re-applied the submission
    /// it started with and the placement stamp aged by the length of the line. Refresh replaces it, and
    /// never starts a play.
    /// </summary>
    [Fact]
    public void ARefreshPlacesAHeldPlayAndNeverStartsOne()
    {
        var mixer = new Sink();
        var voices = new VoiceManager(mixer, new AudioBank());
        var line = new SpatialEmitter { EntityId = 5, SoundId = "line", Mode = PlaybackMode.Single, Range = 400f, MinDistance = 12f, Volume = 1f, PositionSampledAt = 1.0 };
        voices.Submit(line);
        voices.Process(Vector3.Zero);
        Assert.Single(mixer.Started);
        var later = line; later.PositionSampledAt = 3.0;
        voices.Refresh(later);
        voices.Process(Vector3.Zero);
        Assert.Single(mixer.Started);
        Assert.Equal(3.0, mixer.Placed.Last().PositionSampledAt);
        // Ended: a refresh asks for nothing.
        mixer.Playing.Clear();
        voices.Process(Vector3.Zero);
        voices.Refresh(later);
        voices.Process(Vector3.Zero);
        Assert.Single(mixer.Started);
    }

    private sealed class Sink : IVoiceSink
    {
        public readonly HashSet<int> Playing = new();
        public readonly List<SpatialEmitter> Started = new();
        public readonly List<SpatialEmitter> Placed = new();
        public bool IsPlaying(int entityId) => Playing.Contains(entityId);
        public void PlayPhysicalSoundDirect(SpatialEmitter e) { Playing.Add(e.EntityId); Started.Add(e); }
        public void UpdateSpatialAttributes(SpatialEmitter e) => Placed.Add(e);
        public void StopSoundImmediate(int entityId) => Playing.Remove(entityId);
        public bool FadeOut(int entityId) { Playing.Remove(entityId); return true; }
        public void CancelFade(int entityId) { }
    }

    // ── Files ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A shipped recording, mono, and its rate.</summary>
    private static (float[] Pcm, int Rate) Program(string folder, string name)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, ".."));
        var bytes = File.ReadAllBytes(Path.Combine(root, "OpenFPS.Client", "ASSETS", "SOUNDS", folder, name + ".wav"));
        int rate = BitConverter.ToInt32(bytes, 24);
        var pcm = WeaponSynth.ReadWav16Mono(bytes);
        Assert.NotEmpty(pcm);
        return (pcm, rate);
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;
}
