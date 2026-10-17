using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A source is heard from the whole of itself (Cody, 2026-10-16: "a river should sound like a flowing river the
/// entire width and length of the river"). A line of running water is cut at the listener into pieces by the angle
/// each covers (ExtendedSources.Cut), each piece as loud as what it holds by the mixer's law and playing only the
/// events that happen in it; walking beside a uniform line, the level stays flat and the sound widens as you come
/// up to it (docs/RUNNING_WATER.md 15). Before, every 20 m stretch was held to the power of its middle and the
/// level rose and fell 10.4 dB every 20 m beside a creek.
/// </summary>
public class ExtentWalkTests
{
    private readonly ITestOutputHelper _o;
    public ExtentWalkTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    // ── The cut ─────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<Vector3> Ears()
    {
        foreach (float off in new[] { 0.3f, 1.5f, 5f, 20f, 80f, 400f })
            foreach (float along in new[] { -30f, -10f, -2f, 0f, 3f, 9.9f, 25f })
                yield return new Vector3(along, 1.7f, off);
    }

    /// <summary>At any ear the pieces cover the line once, end to end; none covers more than the split angle
    /// unless the most a source may have (twelve) binds, when they are equal; and the places and pieces are
    /// one to one, the source's own voice playing the piece that holds its middle.</summary>
    [Fact]
    public void ThePiecesCoverTheLineOnceAndNoneIsWiderThanTheSplitAngle()
    {
        const float L = 20f;
        foreach (var ear in Ears())
        {
            var view = ExtendedSources.View(Vector3.Zero, Vector3.UnitX, L, ear);
            int n = ExtendedSources.WantedPieces(view, 0);
            var cut = ExtendedSources.Cut(view, n);
            Assert.Equal(n, cut.Count);
            Assert.Equal(-0.5f * L, cut.Edges[0]);
            Assert.Equal(0.5f * L, cut.Edges[^1]);
            float share = 0f;
            var places = new HashSet<int>();
            for (int i = 0; i < cut.Count; i++)
            {
                Assert.True(cut.Edges[i + 1] >= cut.Edges[i], $"edges out of order at {ear}");
                float covers = (view.AngleOf(cut.Edges[i + 1] - view.Foot) - view.AngleOf(cut.Edges[i] - view.Foot)) * 180f / MathF.PI;
                if (n < ExtendedSources.MaxPlaces) Assert.True(covers <= TrainVoicing.SplitAboveDegrees + 1e-3f, $"a piece covers {covers:F1}° at {ear}");
                else Assert.Equal(view.SubtendedDegrees / n, covers, 2);
                int place = cut.PlaceOfPiece(i);
                Assert.True(places.Add(place));
                Assert.Equal(i, cut.PieceOfPlace(place));
                Assert.Equal(place, cut.PlaceAt(0.5f * (cut.Edges[i] + cut.Edges[i + 1])));
                share += cut.ShareOfPlace(place);
            }
            Assert.Equal(1f, share, 4);
            Assert.Equal(0, cut.PlaceAt(0f));
        }
    }

    /// <summary>
    /// The pieces together are the line heard from every point of it by the mixer's law, however it is cut: each
    /// piece's power (its share of the line) at its own place and gain sums to the law's mean over the line, and far
    /// off that is the middle heard alone. One piece or twelve, beside it or at its end.
    /// </summary>
    [Fact]
    public void ThePiecesTogetherAreTheWholeLineHowEverItIsCut()
    {
        const float L = 20f, Reference = 2f, Range = 100000f;
        foreach (var ear in Ears())
        {
            var view = ExtendedSources.View(Vector3.Zero, Vector3.UnitX, L, ear);
            double whole = ExtendedSources.MeanLawPower(view, Reference);
            foreach (int n in new[] { 1, 2, 5, ExtendedSources.WantedPieces(view, 0), ExtendedSources.MaxPlaces })
            {
                var cut = ExtendedSources.Cut(view, n);
                double sum = 0;
                for (int k = 0; k < cut.Count; k++)
                {
                    int i = cut.PieceOfPlace(k);
                    var p = ExtendedSources.Place(view, cut.Edges[i], cut.Edges[i + 1], Reference);
                    Assert.InRange(p.Along, cut.Edges[i] - 1e-3f, cut.Edges[i + 1] + 1e-3f);
                    float d = Vector3.Distance(ear, new Vector3(p.Along, 0f, 0f));
                    // The edge fade is the mixer's too, and a range this long has none here.
                    float g = p.Gain * MathF.Min(1f, Reference / d);
                    sum += cut.ShareOfPlace(k) * (double)g * g;
                }
                Assert.True(Math.Abs(10 * Math.Log10(sum / whole)) < 0.01, $"{n} pieces at {ear}: {10 * Math.Log10(sum / whole):F3} dB off the whole line");
            }
            float middle = Loudness.RenderedGain(1f, Reference, Range, ear.Length());
            if (ear.Length() > 5f * L)
                Assert.InRange(10 * Math.Log10(whole / (middle * (double)middle)), -0.2, 0.2);
        }
    }

    /// <summary>A tap of a movement key moves you 15 cm (memory "cody-taps-movement-keys"). Tapping back and forth
    /// anywhere round a line, a tap there and a tap back never cut it into a different number of pieces and back
    /// again: wherever the count changes, the way back is held by the band between the merge and split angles.</summary>
    [Fact]
    public void ATapOfAMovementKeyNeverCutsItBackAndForth()
    {
        const float L = 20f;
        int places = 0, flickers = 0, changed = 0;
        foreach (float off in new[] { 0.5f, 1.5f, 4f, 12f, 40f })
            for (float along = -45f; along <= 45f; along += 0.05f)
            {
                places++;
                var at = new Vector3(along, 1.7f, off);
                int first = ExtendedSources.WantedPieces(ExtendedSources.View(Vector3.Zero, Vector3.UnitX, L, at), 0);
                // A tap ahead: perhaps a new count. Then back, ahead and back again: it must hold whatever it took.
                int ahead = ExtendedSources.WantedPieces(ExtendedSources.View(Vector3.Zero, Vector3.UnitX, L, at + new Vector3(0.15f, 0f, 0f)), first);
                if (ahead != first) changed++;
                int n = ahead;
                for (int tap = 0; tap < 6; tap++)
                {
                    var ear = at + new Vector3(tap % 2 == 0 ? 0f : 0.15f, 0f, 0f);
                    int next = ExtendedSources.WantedPieces(ExtendedSources.View(Vector3.Zero, Vector3.UnitX, L, ear), n);
                    if (next != n) flickers++;
                    n = next;
                }
            }
        _o.WriteLine($"{places} places tapped round: the first tap changed the count at {changed}, a tap back and forth at {flickers}");
        Assert.True(changed > 0, "no tap ever changed the count: the test asked nothing");
        Assert.Equal(0, flickers);
    }

    // ── The synth: each event in its own piece ──────────────────────────────────────────────────

    /// <summary>
    /// Cut into pieces, the creek's synth writes each breaking site's events into the place that plays the piece
    /// the site is in: a piece holding no site is silent, the places' streams are independent (the largest
    /// correlation between two under 0.05, as the fire's places), and together they are the creek at one point.
    /// </summary>
    [Fact]
    public void ACutCreekPlaysEachEventInItsOwnPiece()
    {
        var spec = RunningWaterSpec.Creek;
        var view = ExtendedSources.View(Vector3.Zero, Vector3.UnitX, spec.LengthMetres, new Vector3(-3f, 1.7f, 1.5f));
        var cut = ExtendedSources.Cut(view, ExtendedSources.MaxPlaces);
        var synth = new RunningWaterSynth(spec, Rate, 5, ExtendedSources.MaxPlaces) { Flow = spec.BaseFlowLitresPerSecond, Cut = cut };
        var one = new RunningWaterSynth(spec, Rate, 5) { Flow = spec.BaseFlowLitresPerSecond };
        int seconds = 8, n = ExtendedSources.MaxPlaces;
        var x = new float[n][];
        for (int k = 0; k < n; k++) x[k] = new float[Rate * seconds];
        var buf = new float[n];
        double alone = 0;
        for (int i = 0; i < Rate * (seconds + 1); i++)
        {
            if (i % 256 == 0) { synth.Control(256f / Rate); one.Control(256f / Rate); }
            synth.NextPlaces(buf);
            float o = one.Next();
            if (i < Rate) continue;
            for (int k = 0; k < n; k++) x[k][i - Rate] = buf[k];
            alone += o * (double)o;
        }
        double together = x.Sum(p => p.Sum(v => (double)v * v));
        double Db(double e) => 10 * Math.Log10(Math.Max(1e-30, e / (Rate * (double)seconds)) / 4e-10);
        _o.WriteLine($"creek cut in {n}: together {Db(together):F1} dB, at one point {Db(alone):F1} dB");
        Assert.InRange(Db(together) - Db(alone), -1.5, 1.5);
        double worst = 0;
        for (int a = 0; a < n; a++)
        {
            double ea = x[a].Sum(v => (double)v * v);
            int i = cut.PieceOfPlace(a);
            _o.WriteLine($"  place {a,2}: piece {i,2} from {cut.Edges[i],6:F2} to {cut.Edges[i + 1],6:F2} m, {Db(ea),5:F1} dB");
            for (int b = a + 1; b < n; b++)
            {
                double eb = x[b].Sum(v => (double)v * v), ab = 0;
                if (ea <= 0 || eb <= 0) continue;
                for (int s = 0; s < x[a].Length; s++) ab += x[a][s] * (double)x[b][s];
                worst = Math.Max(worst, Math.Abs(ab) / Math.Sqrt(ea * eb));
            }
        }
        _o.WriteLine($"largest correlation between two places {worst:F3}");
        Assert.True(worst < 0.05, $"two pieces correlate {worst:F3}");
        // Each plays what is in it: the longest piece, metres of stones, well over the shortest, beside the ear.
        int longest = Enumerable.Range(0, n).MaxBy(cut.ShareOfPlace), shortest = Enumerable.Range(0, n).MinBy(cut.ShareOfPlace);
        Assert.True(Db(x[longest].Sum(v => (double)v * v)) - Db(x[shortest].Sum(v => (double)v * v)) > 3.0,
                    $"the longest piece ({cut.ShareOfPlace(longest):P0}) is not louder than the shortest ({cut.ShareOfPlace(shortest):P0})");
    }

    // ── The walk ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the mixer would make of every voice of the walk's sources at the ear: each voice's share of its source
    /// (its piece's share of the line, or its place's share at its middle's spread) at its own place, gain and
    /// reference, by Loudness.RenderedGain. The same sum as the lab's --extent-walk.
    /// </summary>
    private static double Heard(ClientAudioHarness h, Vector3 ear, out float widthDegrees, out int voices)
    {
        var heard = ClientAudioSystem.HeardTogether(h.Mixer.Latest, h.Mixer.Live, h.Mixer.Pieces, id => id.StartsWith("flow:"), ear, h.Facade.Options.WideSources);
        widthDegrees = heard.WidthDegrees;
        voices = heard.Voices;
        return heard.Power;
    }

    /// <summary>A line of creek laid as the server lays a drainage line: stretches end to end along x, each a
    /// voice of its own.</summary>
    private static ClientAudioHarness CreekLine(int stretches, out float length)
    {
        var h = new ClientAudioHarness();
        float each = RunningWaterSpec.Creek.LengthMetres;
        length = stretches * each;
        for (int i = 0; i < stretches; i++)
        {
            var def = new EntityDefinition
            {
                EntityId = 500 + i,
                Type = EntityType.StaticObject,
                Transform = new Transform { Position = new Vector3((i + 0.5f) * each - 0.5f * length, 0.05f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = "flow:creek";
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.MinDistance = 1f;
            def.SoundEmitter.Range = 60f;
            h.World.RegisterDefinition(def);
        }
        return h;
    }

    /// <summary>
    /// Walking at a steady 1 m/s beside a line of creek 1.5 m off it, the level stays within ±1.5 dB of its mean
    /// (before 2026-10-16 it rose and fell about 10 dB a stretch, each stretch held to its middle); and walking up
    /// to it from 40 m, it widens all the way and grows as a line does, about 3 dB for each halving near it.
    /// </summary>
    [Fact]
    public void BesideAUniformLineTheLevelIsFlatAndItWidensAsYouComeUpToIt()
    {
        var h = CreekLine(12, out float length);
        float eye = LocalPlayerState.StandingEyeHeight;
        var start = new Vector3(-40f, 0f, 1.5f);
        h.StandAt(start);
        h.Player.Yaw = MathF.PI / 2f;
        h.Player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, h.Player.Yaw);
        h.Tick(240);
        int frames = (int)(0.5 * ClientAudioSystem.UpdateHz);
        var levels = new List<double>();
        int most = 0;
        for (float x = -40f; x <= 40f; x += 0.5f)
        {
            for (int f = 1; f <= frames; f++) { h.StandAt(new Vector3(x - 0.5f + 0.5f * f / frames, 0f, 1.5f)); h.Tick(); }
            levels.Add(10 * Math.Log10(Heard(h, new Vector3(x, eye, 1.5f), out _, out int v)));
            most = Math.Max(most, v);
        }
        double mean = 10 * Math.Log10(levels.Average(l => Math.Pow(10, l / 10)));
        _o.WriteLine($"beside {length:F0} m of creek, 1.5 m off: {levels.Min() - mean:F2} to {levels.Max() - mean:F2} dB about the mean, {most} voices at most");
        for (int i = 0; i < levels.Count; i += 8) _o.WriteLine($"  {-40f + 0.5f * i,6:F1} m: {levels[i] - mean,6:F2} dB");
        Assert.InRange(levels.Max() - mean, 0, 1.5);
        Assert.InRange(levels.Min() - mean, -1.5, 0);

        // Out from it, facing along it, then walked straight up to it.
        var out_ = new List<(float Off, double Db, float Width)>();
        h.StandAt(new Vector3(0f, 0f, 40f));
        h.Tick(240);
        for (float off = 40f; off >= 1.5f - 1e-3f; off -= 0.5f)
        {
            for (int f = 1; f <= frames; f++) { h.StandAt(new Vector3(0f, 0f, off + 0.5f - 0.5f * f / frames)); h.Tick(); }
            double db = 10 * Math.Log10(Heard(h, new Vector3(0f, eye, off), out float width, out _));
            out_.Add((off, db, width));
        }
        foreach (var (off, db, width) in out_.Where((_, i) => i % 8 == 0 || i == out_.Count - 1))
            _o.WriteLine($"  {off,5:F1} m off: {db - out_[0].Db,6:F2} dB, {width,5:F1}° wide");
        float WidthAt(float off) => out_.First(o => MathF.Abs(o.Off - off) < 0.26f).Width;
        double DbAt(float off) => out_.First(o => MathF.Abs(o.Off - off) < 0.26f).Db;
        // A long line near you fills nearly half the world either way, so the last few metres add little: never
        // narrower by more than a couple of degrees, and much wider at its bank than 40 m off.
        for (int i = 1; i < out_.Count; i++)
            Assert.True(out_[i].Width >= out_[i - 1].Width - 2f, $"narrower coming up: {out_[i - 1].Width:F0}° at {out_[i - 1].Off} m, {out_[i].Width:F0}° at {out_[i].Off} m");
        Assert.True(WidthAt(1.5f) - WidthAt(40f) > 15f, $"widths {WidthAt(1.5f):F0}° at its bank, {WidthAt(40f):F0}° 40 m off");
        // From 2.5 m to 10 m off (the ear 1.7 m over the water): two and a little halvings of the distance.
        double h1 = Math.Sqrt(2.5 * 2.5 + 1.65 * 1.65), h2 = Math.Sqrt(10 * 10 + 1.65 * 1.65);
        double perHalving = (DbAt(2.5f) - DbAt(10f)) / Math.Log2(h2 / h1);
        _o.WriteLine($"  from 2.5 m to 10 m off, {perHalving:F2} dB for each halving of the distance");
        Assert.InRange(perHalving, 2.0, 4.5);
    }
}
