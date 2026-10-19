using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Audio.Core;
using OpenFPS.Client.Core;
using OpenFPS.Models;
using OpenFPS.Materials;
using OpenFPS.Environment;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A fire heard from its whole burning area and a wood from its crowns (2026-10-17, docs/FIRE.md 12.13): each cut at the
/// listener into pieces by the angle each covers, as running water and open water are (docs/RUNNING_WATER.md 15), each
/// piece as loud as what it holds by the mixer's law and playing only what happens in it: a fire's bodies of fire where
/// their ground is, crackle at the bed and roar in the flames over it; a wood's crowns where they stand.
/// </summary>
public class FireAndWoodCutTests
{
    private readonly ITestOutputHelper _o;
    public FireAndWoodCutTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;
    private static readonly OpenFPS.Sound.Hearing.HearingSettings Law = new();

    private static double Db(double ratio) => 10 * Math.Log10(Math.Max(1e-30, ratio));

    /// <summary>The law's power at a distance, flat inside the reference: Loudness.RenderedGain squared without its edge fade.</summary>
    private static double LawAt(float d, float reference) => d <= reference ? 1.0 : reference * (double)reference / (d * (double)d);

    /// <summary>A fire's pieces at an ear, as the client asks for them: the angle's, its flames over them, within a source's voices.</summary>
    private static ExtendedSources.LineCut CutFire(FireSpec spec, Vector3 at, Quaternion turn, Vector3 ear, float reference, int previous = 0)
    {
        var outline = ExtendedSources.FireOutline(spec.Outline, at, turn);
        int n = ExtendedSources.WantedAreaPieces(outline, ear, previous);
        if (n + ExtendedSources.FlamesFor(n) > ExtendedSources.MaxPlaces) n = ExtendedSources.BedPiecesFor(ExtendedSources.MaxPlaces);
        return ExtendedSources.CutArea(outline, ear, n, reference, true, ExtendedSources.FlamesFor(n), spec.RoarRiseMetres, at, turn);
    }

    private static IEnumerable<(string Name, FireSpec Spec, Vector3 At, Quaternion Turn)> Fires()
    {
        yield return ("fire pit", ModelLibrary.Presets.Fire("fire_pit/shape=c0.9"), new Vector3(3f, 0.4f, -2f), Quaternion.Identity);
        yield return ("burning car", FireSpec.BurningCar, new Vector3(0f, 0.7f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));
        yield return ("stand of trees", FireSpec.BurningTrees, new Vector3(-10f, 4.5f, 20f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f));
    }

