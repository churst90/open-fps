using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Fire that burns what is there (docs/FIRE.md 12): a fire over a shape, fuel as a property of things,
/// ignition by heat received and by brands, the weather and water.
/// </summary>
public class FireSpreadTests
{
    private readonly ITestOutputHelper _o;
    public FireSpreadTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private const int Rate = 48000;

    private static FireWeather Air(float wind, float rain = 0f) => new(WindWeather.Steady(wind, 270f, 0.2f), rain, 24f, 35f);

    private static void Run(FireSpread s, double seconds, Func<double, FireWeather> weather, double from = 0)
    {
        for (double t = from + 1; t <= from + seconds; t += 1) s.Step(t, 1f, weather(t));
    }

    private void Timeline(FireSpread s)
    {
        foreach (var e in s.Events) _o.WriteLine(FireSpread.Describe(e, 0, s.Objects));
    }

    // ── Shapes ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFireFillsTheShapeItIsGiven()
    {
        var circle = FireShape.Circle(1.8f);
        Assert.Equal(MathF.PI * 0.81f, circle.Area, 3);
        var (vx, vz, c) = circle.Moments();
        Assert.Equal(0.9f * 0.9f / 4f, vx, 4);
        Assert.Equal(vx, vz, 4);
        Assert.Equal(0f, c, 4);
        // An outline of a rectangle is the rectangle; turned 45 degrees it spreads along its diagonal.
        var rect = FireShape.Outline(new[] { new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 1), new Vector2(0, 1) });
        Assert.Equal(4f, rect.Area, 3);
        var (rx, rz, rc) = rect.Moments();
        Assert.Equal(16f / 12f, rx, 3);
        Assert.Equal(1f / 12f, rz, 3);
        Assert.Equal(0f, rc, 3);
        float h = MathF.Sqrt(0.5f);
        var turned = FireShape.Outline(new[] { new Vector2(0, 0), new Vector2(4 * h, 4 * h), new Vector2(4 * h - h, 4 * h + h), new Vector2(-h, h) });
        Assert.True(turned.Moments().CovXZ > 0.5f);
        Assert.True(turned.Contains(0f, 0f));
        Assert.False(turned.Contains(1.5f, -1.5f));

        // In a key, and back.
        foreach (var shape in new[] { circle, FireShape.Rectangle(3.5f, 2f), rect })
        {
            string key = FireSpec.KeyFor("bonfire", 100.5, shape);
            FireSpec.ParseKey(key, out string preset, out double? lit, out var back);
            Assert.Equal("bonfire", preset);
            Assert.Equal(100.5, lit!.Value, 3);
            Assert.NotNull(back);
            Assert.Equal(shape.Area, back!.Area, 2);
        }

