using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Sound.Core.Rail;
using OpenFPS.Audio.Core;
using OpenFPS.Client.Core;
using OpenFPS.Materials;
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

    // ── A line along a path of its own (2026-10-17) ─────────────────────────────────────────────

    /// <summary>An arc of <paramref name="radius"/> m through <paramref name="degrees"/>, its middle at the origin, bending
    /// round toward +z, as a polyline of <paramref name="points"/> points.</summary>
    private static Vector3[] Arc(float radius, float degrees, int points)
    {
        var p = new Vector3[points];
        float span = degrees * MathF.PI / 180f;
        for (int i = 0; i < points; i++)
        {
            float a = -0.5f * span + span * i / (points - 1);
            p[i] = new Vector3(radius * MathF.Sin(a), 0f, radius - radius * MathF.Cos(a));
        }
        return p;
    }

    private static IEnumerable<LinePath> Paths()
    {
        yield return LinePath.Straight(Vector3.Zero, Vector3.UnitX, 20f);
        yield return new LinePath(Arc(20f, 120f, 25));
        yield return new LinePath(Arc(20f, 120f, 25), width: 6f);
        // A dog-leg with a climb in it, as a drainage line's cells run.
        yield return new LinePath(new[] { new Vector3(-30f, 1f, 0f), new Vector3(-5f, 0.5f, 0f), new Vector3(0f, 0.4f, 6f), new Vector3(25f, 0f, 8f) }, width: 2f);
    }

    /// <summary>A straight path is cut and placed as the straight line always was (ExtendedSources.View), to a
    /// millimetre and a hundredth of a decibel.</summary>
    [Fact]
    public void AStraightPathIsCutAndPlacedAsTheStraightLine()
    {
        const float L = 20f, Reference = 2f;
        var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, L);
        foreach (var ear in Ears())
        {
            var view = ExtendedSources.View(Vector3.Zero, Vector3.UnitX, L, ear);
            int n = ExtendedSources.WantedPieces(view, 0);
            Assert.Equal(n, ExtendedSources.WantedPieces(path, ear, 0));
            var a = ExtendedSources.Cut(view, n);
            var b = ExtendedSources.Cut(path, ear, n);
            for (int i = 0; i <= n; i++) Assert.True(MathF.Abs(a.Edges[i] - b.Edges[i]) < 1e-3f, $"edge {i} at {ear}: {a.Edges[i]} against {b.Edges[i]}");
            for (int i = 0; i < n; i++)
            {
                var pa = ExtendedSources.Place(view, a.Edges[i], a.Edges[i + 1], Reference);
                var pb = ExtendedSources.Place(path, ear, a.Edges[i], a.Edges[i + 1], Reference);
                Assert.True(MathF.Abs(pa.Along - pb.Along) < 1e-3f, $"piece {i} at {ear} along {pa.Along} against {pb.Along}");
                Assert.True(MathF.Abs(pa.Along - pb.At.X) < 1e-3f, $"piece {i} at {ear} placed at {pb.At}");
                Assert.True(Math.Abs(20 * Math.Log10(pa.Gain / pb.Gain)) < 0.01, $"piece {i} at {ear}: {pa.Gain} against {pb.Gain}");
            }
            Assert.Equal(ExtendedSources.MeanLawPower(view, Reference), ExtendedSources.MeanLawPower(path, ear, Reference), 4);
        }
    }

    /// <summary>
    /// A bent path, a wide bed, a dog-leg that climbs: at any ear the pieces cover it once end to end, none covers more
    /// than the split angle unless the cap binds, and together they are the whole path heard from every point of it
    /// (wide beds across themselves), within a hundredth of a decibel, however it is cut.
    /// </summary>
    [Fact]
    public void APathsPiecesCoverItOnceAndTogetherAreTheWholePath()
    {
        const float Reference = 2f;
        foreach (var path in Paths())
            foreach (var flat in Ears())
            {
                var ear = flat + new Vector3(0f, 0f, 3f);
                double whole = ExtendedSources.MeanLawPower(path, ear, Reference);
                int wanted = ExtendedSources.WantedPieces(path, ear, 0);
                foreach (int n in new[] { 1, 3, wanted, ExtendedSources.MaxPlaces })
                {
                    var cut = ExtendedSources.Cut(path, ear, n);
                    Assert.Equal(-path.HalfLength, cut.Edges[0]);
                    Assert.Equal(path.HalfLength, cut.Edges[^1]);
                    double sum = 0, share = 0;
                    for (int i = 0; i < cut.Count; i++)
                    {
                        Assert.True(cut.Edges[i + 1] >= cut.Edges[i], $"edges out of order at {ear}");
                        float covers = Covered(path, ear, cut.Edges[i], cut.Edges[i + 1]);
                        if (n == wanted && wanted < ExtendedSources.MaxPlaces)
                            Assert.True(covers <= TrainVoicing.SplitAboveDegrees + 0.05f, $"a piece covers {covers:F2}° at {ear}");
                        var p = ExtendedSources.Place(path, ear, cut.Edges[i], cut.Edges[i + 1], Reference);
                        float g = p.Gain * MathF.Min(1f, Reference / Vector3.Distance(ear, p.At));
                        int place = cut.PlaceOfPiece(i);
                        sum += cut.ShareOfPlace(place) * (double)g * g;
                        share += cut.ShareOfPlace(place);
                    }
                    Assert.Equal(1.0, share, 4);
                    Assert.True(Math.Abs(10 * Math.Log10(sum / whole)) < 0.01, $"{n} pieces of a {path.Length:F0} m path at {ear}: {10 * Math.Log10(sum / whole):F3} dB off");
                }
            }
    }

    /// <summary>The angle a stretch of a path covers at the ear, degrees, by fine steps along it.</summary>
    private static float Covered(LinePath path, Vector3 ear, float from, float to)
    {
        double sum = 0;
        var last = path.PointAt(from) - ear;
        for (int s = 1; s <= 400; s++)
        {
            var next = path.PointAt(from + (to - from) * s / 400f) - ear;
            sum += Math.Atan2(Vector3.Cross(last, next).Length(), Vector3.Dot(last, next));
            last = next;
        }
        return (float)(sum * 180.0 / Math.PI);
    }

    /// <summary>Tapping back and forth round a bent path never cuts it into a different number of pieces and back.</summary>
    [Fact]
    public void ATapNeverCutsABentPathBackAndForth()
    {
        var path = new LinePath(Arc(20f, 120f, 25));
        int flickers = 0, changed = 0;
        foreach (float r in new[] { 15f, 18.5f, 21.5f, 26f, 40f })
            for (float a = -80f; a <= 80f; a += 0.4f)
            {
                float rad = a * MathF.PI / 180f;
                var at = new Vector3(r * MathF.Sin(rad), 1.7f, 20f - r * MathF.Cos(rad));
                var step = new Vector3(MathF.Cos(rad), 0f, MathF.Sin(rad)) * 0.15f;
                int first = ExtendedSources.WantedPieces(path, at, 0);
                int n = ExtendedSources.WantedPieces(path, at + step, first);
                if (n != first) changed++;
                for (int tap = 0; tap < 6; tap++)
                {
                    int next = ExtendedSources.WantedPieces(path, at + (tap % 2 == 0 ? Vector3.Zero : step), n);
                    if (next != n) flickers++;
                    n = next;
                }
            }
        _o.WriteLine($"round a bend: the first tap changed the count {changed} times, a tap back and forth {flickers}");
        Assert.True(changed > 0, "no tap ever changed the count: the test asked nothing");
        Assert.Equal(0, flickers);
    }

    /// <summary>A creek laid along a bent line of its own, one source, as the server lays a drainage line or a mapped
    /// creek (SoundEmitterComponent.Line).</summary>
    private static ClientAudioHarness OnePathCreek(Vector3[] line, Vector3 position)
    {
        var h = new ClientAudioHarness();
        var def = new EntityDefinition
        {
            EntityId = 700,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "flow:creek";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 60f;
        def.SoundEmitter.Line = line.Select(p => p - position).ToArray();
        h.World.RegisterDefinition(def);
        return h;
    }

    /// <summary>
    /// Walking round the inside of a bend 1.5 m off a creek laid along it, one source, the level stays within ±1.5 dB of
    /// its mean and it comes from beside you all the way round: the cut follows the bend, not a chord across it.
    /// </summary>
    [Fact]
    public void RoundABendTheLevelIsFlatAndComesFromBeside()
    {
        const float R = 40f;
        var line = Arc(R, 100f, 41).Select(p => p + new Vector3(0f, 0.05f, 0f)).ToArray();
        var h = OnePathCreek(line, line[20]);
        float eye = LocalPlayerState.StandingEyeHeight;
        Vector3 Feet(float degrees)
        {
            float a = degrees * MathF.PI / 180f;
            return new Vector3((R - 1.5f) * MathF.Sin(a), 0f, R - (R - 1.5f) * MathF.Cos(a));
        }
        h.StandAt(Feet(-30f));
        h.Tick(240);
        var levels = new List<double>();
        int frames = (int)(0.5 * ClientAudioSystem.UpdateHz);
        float worstOff = 0f;
        for (float a = -30f; a <= 30f; a += 0.5f / R * 180f / MathF.PI)
        {
            for (int f = 1; f <= frames; f++) { h.StandAt(Feet(a)); h.Tick(); }
            var ear = Feet(a) + new Vector3(0f, eye, 0f);
            var heard = AudioWorld.HeardTogether(h.Mixer.Latest, h.Mixer.Live, h.Mixer.Pieces, id => id.StartsWith("flow:"), ear, h.Facade.Options.WideSources);
            levels.Add(10 * Math.Log10(heard.Power));
            // The water is outward of the walk, toward the bend's outside.
            float rad = a * MathF.PI / 180f;
            var outward = new Vector3(MathF.Sin(rad), 0f, -MathF.Cos(rad));
            float toward = MathF.Atan2(outward.X, outward.Z) * 180f / MathF.PI;
            float off = MathF.Abs(((heard.BearingDegrees - toward) % 360f + 540f) % 360f - 180f);
            worstOff = MathF.Max(worstOff, off);
        }
        double mean = 10 * Math.Log10(levels.Average(l => Math.Pow(10, l / 10)));
        _o.WriteLine($"round a {R:F0} m bend 1.5 m inside it: {levels.Min() - mean:F2} to {levels.Max() - mean:F2} dB about the mean; heard at most {worstOff:F0}° off beside");
        Assert.InRange(levels.Max() - mean, 0, 1.5);
        Assert.InRange(levels.Min() - mean, -1.5, 0);
        Assert.True(worstOff < 20f, $"heard {worstOff:F0}° off the water beside you");
    }

    /// <summary>
    /// A long line is heard from its part near you, not its middle: stood 2 m off one end of a 300 m creek, its middle
    /// 150 m away and past its 60 m range, it still wins a voice and is cut into pieces, and it is as loud as the law over
    /// the whole line says (ExtendedSources.MeanLawPower).
    /// </summary>
    [Fact]
    public void ALongLineIsHeardFromItsNearPart()
    {
        var line = new[] { new Vector3(-150f, 0.05f, 0f), new Vector3(150f, 0.05f, 0f) };
        var h = OnePathCreek(line, new Vector3(0f, 0.05f, 0f));
        var feet = new Vector3(140f, 0f, 2f);
        h.StandAt(feet);
        h.Tick(240);
        var ear = feet + new Vector3(0f, LocalPlayerState.StandingEyeHeight, 0f);
        var heard = AudioWorld.HeardTogether(h.Mixer.Latest, h.Mixer.Live, h.Mixer.Pieces, id => id.StartsWith("flow:"), ear, h.Facade.Options.WideSources);
        float atEnd = h.Audio.World.HeardInRanking(700);
        _o.WriteLine($"2 m off a 300 m creek, 10 m from its end: {heard.Voices} voices, {10 * Math.Log10(Math.Max(1e-30, heard.Power)):F1} dB; ranked at {atEnd:G3}");
        Assert.True(heard.Voices > 1, "the creek was not heard, or not cut");
        Assert.True(h.Mixer.Pieces.TryGetValue(700, out var cut) && cut.Count > 1);
        // Ranked as what is beside you: within the 3 dB a line's far side adds, as loud as stood beside its middle.
        h.StandAt(new Vector3(0f, 0f, 2f));
        h.Tick(60);
        float atMiddle = h.Audio.World.HeardInRanking(700);
        _o.WriteLine($"  ranked at {atMiddle:G3} beside its middle: {20 * Math.Log10(atEnd / atMiddle):F1} dB");
        Assert.True(atEnd > 0f && Math.Abs(20 * Math.Log10(atEnd / atMiddle)) < 4.0, $"ranked {atEnd:G3} by its end, {atMiddle:G3} by its middle");
    }

    /// <summary>
    /// A long line's path is asked about from its point nearest the ear: stood beside one end of a 300 m creek with a
    /// brick wall between you and its middle, 120 m off, the creek beside you is not heard through the wall.
    /// </summary>
    [Fact]
    public void ALongLineIsAskedAboutFromItsNearPart()
    {
        var line = new[] { new Vector3(-150f, 0.05f, 0f), new Vector3(150f, 0.05f, 0f) };
        var h = OnePathCreek(line, new Vector3(0f, 0.05f, 0f));
        h.AddWall(800, new Vector3(120f, 2.5f, 1f), new Vector3(0.5f, 5f, 30f));
        h.StandAt(new Vector3(140f, 0f, 2f));
        bool answered = h.TickUntil(() => false, 240, TimeSpan.FromSeconds(2));
        Assert.False(answered);
        Assert.True(h.Mixer.Latest.TryGetValue(700, out var e), "the creek has no voice");
        _o.WriteLine($"beside its end, a wall toward its middle: occlusion {e.Occlusion:F2}, high band {e.EqHigh:F2}");
        Assert.True(e.Occlusion < 0.2f && e.EqHigh > 0.5f, $"heard through the wall: occlusion {e.Occlusion:F2}, high band {e.EqHigh:F2}");
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
        var heard = AudioWorld.HeardTogether(h.Mixer.Latest, h.Mixer.Live, h.Mixer.Pieces, id => id.StartsWith("flow:"), ear, h.Facade.Options.WideSources);
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