    // ── A fire's burning area ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// At any ear, beside a fire, round it, over it or far off, its pieces cover its bed once, none wider than the split
    /// angle unless a source's twelve voices bind, and together they are the whole burning area heard by the mixer's law
    /// (each its share of the area at its own place and gain), within a tenth of a decibel; the flames over them are the
    /// same area that far above it, each over a run of the pieces.
    /// </summary>
    [Fact]
    public void AFiresPiecesTogetherAreItsWholeBurningArea()
    {
        foreach (var (name, spec, at, turn) in Fires())
        {
            var (_, reference) = Law.Place(spec.SourceLevelDb, spec.ExtentMetres);
            var outline = ExtendedSources.FireOutline(spec.Outline, at, turn);
            var flames = ExtendedSources.FireOutline(spec.Outline, at + new Vector3(0f, spec.RoarRiseMetres, 0f), turn);
            foreach (float d in new[] { 0f, 1.5f, 4f, 12f, 40f, 150f })
                foreach (float bearing in new[] { 0.2f, 2.0f, 4.1f })
                {
                    var ear = at with { Y = 1.7f } + new Vector3(MathF.Cos(bearing), 0f, MathF.Sin(bearing)) * d;
                    var cut = CutFire(spec, at, turn, ear, reference);
                    Assert.True(cut.Places <= ExtendedSources.MaxPlaces, $"{name}: {cut.Places} voices");
                    float shares = Enumerable.Range(0, cut.Places).Sum(cut.ShareOfPlace);
                    Assert.Equal(1f, shares, 3);
                    double bed = 0, above = 0;
                    for (int p = 0; p < cut.Places; p++)
                    {
                        var place = ExtendedSources.PlaceOfArea(cut, ear, p, reference);
                        double heard = cut.ShareOfPlace(p) * place.Gain * (double)place.Gain * LawAt(Vector3.Distance(place.At, ear), reference);
                        if (p < cut.Count) bed += heard;
                        else
                        {
                            above += heard;
                            Assert.Equal(at.Y + spec.RoarRiseMetres, place.At.Y, 3);
                        }
                    }
                    float flameShare = cut.Area!.Flames > 0 ? ExtendedSources.HeardFlameShare : 0f;
                    double wholeBed = (1 - flameShare) * ExtendedSources.AreaMeanLawPower(outline, ear, reference);
                    Assert.True(Math.Abs(Db(bed / wholeBed)) < 0.1, $"{name} {d} m off: the bed's pieces {Db(bed / wholeBed):F3} dB from the whole bed");
                    if (flameShare > 0f)
                    {
                        double wholeFlames = flameShare * ExtendedSources.AreaMeanLawPower(flames, ear, reference);
                        Assert.True(Math.Abs(Db(above / wholeFlames)) < 0.1, $"{name} {d} m off: the flames {Db(above / wholeFlames):F3} dB from the whole");
                    }
                    double covered = (cut.Edges[^1] - cut.Edges[0]) * 180.0 / Math.PI;
                    if (cut.Count < ExtendedSources.BedPiecesFor(ExtendedSources.MaxPlaces))
                        Assert.True(covered / cut.Count <= OpenFPS.Sound.Core.Rail.TrainVoicing.SplitAboveDegrees + 0.01,
                                    $"{name} {d} m off: {cut.Count} pieces over {covered:F1}°");
                    if (bearing == 0.2f) _o.WriteLine($"{name}, {d,5:F1} m off: {cut.Count} pieces and {cut.Area.Flames} flames over {covered,5:F1}°");
                }
        }
    }

    /// <summary>Coming up to a burning car in taps of a movement key (15 cm) its pieces only part, and going away only merge:
    /// the band between the split and merge angles holds a cut against a tap back and forth.</summary>
    [Fact]
    public void ComingUpToAFireItsPiecesOnlyPartAndGoingAwayOnlyMerge()
    {
        var spec = FireSpec.BurningCar;
        var at = new Vector3(0f, 0.7f, 0f);
        var outline = ExtendedSources.FireOutline(spec.Outline, at, Quaternion.Identity);
        int n = 0, most = 0;
        for (float d = 40f; d >= 2f; d -= 0.15f)
        {
            int next = ExtendedSources.WantedAreaPieces(outline, new Vector3(d + 0.9f, 1.7f, 0.3f), n);
            Assert.True(next >= n, $"at {d:F2} m it merged from {n} to {next} coming up");
            n = next;
            most = Math.Max(most, n);
        }
        for (float d = 2f; d <= 40f; d += 0.15f)
        {
            int next = ExtendedSources.WantedAreaPieces(outline, new Vector3(d + 0.9f, 1.7f, 0.3f), n);
            Assert.True(next <= n, $"at {d:F2} m it parted from {n} to {next} going away");
            n = next;
        }
        _o.WriteLine($"a burning car: {most} pieces at its side, {n} at 40 m");
        // At 40 m it covers 6.4°: one piece wanted, up to three held from nearer.
        Assert.True(most >= 8 && n <= 3);
    }