        // As hard per square metre over a bigger bed: heat and power with the area.
        var pit = FireSpec.GardenFirePit;
        var big = FireSpec.ByName(FireSpec.KeyFor("fire_pit", null, FireShape.Rectangle(1.8f, 1.8f)));
        Assert.Equal(4f * pit.HeatReleaseKw, big.HeatReleaseKw, 1);
        Assert.Equal(pit.SourceLevelDb + 10f * MathF.Log10(4f), big.SourceLevelDb, 2);
        Assert.Equal(1.8f, big.BaseDiameterMetres, 3);
        // A round bed of the pit's width is the pit's square less its corners.
        var round = FireSpec.ByName(FireSpec.KeyFor("fire_pit", null, FireShape.Circle(0.9f)));
        Assert.Equal(pit.HeatReleaseKw * MathF.PI / 4f, round.HeatReleaseKw, 1);
    }

    [Fact]
    public void APlacedFirePitTakesItsSize()
    {
        // At the prefab's own size (0.9 m), the preset; scaled, a bigger fire; round, a round bed.
        Assert.Equal("fire:fire_pit", FireSpec.KeyForPlaced("fire:fire_pit", ColliderShape.Box, new Vector3(0.9f, 0.6f, 0.9f)));
        string scaled = FireSpec.KeyForPlaced("fire:fire_pit", ColliderShape.Box, new Vector3(1.8f, 1.2f, 1.8f));
        Assert.Equal("fire:fire_pit/shape=r1.8x1.8", scaled);
        string round = FireSpec.KeyForPlaced("fire:fire_pit", ColliderShape.Cylinder, new Vector3(1.2f, 0.6f, 1.2f));
        Assert.Equal("fire:fire_pit/shape=c1.2", round);
        // A key that says its shape keeps it.
        Assert.Equal("fire:bonfire/lit=5.0/shape=c2", FireSpec.KeyForPlaced("fire:bonfire/lit=5.0/shape=c2", ColliderShape.Box, new Vector3(9f, 1f, 9f)));
        // Not a fire: untouched.
        Assert.Equal("foliage:park_tree", FireSpec.KeyForPlaced("foliage:park_tree", ColliderShape.Box, Vector3.One));
        // And the places follow the shape: a round bed's ring is narrower than its square's.
        var square = ExtendedSources.Layout("fire:fire_pit")!;
        var circle = ExtendedSources.Layout(round)!;
        Assert.Equal(square.Length, circle.Length);
        Assert.True(circle[1].Length() < 1.2f / 0.9f * square[1].Length());
    }

    [Fact]
    public void ARoundFireIsLaidOutInsideItsCircle()
    {
        var spec = FireSpec.Litter.WithShape(FireShape.Circle(6f));
        int n = FireSynth.Layout(spec).Length;
        var f = new FireSynth(spec, Rate, 3, n) { Spread = 1f };
        // 6 x 6 bounds at 1 m bodies: 36 on the grid, about 28 inside the circle.
        Assert.InRange(f.Cells, 22, 32);
    }

    // ── Catching ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StumpsCatchDownwindInAWindAndNotInAStill()
    {
        foreach (float wind in new[] { 6f, 0.5f })
        {
            var s = new FireSpread(7);
            var ids = Enumerable.Range(0, 4).Select(i => s.Add(new FuelObject($"stump {i}", new Vector3(i, 0f, 0f), 0f, FuelCatalog.Stump(0.6f, 0.4f)))).ToList();
            // And one upwind.
            int up = s.Add(new FuelObject("upwind", new Vector3(-1f, 0f, 0f), 0f, FuelCatalog.Stump(0.6f, 0.4f)));
            s.Light(ids[0], 0);
            Run(s, 1800, _ => Air(wind));
            Timeline(s);
            if (wind > 1f)
            {
                Assert.True(s.HasCaught(ids[1]), "the next stump downwind did not catch in a 6 m/s wind");
                Assert.True(s.CaughtAt(ids[1]) < s.CaughtAt(ids[2]));
                Assert.Contains(s.Events, e => e.Object == ids[1] && e.Cause == IgnitionCause.FlameContact);
            }
            else Assert.False(s.HasCaught(ids[1]), "a stump caught from its neighbour in still air");
            Assert.False(s.HasCaught(up), "the stump upwind caught");
        }
    }

    [Fact]
    public void AGapWithNoFuelStopsTheFlamesButBrandsJumpItDownwind()
    {
        int across = 0, calm = 0;
        foreach (float wind in new[] { 6f, 0.5f })
        {
            var s = new FireSpread(11);
            var row = Enumerable.Range(0, 4).Select(i => s.Add(new FuelObject($"tree {i}", new Vector3(6f * i, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f)))).ToList();
            var far = Enumerable.Range(0, 3).Select(i => s.Add(new FuelObject($"far {i}", new Vector3(18f + 30f + 6f * i, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f)))).ToList();
            s.Strike(Vector3.Zero, 0, force: true);
            Run(s, 900, _ => Air(wind));
            Timeline(s);
            // Nothing across the gap catches by heat: only brands, which the wind carries.
            Assert.DoesNotContain(s.Events, e => far.Contains(e.Object) && e.What == "caught" && e.Cause is IgnitionCause.Radiation or IgnitionCause.FlameContact
                                                  && !far.Contains(e.From));
            int caught = far.Count(s.HasCaught);
            if (wind > 1f) across = caught; else calm = caught;
        }
        _o.WriteLine($"across the gap: {across} in the wind, {calm} in still air");
        Assert.True(across > 0, "no brand crossed the gap downwind");
        Assert.Equal(0, calm);
    }

    [Fact]
    public void ACrownTorchesOnlyOverAnIntenseEnoughSurfaceFire()
    {
        // Van Wagner (1977): CBH 2 m, foliar moisture 100 %: (0.010 × 2 × (460 + 2590))^1.5 ≈ 476 kW/m.
        Assert.Equal(476f, FireSpread.VanWagnerIntensity(2f, 1f), 0);
        foreach (float wind in new[] { 6f, 0.5f })
        {
            var s = new FireSpread(3);
            int tree = s.Add(new FuelObject("tree", Vector3.Zero, 0f, FuelCatalog.Tree(5f, 2f, 12f)));
            s.Light(tree, 0, "litter");
            Run(s, 300, _ => Air(wind));
            Timeline(s);
            bool torched = !double.IsNaN(s.CaughtAt(tree, "crown"));
            Assert.Equal(wind > 1f, torched);
        }
        // A crown high above its litter needs more than the litter gives.
        Assert.True(FireSpread.VanWagnerIntensity(8f, 1f) > 3000f);
    }

    [Fact]
    public void LightningStrikesWhatStandsHighest()
    {
        var s = new FireSpread(5);
        int stump = s.Add(new FuelObject("stump", new Vector3(5f, 0f, 0f), 0f, FuelCatalog.Stump(0.6f, 0.4f)));
        int tree = s.Add(new FuelObject("tree", new Vector3(30f, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f)));
        Assert.Equal(tree, s.Strike(new Vector3(0f, 0f, 0f), 0, force: true));
        Assert.True(s.HasCaught(tree));
        Assert.False(s.HasCaught(stump));
        // Out of its reach (the rolling sphere: 10 × 30^0.65 ≈ 91 m), the ground.
        Assert.Equal(-1, s.Strike(new Vector3(500f, 0f, 0f), 1, force: true));
    }

    [Fact]
    public void TheSpreadIsTheSameEachTime()
    {
        string Once()
        {
            var s = new FireSpread(42);
            for (int i = 0; i < 6; i++) s.Add(new FuelObject($"tree {i}", new Vector3(6f * i, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f)));
            s.Strike(Vector3.Zero, 0, force: true);
            Run(s, 300, _ => Air(5f));
            return string.Join("\n", s.Events.Select(e => FireSpread.Describe(e, 0, s.Objects)));
        }
        Assert.Equal(Once(), Once());
    }

    // ── Weather and water ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RainWetsWhatHasNotCaughtAndAHeavyOnePutsAFireOut()
    {
        // Simard's equilibrium moisture: dry air, dry fuel; damp air, damp fuel.
        Assert.InRange(FireSpread.EquilibriumMoisture(30f, 20f), 0.02f, 0.06f);
        Assert.InRange(FireSpread.EquilibriumMoisture(10f, 90f), 0.15f, 0.25f);

        // A campfire in light rain burns on; in a cloudburst it goes out.
        foreach (float rain in new[] { 2f, 60f })
        {
            var s = new FireSpread(1);
            int fire = s.Add(new FuelObject("campfire", Vector3.Zero, 0f, FuelCatalog.ForFire("campfire")));
            s.Light(fire, -1200);
            Run(s, 300, _ => Air(2f, rain));
            Timeline(s);
            var b = s.Burning(300, coolingSeconds: 1000f).Single();
            if (rain < 10f) { Assert.True(b.Running); Assert.InRange(b.Quench, 0f, 0.2f); }
            else Assert.False(b.Running);
        }

        // After rain the litter under a tree will not take a brand: two hours of 10 mm/h.
        var w = new FireSpread(2);
        int tree = w.Add(new FuelObject("tree", Vector3.Zero, 0f, FuelCatalog.Tree(5f, 2f, 12f)));
        Run(w, 7200, _ => Air(2f, 10f));
        var litter = w.State(tree).First(p => p.Part == "litter");
        _o.WriteLine($"litter after rain: {litter.Moisture:F2}");
        Assert.True(litter.Moisture >= 0.3f);
        Assert.Equal(tree, w.Strike(Vector3.Zero, 7200, continuingCurrentChance: 1f));
        Assert.False(w.HasCaught(tree));
    }

    [Fact]
    public void WaterOnAFireCoolsItButOnOilItFlares()
    {
        var s = new FireSpread(4);
        int pile = s.Add(new FuelObject("pile", Vector3.Zero, 0f, FuelCatalog.WoodPile(2f, 1f, 1f)));
        s.Light(pile, -3000);
        Run(s, 10, _ => Air(1f), -10);
        // A hose: two kilograms a second over two square metres, landing on the pile's middle. Water landing
        // on bare ground reaches nothing.
        Assert.Equal(-1, s.AddWaterAt(new Vector2(20f, 20f), 2f, 120f, 0));
        Assert.Equal(pile, s.AddWaterAt(new Vector2(0.2f, 0.1f), 2f, 120f, 0));
        Run(s, 5, _ => Air(1f));
        var on = s.WaterOn(pile, 5);
        Assert.Equal(1f, on.KgPerSquareMetreSecond, 2);
        Assert.True(on.Quench > 0.3f);
        Run(s, 115, _ => Air(1f), 5);
        Timeline(s);
        Assert.Contains(s.Events, e => e.Object == pile && e.What == "put out by water");

        var oil = new FuelSpec { Name = "pan", Parts = new[] { FuelCatalog.ForFire("campfire").Parts[0] with { WaterFlares = true } } };
        var t = new FireSpread(4);
        int pan = t.Add(new FuelObject("pan", Vector3.Zero, 0f, oil));
        t.Light(pan, -1200);
        Run(t, 5, _ => Air(1f), -5);
        float before = t.Burning(0).Single().HeatKw;
        t.AddWater(pan, 0.5f, 5f, 0);
        Run(t, 1, _ => Air(1f));
        Assert.True(t.Burning(1).Single().HeatKw > 2f * before, "water on burning oil did not throw it");
        Assert.DoesNotContain(t.Events, e => e.What == "put out by water");
    }

    // ── Sound ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WaterTakesTheRoarAndTheFireHissesAndTicksAsItGoesOut()
    {
        var spec = FireSpec.Bonfire;
        float Power(float quench, bool lit, float from, float seconds, out FireSynth f)
        {
            f = new FireSynth(spec, Rate, 9, 1) { Wind = 2f, CracklePart = 0f, SteamPart = 0f, SettlePart = 0f };
            double e = 0;
            int n = (int)((from + seconds) * Rate);
            for (int i = 0; i < n; i++)
            {
                if (i % 256 == 0) { f.Quench = i > Rate ? quench : 0f; f.Lit = i <= Rate || lit; f.Control(256f / Rate); }
                float v = f.Next();
                if (i >= from * Rate) e += v * v;
            }
            return (float)(e / (seconds * Rate));
        }
        float dry = Power(0f, true, 20f, 10f, out _);
        float doused = Power(0.9f, true, 20f, 10f, out _);
        _o.WriteLine($"roar and fizz: dry {10 * Math.Log10(dry / 4e-10):F1} dB, nine-tenths doused {10 * Math.Log10(doused / 4e-10):F1} dB");
        Assert.True(doused < 0.3f * dry);
        // Put out, it ticks a while as its char cools, then falls silent.
        var g = new FireSynth(FireSpec.Campfire, Rate, 9, 1) { Wind = 2f };
        double early = 0, late = 0;
        for (int i = 0; i < Rate * 240; i++)
        {
            if (i % 256 == 0) { g.Lit = i < Rate * 5; g.Control(256f / Rate); }
            float v = g.Next();
            if (i >= Rate * 30 && i < Rate * 60) early += v * v;
            if (i >= Rate * 210) late += v * v;
        }
        _o.WriteLine($"cooling: {10 * Math.Log10(early / (30.0 * Rate) / 4e-10):F1} dB half a minute after, {10 * Math.Log10(late / (30.0 * Rate) / 4e-10 + 1e-30):F1} dB four minutes after");
        Assert.True(g.HeatNowKw < 1f);
        Assert.True(early > 0, "no ticks as the char cooled");
        Assert.True(late < 0.25 * early, "the char ticked as much four minutes after as at first");
    }

    [Fact]
    public void NothingAllocatesWhileWaterPutsAFireOut()
    {
        var spec = FireSpec.WoodPile;
        int n = FireSynth.Layout(spec).Length;
        var f = new FireSynth(spec, Rate, 3, n) { Spread = 1f };
        var buf = new float[n];
        void Go(int samples, float quench, bool lit)
        {
            for (int i = 0; i < samples; i++)
            {
                if (i % 256 == 0) { f.Wind = 3f; f.Quench = quench; f.Lit = lit; f.Control(256f / Rate); }
                f.NextPlaces(buf);
            }
        }
        Go(Rate, 0f, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Go(Rate * 2, 0.7f, true);
        Go(Rate * 4, 1f, false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024, $"allocated {allocated} bytes while going out");
    }

    // ── Fuel from things ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatAThingBurnsAsComesFromWhatItIs()
    {
        // A crown of foliage off the ground: a tree, with litter under it.
        var tree = FuelCatalog.ForThing("", "Foliage", ColliderShape.Box, new Vector3(8f, 8f, 8f), 3f);
        Assert.NotNull(tree);
        Assert.Equal(new[] { "litter", "crown", "branches" }, tree!.Parts.Select(p => p.Name));
        Assert.Equal(3f, tree.Parts[1].CrownBaseMetres);
        // Wood on the ground by its proportions.
        Assert.Equal("stump", FuelCatalog.ForThing("", "Wood", ColliderShape.Box, new Vector3(0.6f, 0.4f, 0.6f), 0f)!.Parts[0].Preset);
        Assert.Equal("wood_pile", FuelCatalog.ForThing("", "Wood", ColliderShape.Box, new Vector3(2f, 1f, 1f), 0f)!.Parts[0].Preset);
        Assert.Equal("tree_trunk", FuelCatalog.ForThing("", "Wood", ColliderShape.Box, new Vector3(0.5f, 3f, 0.5f), 0f)!.Parts[0].Preset);
        // A floor and a fence board are structures, a later stage; brick does not burn.
        Assert.Null(FuelCatalog.ForThing("", "Wood", ColliderShape.Box, new Vector3(5f, 0.1f, 5f), 0f));
        Assert.Null(FuelCatalog.ForThing("", "Brick", ColliderShape.Box, new Vector3(1f, 1f, 1f), 0f));
        // A vehicle by its engine; a map's fire as itself, burning.
        Assert.Equal("burning_car", FuelCatalog.ForThing("engine:sedan", "Metal", ColliderShape.Box, new Vector3(1.8f, 1.4f, 4.5f), 0f)!.Parts[0].Preset);
        var pit = FuelCatalog.ForThing("fire:fire_pit", "None", ColliderShape.Box, new Vector3(0.9f, 0.6f, 0.9f), 0.3f)!;
        Assert.True(pit.AlwaysBurning);
        Assert.Equal("fire:fire_pit", pit.Parts[0].Preset);
        // Land cover's hook.
        Assert.NotNull(FuelCatalog.ForLandCover("grass"));
        Assert.Null(FuelCatalog.ForLandCover("water"));
    }
}