    private static float[][] Render(int places, int seconds, Action<Span<float>> next, Action control, int settle)
    {
        var x = new float[places][];
        for (int k = 0; k < places; k++) x[k] = new float[Rate * seconds];
        var buf = new float[places];
        for (int i = 0; i < Rate * (seconds + settle); i++)
        {
            if (i % 256 == 0) control();
            next(buf);
            if (i < Rate * settle) continue;
            for (int k = 0; k < places; k++) x[k][i - Rate * settle] = buf[k];
        }
        return x;
    }

    private static double Power(float[] x) => x.Sum(v => (double)v * v) / x.Length;

    /// <summary>A band's power: two one-pole high-passes at <paramref name="above"/> and two low-passes at <paramref name="below"/>.</summary>
    private static double BandPower(float[] x, float above, float below)
    {
        float ah = MathF.Exp(-MathF.Tau * above / Rate), al = MathF.Exp(-MathF.Tau * below / Rate);
        float h1 = 0, h2 = 0, x1 = 0, y1 = 0, l1 = 0, l2 = 0;
        double e = 0;
        foreach (float v in x)
        {
            // High-pass twice, then low-pass twice.
            h1 = ah * (h1 + v - x1); x1 = v;
            h2 = ah * (h2 + h1 - y1); y1 = h1;
            l1 = (1 - al) * h2 + al * l1;
            l2 = (1 - al) * l1 + al * l2;
            e += l2 * (double)l2;
        }
        return e / x.Length;
    }

    /// <summary>
    /// A house fire cut at the ear beside it: every voice of the cut is its own stream (no two correlate), the voices the
    /// cut does not use are silent, the bed's voices carry its crackle and the flames' its roar (the flames' under 200 Hz
    /// ten times the bed's, their crackle band a hundredth), and together they are as loud as the fire heard from one point.
    /// </summary>
    [Fact]
    public void AFireCutAtTheEarPlaysItsCrackleAtTheBedAndItsRoarInTheFlames()
    {
        var spec = FireSpec.HouseFire;
        var (_, reference) = Law.Place(spec.SourceLevelDb, spec.ExtentMetres);
        var at = new Vector3(0f, 1f, 0f);
        var cut = CutFire(spec, at, Quaternion.Identity, new Vector3(-8f, 1.7f, 1f), reference);
        Assert.True(cut.Area!.Flames > 0);
        int n = ExtendedSources.MaxPlaces;
        var fire = new FireSynth(spec, Rate, 9, n) { Wind = 2f, FallPart = 0f, Cut = cut };
        var one = new FireSynth(spec, Rate, 9) { Wind = 2f, FallPart = 0f };
        double alone = 0;
        int seconds = 6;
        var x = Render(n, seconds, s => { fire.NextPlaces(s); float o = one.Next(); alone += o * (double)o; },
                       () => { fire.Control(256f / Rate); one.Control(256f / Rate); }, settle: 2);
        alone /= Rate * (seconds + 2.0);
        double together = x.Sum(Power);
        _o.WriteLine($"a house fire beside it, {cut.Count} pieces and {cut.Area.Flames} flames: together {Db(together / alone):F2} dB against one point");
        Assert.InRange(Db(together / alone), -1.5, 1.5);
        for (int p = cut.Places; p < n; p++) Assert.All(x[p], v => Assert.Equal(0f, v));
        double bedLow = 0, bedHigh = 0;
        for (int p = 0; p < cut.Count; p++) { bedLow += BandPower(x[p], 20f, 200f); bedHigh += BandPower(x[p], 2000f, 8000f); }
        bedLow /= cut.Count;
        bedHigh /= cut.Count;
        for (int p = cut.Count; p < cut.Places; p++)
        {
            double low = BandPower(x[p], 20f, 200f), high = BandPower(x[p], 2000f, 8000f);
            _o.WriteLine($"  flame {p - cut.Count}: under 200 Hz {Db(low / bedLow):F1} dB over a bed piece's, 2-8 kHz {Db(high / bedHigh):F1} dB");
            Assert.True(low > 100 * bedLow, $"flame {p - cut.Count} carries no more roar than the bed");
            // The roar's own tail reaches the top octaves of a house fire; the crackle is the bed's.
            Assert.True(high < bedHigh, $"flame {p - cut.Count} carries the crackle");
        }
        double worst = 0;
        for (int a = 0; a < cut.Places; a++)
            for (int b = a + 1; b < cut.Places; b++)
            {
                double ab = 0, ea = 0, eb = 0;
                for (int s = 0; s < x[a].Length; s++) { ab += x[a][s] * (double)x[b][s]; ea += x[a][s] * (double)x[a][s]; eb += x[b][s] * (double)x[b][s]; }
                if (ea > 0 && eb > 0) worst = Math.Max(worst, Math.Abs(ab) / Math.Sqrt(ea * eb));
            }
        _o.WriteLine($"  largest correlation between two voices {worst:F3}");
        Assert.True(worst < 0.05, $"two voices correlate {worst:F3}");
    }

    /// <summary>
    /// Cut again from the other side of the fire pit, each place's share of its one body of fire glides to the new cut over
    /// a quarter second (never more than a control block's step of it), and is there within a per cent after a second and a
    /// half: a recut is the ear moving, and nothing jumps from one voice to another.
    /// </summary>
    [Fact]
    public void CutAgainAFiresSharesGlideAndNeverJump()
    {
        var spec = ModelLibrary.Presets.Fire("fire_pit/shape=c0.9");
        var (_, reference) = Law.Place(spec.SourceLevelDb, spec.ExtentMetres);
        var at = new Vector3(0f, 0.4f, 0f);
        var west = CutFire(spec, at, Quaternion.Identity, new Vector3(-1.5f, 1.7f, 0f), reference);
        var north = CutFire(spec, at, Quaternion.Identity, new Vector3(0.3f, 1.7f, 1.4f), reference);
        int n = ExtendedSources.MaxPlaces;
        var fire = new FireSynth(spec, Rate, 4, n) { Wind = 1f, Cut = west };
        float dt = 256f / Rate;
        for (int i = 0; i < 400; i++) fire.Control(dt);
        var last = Enumerable.Range(0, n).Select(p => fire.ShareOf(0, p)).ToArray();
        fire.Cut = north;
        float worst = 0f;
        for (int i = 0; i < (int)(1.5f / dt); i++)
        {
            fire.Control(dt);
            for (int p = 0; p < n; p++)
            {
                float now = fire.ShareOf(0, p);
                worst = MathF.Max(worst, MathF.Abs(now - last[p]));
                last[p] = now;
            }
        }
        _o.WriteLine($"the pit cut again from the north ({west.Count} pieces to {north.Count}): its share moved at most {worst:P2} of it a control block");
        Assert.True(worst <= dt / FireSynth.RecutGlideSeconds + 1e-4f, $"a share jumped {worst:P1} in one block");
        // There: each place its piece's share of the bed, the pit being one body over all of it (the bed's area by bearing
        // from the ear, on a fine grid).
        var truth = new double[north.Count];
        int inside = 0;
        for (int i = 0; i < 300; i++)
            for (int j = 0; j < 300; j++)
            {
                float x = -0.45f + 0.9f * (i + 0.5f) / 300f, z = -0.45f + 0.9f * (j + 0.5f) / 300f;
                if (x * x + z * z > 0.45f * 0.45f) continue;
                int piece = north.Area!.PieceOf(new Vector2(at.X + x, at.Z + z), north.Edges);
                truth[north.PlaceOfPiece(piece)]++;
                inside++;
            }
        for (int p = 0; p < north.Count; p++)
            Assert.InRange(fire.ShareOf(0, p) / (truth[p] / inside), 0.9, 1.1);
    }

    // ── A wood's crowns ─────────────────────────────────────────────────────────────────────────

    /// <summary>Thirty park trees over 160 by 120 m, crowns at 8 m, each with a share of itself in the wood (some none).</summary>
    private static (Vector3[] At, float[] Share) Crowns(int seed = 3)
    {
        var rng = new Random(seed);
        var at = new Vector3[30];
        var share = new float[30];
        for (int i = 0; i < at.Length; i++)
        {
            at[i] = new Vector3((float)(rng.NextDouble() - 0.5) * 160f, 8f, (float)(rng.NextDouble() - 0.5) * 120f);
            share[i] = i % 7 == 3 ? 0f : 0.3f + 0.7f * (float)rng.NextDouble();
        }
        return (at, share);
    }

    /// <summary>
    /// A wood cut at the ear, from far off, at its edge or among its trees: every crown with a share is in one piece and
    /// every piece holds one or more, and the pieces together are its crowns each heard from where it is by the mixer's law,
    /// exactly (a crown is a point at the wood's distances).
    /// </summary>
    [Fact]
    public void AWoodsPiecesTogetherAreItsCrownsEachHeardFromWhereItIs()
    {
        var (at, share) = Crowns();
        var tree = FoliageSpec.ParkTree;
        var (_, reference) = Law.Place(tree.SourceLevelDb, tree.ExtentMetres);
        foreach (var ear in new[] { new Vector3(0f, 1.7f, -300f), new Vector3(10f, 1.7f, -70f), new Vector3(5f, 1.7f, 8f), new Vector3(-250f, 1.7f, 230f) })
        {
            int n = ExtendedSources.WantedPointPieces(at, share, ear, 0);
            var cut = ExtendedSources.CutPoints(at, share, ear, n, reference);
            var area = cut.Area!;
            Assert.True(area.Discrete);
            Assert.Equal(share.Count(s => s > 0f), area.Samples.Length);
            for (int i = 0; i < cut.Count; i++) Assert.True(area.SampleStart[i + 1] > area.SampleStart[i], $"piece {i} holds no crown");
            double heard = 0;
            for (int p = 0; p < cut.Places; p++)
            {
                var place = ExtendedSources.PlaceOfArea(cut, ear, p, reference);
                heard += cut.ShareOfPlace(p) * place.Gain * (double)place.Gain * LawAt(Vector3.Distance(place.At, ear), reference);
            }
            double whole = ExtendedSources.PointsMeanLawPower(at, share, ear, reference);
            _o.WriteLine($"from {ear}: {n} wanted, {cut.Count} pieces over {ExtendedSources.PointsDegrees(at, share, ear):F0}°, {Db(heard / whole):F4} dB from its crowns");
            Assert.True(Math.Abs(Db(heard / whole)) < 0.01);
        }
    }

    /// <summary>
    /// A wood of nine trees cut at the ear: each piece plays its crowns' share of the wood (its power within a decibel and
    /// a half of its share), no two pieces correlate, the voices the cut does not use are silent, and together it is as
    /// loud as the wood from one point.
    /// </summary>
    [Fact]
    public void AWoodCutAtTheEarPlaysEachPieceItsCrownsShare()
    {
        var (at, share) = Crowns(5);
        var tree = FoliageSpec.ParkTree;
        var (_, reference) = Law.Place(tree.SourceLevelDb, tree.ExtentMetres);
        var ear = new Vector3(0f, 1.7f, -90f);
        var cut = ExtendedSources.CutPoints(at, share, ear, ExtendedSources.WantedPointPieces(at, share, ear, 0), reference);
        int n = ExtendedSources.MaxPlaces;
        float trees = share.Sum();
        var wood = new FoliageSynth(tree, Rate, 5, n) { Wind = 4.5f, Trees = trees, Cut = cut };
        var one = new FoliageSynth(tree, Rate, 5) { Wind = 4.5f, Trees = trees };
        double alone = 0;
        int seconds = 8;
        var x = Render(n, seconds, s => { wood.NextPlaces(s); float o = one.Next(); alone += o * (double)o; },
                       () => { wood.Control(256f / Rate); one.Control(256f / Rate); }, settle: 2);
        alone /= Rate * (seconds + 2.0);
        double together = x.Sum(Power);
        _o.WriteLine($"a wood of {trees:F1} trees' worth in {cut.Count} pieces: together {Db(together / alone):F2} dB against one point");
        Assert.InRange(Db(together / alone), -1.5, 1.5);
        for (int p = cut.Places; p < n; p++) Assert.All(x[p], v => Assert.Equal(0f, v));
        for (int p = 0; p < cut.Places; p++)
        {
            double heard = Power(x[p]) / together;
            float s = cut.ShareOfPlace(p);
            _o.WriteLine($"  place {p}: {s:P0} of the crowns, {heard:P0} of the power");
            if (s > 0.08f) Assert.InRange(Db(heard / s), -1.5, 1.5);
        }
        double worst = 0;
        for (int a = 0; a < cut.Places; a++)
            for (int b = a + 1; b < cut.Places; b++)
            {
                double ab = 0, ea = 0, eb = 0;
                for (int i = 0; i < x[a].Length; i++) { ab += x[a][i] * (double)x[b][i]; ea += x[a][i] * (double)x[a][i]; eb += x[b][i] * (double)x[b][i]; }
                if (ea > 0 && eb > 0) worst = Math.Max(worst, Math.Abs(ab) / Math.Sqrt(ea * eb));
            }
        _o.WriteLine($"  largest correlation between two pieces {worst:F3}");
        Assert.True(worst < 0.05);
    }

    // ── Through the client ──────────────────────────────────────────────────────────────────────

    private static void Fire(ClientAudioHarness h, int id, string key, Vector3 at, Quaternion turn)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = turn, Scale = Vector3.One },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = key;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 400f;
        h.World.RegisterDefinition(def);
    }

    /// <summary>What the mixer makes at the ear of every live voice of the sources <paramref name="source"/> takes, its middle's
    /// reference, and how many voices.</summary>
    private static (double Power, float Reference, int Voices) Heard(ClientAudioHarness h, Vector3 ear, Func<string, bool> source)
    {
        var heard = AudioWorld.HeardTogether(h.Mixer.Latest, h.Mixer.Live, h.Mixer.Pieces, source, ear, h.Facade.Options.WideSources);
        float reference = h.Mixer.Latest.Values.Where(e => source(e.PhysicalKey ?? "") && e.PlaceOfEntity == 0).Select(e => e.MinDistance).FirstOrDefault();
        return (heard.Power, reference, heard.Voices);
    }

    /// <summary>
    /// Walking round a burning car at 4 m and then away from it to 80 m in steps of 5 cm, the voices the client places are
    /// the car's whole burning area heard by the mixer's law at every step (the bed's half at its height and the flames'
    /// above it), to within a fifth of a decibel of one gain all the way: no step as it is cut again, parted or merged.
    /// Near, it is heard from many pieces; at 80 m, well past the merge, from one.
    /// </summary>
    [Fact]
    public void WalkingRoundAndAwayFromABurningCarItIsItsWholeAreaAtEveryStep()
    {
        var h = new ClientAudioHarness();
        var spec = FireSpec.BurningCar;
        var at = new Vector3(0f, spec.BedHeightMetres, 0f);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        Fire(h, 700, "fire:burning_car", at, turn);
        var bed = ExtendedSources.FireOutline(spec.Outline, at, turn);
        var flames = ExtendedSources.FireOutline(spec.Outline, at + new Vector3(0f, spec.RoarRiseMetres, 0f), turn);
        float eye = LocalPlayerState.StandingEyeHeight;
        var walk = new List<Vector3>();
        for (float a = 0f; a < MathF.Tau; a += 0.05f / 4f) walk.Add(new Vector3(MathF.Sin(a) * 4f, 0f, -MathF.Cos(a) * 4f));
        for (float d = 4f; d <= 80f; d += 0.05f) walk.Add(new Vector3(0f, 0f, -d));
        h.StandAt(walk[0]);
        h.Tick(240);
        var ratios = new List<double>();
        int most = 0, last = 0;
        foreach (var feet in walk)
        {
            h.StandAt(feet);
            h.Tick();
            var ear = feet + new Vector3(0f, eye, 0f);
            var (power, reference, voices) = Heard(h, ear, id => id.StartsWith("fire:"));
            double law = 0.5 * ExtendedSources.AreaMeanLawPower(bed, ear, reference) + 0.5 * ExtendedSources.AreaMeanLawPower(flames, ear, reference);
            ratios.Add(Db(power / law));
            most = Math.Max(most, voices);
            last = voices;
        }
        double steps = 0;
        for (int i = 1; i < ratios.Count; i++) steps = Math.Max(steps, Math.Abs(ratios[i] - ratios[i - 1]));
        _o.WriteLine($"a burning car walked round at 4 m and away to 80 m: against its whole area {ratios.Min() - ratios[0]:F3} to {ratios.Max() - ratios[0]:F3} dB, " +
                     $"the most a 5 cm step moved it {steps:F3} dB; {most} voices at most, {last} at 80 m");
        Assert.True(ratios.Max() - ratios.Min() < 0.2, $"the pieces stray {ratios.Max() - ratios.Min():F2} dB from the whole area");
        Assert.True(most >= 6, $"{most} voices beside it");
        Assert.Equal(1, last);
    }

    /// <summary>
    /// Walking toward a wood of park trees from 300 m to 115 m (every crown past the hand-over, so wholly in its wood), the
    /// wood's voices are its crowns each at its own distance by the mixer's law to within a fifth of a decibel of one gain:
    /// each piece as loud as its crowns, with no gain holding the wood to its middle. Near, it is heard from many pieces.
    /// </summary>
    [Fact]
    public void WalkingUpToAWoodItIsItsCrownsEachWhereItStands()
    {
        var h = new ClientAudioHarness();
        var (at, _) = Crowns(7);
        for (int i = 0; i < at.Length; i++)
        {
            var crown = at[i] + new Vector3(100f, 0f, 100f);
            var def = new EntityDefinition
            {
                EntityId = 800 + i,
                Type = EntityType.StaticObject,
                Transform = new Transform { Position = crown, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = "foliage:park_tree";
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.MinDistance = 4f;
            def.SoundEmitter.Range = 90f;
            h.World.RegisterDefinition(def);
            at[i] = crown;
        }
        h.World.RefreshWoods();
        var walk = new List<Vector3>();
        // From 200 m: further out the edge of the wood's range fades its far crowns, which the law here leaves out.
        for (float z = -200f; z <= -115f; z += 0.5f) walk.Add(new Vector3(100f, 0f, z));
        h.StandAt(walk[0]);
        h.Tick(240);
        var ratios = new List<double>();
        int most = 0;
        float eye = LocalPlayerState.StandingEyeHeight;
        var all = at.Select(_ => 1f).ToArray();
        foreach (var feet in walk)
        {
            h.StandAt(feet);
            h.Tick();
            var ear = feet + new Vector3(0f, eye, 0f);
            var (power, reference, voices) = Heard(h, ear, id => id.StartsWith(WoodChorus.Prefix));
            ratios.Add(Db(power / ExtendedSources.PointsMeanLawPower(at, all, ear, reference)));
            most = Math.Max(most, voices);
        }
        _o.WriteLine($"a wood of {at.Length} park trees from 300 m to 115 m: against its crowns {ratios.Min() - ratios[0]:F3} to {ratios.Max() - ratios[0]:F3} dB; {most} voices at most");
        Assert.True(ratios.Max() - ratios.Min() < 0.2, $"the pieces stray {ratios.Max() - ratios.Min():F2} dB from its crowns");
        Assert.True(most >= 6, $"{most} voices near it");
    }
}
