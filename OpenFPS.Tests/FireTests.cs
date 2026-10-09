using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Fire at any size (docs/FIRE.md): the bodies of fire over its area, the places it is heard from, the
/// roar's law, the crowd of crackles, its life and its events, and /spawn fire.
/// </summary>
public class FireTests : IDisposable
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-fire-{Guid.NewGuid():N}");

    public FireTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    private static float[] Render(FireSynth s, float seconds, float wind = 2f, Action<FireSynth, double>? each = null)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0)
            {
                if (each == null) s.Wind = wind;
                else each(s, i / (double)Rate);
                s.Control(256f / Rate);
            }
            x[i] = s.Next();
        }
        return x;
    }

    /// <summary>One-sided PSD averaged over a band, Welch with a Hann window, Pa²/Hz.</summary>
    private static double Psd(float[] x, float lo, float hi)
    {
        const int n = 16384;
        var w = new double[n];
        double wss = 0;
        for (int i = 0; i < n; i++) { w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); wss += w[i] * w[i]; }
        double sum = 0; int count = 0;
        for (int start = 0; start + n <= x.Length; start += n / 2)
        {
            var buf = new System.Numerics.Complex[n];
            for (int i = 0; i < n; i++) buf[i] = new System.Numerics.Complex(x[start + i] * w[i], 0);
            Spectrum.Fft(buf);
            for (int k = (int)(lo * n / Rate); k <= (int)(hi * n / Rate); k++) { sum += 2 * buf[k].Magnitude * buf[k].Magnitude / (Rate * wss); count++; }
        }
        return sum / Math.Max(1, count);
    }

    private static double Db(float[] x) => 10 * Math.Log10(x.Average(v => (double)v * v) / 4e-10);

    [Fact]
    public void EveryFireIsHeardFromItsPlacesAndNoMoreThanEight()
    {
        foreach (var (name, make) in FireSpec.Presets)
        {
            var spec = make();
            var layout = ExtendedSources.Layout("fire:" + name);
            Assert.NotNull(layout);
            Assert.Equal(Math.Clamp(spec.Places, 1, FireSynth.MaxPlaces), layout!.Length);
            Assert.True(layout.Length <= ExtendedSources.MaxPlaces, $"{name} has {layout.Length} places; a source has {ExtendedSources.MaxPlaces} voice ids");
            // The places, each an equal share, spread across and along as the burning area does (w²/12, d²/12),
            // a front along its length; so the ears hear the fire as wide as it is.
            double vx = layout.Average(p => (double)p.X * p.X), vz = layout.Average(p => (double)p.Z * p.Z);
            double ax = spec.AreaWidth * spec.AreaWidth / 12.0, az = spec.AreaDepth * spec.AreaDepth / 12.0;
            _o.WriteLine($"  spread across {Math.Sqrt(vx):F2} m against the area's {Math.Sqrt(ax):F2}, along {Math.Sqrt(vz):F2} against {Math.Sqrt(az):F2}");
            Assert.InRange(vx / ax, 0.8, 1.2);
            if (spec.AreaWidth < 2.5f * spec.AreaDepth) Assert.InRange(vz / az, 0.8, 1.2);
            Assert.Equal(Vector3.Zero, layout[0]);
            // Every place over the burning area.
            foreach (var p in layout)
                Assert.True(MathF.Abs(p.X) <= 0.5f * spec.AreaWidth + 0.01f && MathF.Abs(p.Z) <= 0.5f * spec.AreaDepth + 0.01f,
                            $"{name}: a place at {p} is off its {spec.AreaWidth} x {spec.AreaDepth} m");
            var (nx, nz, d) = FireSynth.CellGrid(spec);
            Assert.InRange(nx * nz, 1, FireSynth.MaxCells);
            _o.WriteLine($"{name}: {layout.Length} places, {nx * nz} bodies of {d:F1} m");
        }
        // A key with its lighting time has the same places.
        Assert.Equal(ExtendedSources.Layout("fire:house_fire")!.Length, ExtendedSources.Layout(FireSpec.KeyFor("house_fire", 1234.5))!.Length);
    }

    [Fact]
    public void AKeySaysWhenTheFireWasLit()
    {
        string key = FireSpec.KeyFor("burning_car", 98765.4);
        Assert.Equal("fire:burning_car/lit=98765.4", key);
        FireSpec.ParseKey(key, out string preset, out double? lit);
        Assert.Equal("burning_car", preset);
        Assert.Equal(98765.4, lit!.Value, 3);
        FireSpec.ParseKey("fire:fire_pit", out preset, out lit);
        Assert.Equal("fire_pit", preset);
        Assert.Null(lit);
        Assert.Equal(FireSpec.BurningCar.Name, FireSpec.ByName("burning_car/lit=12").Name);
    }

    [Fact]
    public void AFireGrowsBurnsDiesAndSmoulders()
    {
        var spec = FireSpec.HouseFire;
        Assert.Equal(1f, FireSynth.LifeShare(spec, double.NaN));
        Assert.True(FireSynth.LifeShare(spec, 0) <= 0.01f);
        // t²: a quarter of the way through its growth it is a sixteenth of its full heat release.
        Assert.Equal(1f / 16f, FireSynth.LifeShare(spec, spec.GrowthSeconds / 4), 3);
        Assert.Equal(1f, FireSynth.LifeShare(spec, spec.GrowthSeconds + spec.SteadySeconds / 2));
        float halfDecay = FireSynth.LifeShare(spec, spec.GrowthSeconds + spec.SteadySeconds + spec.DecaySeconds / 2);
        Assert.InRange(halfDecay, 0.45f, 0.55f);
        Assert.Equal(0.03f, FireSynth.LifeShare(spec, spec.GrowthSeconds + spec.SteadySeconds + spec.DecaySeconds + 600), 3);
    }

    /// <summary>The roar's spectrum falls as f^−α from 20 Hz, and its unit is a PSD of 1 Pa²/Hz at 100 Hz.</summary>
    [Fact]
    public void TheRoarFallsAsItsPowerLaw()
    {
        var f = new PowerLawNoise();
        f.Tune(2.5f, FireSynth.RoarFloorHz, Rate);
        float at100 = f.Magnitude(100f, Rate);
        foreach (float hz in new[] { 200f, 400f, 1000f, 4000f })
        {
            double slope = 20 * Math.Log10(f.Magnitude(hz, Rate) / at100) / Math.Log2(hz / 100.0);
            _o.WriteLine($"{hz} Hz: {slope:F2} dB an octave of amplitude");
            Assert.InRange(slope, -3.0103 * 2.5 / 2 * 2 - 0.8, -3.0103 * 2.5 / 2 * 2 + 0.8);
        }
        // Under the floor it falls away.
        Assert.True(f.Magnitude(5f, Rate) < 0.2f * f.Magnitude(20f, Rate));
        // White noise of variance 1 through it, scaled by the unit, has a PSD of 1 Pa²/Hz at 100 Hz.
        var rng = new Random(3);
        var y = new float[Rate * 20];
        for (int i = 0; i < y.Length; i++) y[i] = f.Process(((float)rng.NextDouble() * 2f - 1f) * 1.7320508f) * f.UnitAtReference;
        double psd = Psd(y, 90f, 110f);
        _o.WriteLine($"PSD at 100 Hz {psd:F3} Pa²/Hz");
        Assert.InRange(psd, 0.7, 1.4);
    }

    /// <summary>
    /// The roar's power goes as the heat release per area squared times the area (docs/FIRE.md 1.5): four
    /// times the area at the same rate per square metre is four times the power, 6 dB; twice the rate per
    /// square metre on the same area is 6 dB too.
    /// </summary>
    [Fact]
    public void TheRoarGrowsWithTheAreaAndTheRatePerArea()
    {
        var one = FireSpec.HouseFire with { Panes = 0, WidthMetres = 6f, DepthMetres = 6f, HeatReleaseKw = 7200f };
        var four = one with { WidthMetres = 12f, DepthMetres = 12f, HeatReleaseKw = 28800f };
        var hotter = one with { HeatReleaseKw = 14400f };
        double Roar(FireSpec s)
        {
            var f = new FireSynth(s, Rate, 3, FireSynth.Layout(s).Length) { CracklePart = 0f, FizzPart = 0f, FallPart = 0f, Spread = 1f };
            return Db(Render(f, 12f, wind: 1f));
        }
        double a = Roar(one), b = Roar(four), c = Roar(hotter);
        _o.WriteLine($"36 m² {a:F1} dB, 144 m² {b:F1} dB, 36 m² at twice the rate {c:F1} dB");
        Assert.InRange(b - a, 4.5, 7.5);
        Assert.InRange(c - a, 4.5, 7.5);
    }

    /// <summary>The fizz is its own part: muting the crackles leaves it, and alone its power follows the
    /// heat release (docs/FIRE.md 7.3). It was scaled by the crackles' part.</summary>
    [Fact]
    public void TheFizzIsItsOwnPartAndFollowsTheHeatRelease()
    {
        FireSynth Only(FireSpec spec, float fizz) => new(spec, Rate, 3)
        {
            RoarPart = 0f, CracklePart = 0f, FizzPart = fizz, SteamPart = 0f, SettlePart = 0f,
            TorchPart = 0f, FallPart = 0f, GlassPart = 0f, BurstPart = 0f,
        };
        var pit = FireSpec.GardenFirePit;
        var alone = Render(Only(pit, 1f), 30f);
        var muted = Render(Only(pit, 0f), 30f);
        var hotter = Render(Only(pit with { HeatReleaseKw = 4f * pit.HeatReleaseKw }, 1f), 30f);
        double a = Db(alone), h = Db(hotter);
        double peakMuted = muted.Max(v => MathF.Abs(v));
        _o.WriteLine($"fizz alone {a:F1} dB at a metre; four times the heat {h:F1} dB; muted, peak {peakMuted:G3} Pa");
        Assert.True(a > 20.0, "the crackles muted took the fizz with them");
        Assert.True(peakMuted < 1e-6, "the fizz muted still sounds");
        Assert.InRange(h - a, 4.0, 8.0);
    }

    /// <summary>A bigger body of fire puffs slower: 1.5 / √D.</summary>
    [Fact]
    public void ABiggerBodyOfFirePuffsSlower()
    {
        var (_, _, d) = FireSynth.CellGrid(FireSpec.Bonfire);
        Assert.Equal(3.5f, d, 2);
        Assert.Contains("puffing at 0.80 Hz", new FireSynth(FireSpec.Bonfire, Rate, 1).Census());
        Assert.Contains("puffing at 0.30 Hz", new FireSynth(FireSpec.CrownFire, Rate, 1).Census());
    }

    /// <summary>A crown fire's heat release follows its rate of spread, U^0.9 (Cruz et al. 2005).</summary>
    [Fact]
    public void ACrownFireFollowsTheWind()
    {
        float Heat(float wind)
        {
            var f = new FireSynth(FireSpec.CrownFire, Rate, 2, 1) { CracklePart = 0f };
            for (int i = 0; i < 2000; i++) { f.Wind = wind * WindField.HeightShare(FireSpec.CrownFire.FlameHeightMetres); f.Control(0.1f); }
            return f.HeatNowKw;
        }
        float slow = Heat(5f), fast = Heat(15f);
        _o.WriteLine($"5 m/s {slow / 1e6:F2} GW, 15 m/s {fast / 1e6:F2} GW");
        Assert.InRange(fast / slow, MathF.Pow(3f, 0.9f) * 0.8f, MathF.Pow(3f, 0.9f) * 1.25f);
    }

    /// <summary>A house fire's places are independent streams, and together they are the fire.</summary>
    [Fact]
    public void AHouseFiresPlacesAreIndependentAndAddUpToIt()
    {
        var spec = FireSpec.HouseFire;
        int n = FireSynth.Layout(spec).Length;
        var f = new FireSynth(spec, Rate, 9, n) { Spread = 1f, FallPart = 0f };
        var places = new float[n][];
        for (int p = 0; p < n; p++) places[p] = new float[Rate * 8];
        var buf = new float[n];
        for (int i = 0; i < Rate * 8; i++)
        {
            if (i % 256 == 0) { f.Wind = 2f; f.Control(256f / Rate); }
            f.NextPlaces(buf);
            for (int p = 0; p < n; p++) places[p][i] = buf[p];
        }
        double worst = 0, sumPower = 0, partPowers = 0;
        for (int a = 0; a < n; a++)
        {
            double ea = places[a].Sum(v => (double)v * v);
            partPowers += ea;
            for (int b = a + 1; b < n; b++)
            {
                double eb = places[b].Sum(v => (double)v * v), ab = 0;
                for (int i = 0; i < places[a].Length; i++) ab += places[a][i] * places[b][i];
                worst = Math.Max(worst, Math.Abs(ab / Math.Sqrt(ea * eb)));
            }
        }
        for (int i = 0; i < places[0].Length; i++) { double s = 0; for (int p = 0; p < n; p++) s += places[p][i]; sumPower += s * s; }
        _o.WriteLine($"worst correlation {worst:F3}; sum {10 * Math.Log10(sumPower):F2}, parts {10 * Math.Log10(partPowers):F2}");
        Assert.True(worst < 0.1, $"two places of the house correlate {worst:F3}");
        Assert.InRange(10 * Math.Log10(sumPower / partPowers), -0.5, 0.5);
    }

    /// <summary>A big fire's crackles are a crowd: at most a few hundred a second a place are drawn one by
    /// one, and the rest are noise of the same power.</summary>
    [Fact]
    public void ABigFiresCracklesAreACrowd()
    {
        // A pile ten thousand times a bonfire, to have a crowd at every place.
        var spec = FireSpec.Bonfire with { HeatReleaseKw = 4e7f };
        int n = FireSynth.Layout(spec).Length;
        var f = new FireSynth(spec, Rate, 4, n) { Spread = 1f };
        Render(f, 4f, wind: 3f);
        double perPlace = f.DrawnCrackles / 4.0 / n;
        _o.WriteLine($"{f.CrackleRate:F0} crackles a second; {perPlace:F0} drawn a second at each place");
        Assert.True(f.CrackleRate > 4 * FireSynth.MaxDrawnCrackles * n);
        Assert.InRange(perPlace, 0.5 * FireSynth.MaxDrawnCrackles, 1.3 * FireSynth.MaxDrawnCrackles);
        // A campfire's are all drawn.
        var camp = new FireSynth(FireSpec.Campfire, Rate, 4, 4) { Spread = 1f };
        Render(camp, 20f);
        Assert.InRange(camp.DrawnCrackles / 20.0, 0.3 * camp.CrackleRate, 3 * camp.CrackleRate);
    }

    /// <summary>
    /// A house lit at a known moment: its windows crack as its rooms go and fall out later, it comes down
    /// in four collapses once it is fully alight, and burnt pieces fall. A car's struts burst once each.
    /// </summary>
    [Fact]
    public void AHouseAndACarHaveTheirEventsInTheirTime()
    {
        var house = new FireSynth(FireSpec.HouseFire, Rate, 5, 7) { Spread = 1f };
        for (int i = 0; i < 400 && !house.GlassReady; i++) Thread.Sleep(50);
        double end = FireSpec.HouseFire.GrowthSeconds + FireSpec.HouseFire.SteadySeconds;
        for (double t = 0; t < end; t += 0.25) { house.Age = t; house.Wind = 2f; house.Control(0.25f); }
        _o.WriteLine($"house: {house.PaneCracks} cracks, {house.PaneFalls} panes fell, {house.Collapses} collapses, {house.Falls} falls");
        Assert.Equal(FireSpec.HouseFire.Panes, house.PaneCracks);
        Assert.Equal(FireSpec.HouseFire.Panes, house.PaneFalls);
        Assert.Equal(4, house.Collapses);
        Assert.True(house.Falls > 100);

        var car = new FireSynth(FireSpec.BurningCar, Rate, 6, 4) { Spread = 1f };
        for (double t = 0; t < 3600; t += 0.25) { car.Age = t; car.Wind = 2f; car.Control(0.25f); }
        _o.WriteLine($"car: {car.Bursts} bursts, {car.PaneCracks} windows");
        Assert.InRange(car.Bursts, FireSpec.BurningCar.Struts, FireSpec.BurningCar.Struts + FireSpec.BurningCar.Tyres);
        Assert.Equal(FireSpec.BurningCar.Panes, car.PaneCracks);

        // Heard first part way through its life, what is past stays past.
        var late = new FireSynth(FireSpec.HouseFire, Rate, 5, 7);
        late.Age = end;
        late.Control(0.01f);
        Assert.Equal(0, late.PaneCracks);
    }

    /// <summary>A stand of trees torches a tree at a time, and burnt branches come down.</summary>
    [Fact]
    public void TreesTorchAndDropBranches()
    {
        var f = new FireSynth(FireSpec.BurningTrees, Rate, 8, 7) { Spread = 1f };
        for (int i = 0; i < 6000; i++) { f.Wind = 3f; f.Control(0.1f); }
        _o.WriteLine($"{f.Torches} torchings, {f.Falls} falls in ten minutes");
        Assert.InRange(f.Torches, 20, 120);
        Assert.InRange(f.Falls, 50, 400);
    }

    /// <summary>Rendering a fire allocates nothing: it runs on the render workers for the mixer.</summary>
    [Fact]
    public void NothingAllocatesWhileAFireBurns()
    {
        foreach (var name in new[] { "fire_pit", "burning_car", "house_fire", "crown_fire" })
        {
            var spec = FireSpec.ByName(name);
            int n = FireSynth.Layout(spec).Length;
            var f = new FireSynth(spec, Rate, 3, n) { Spread = 1f };
            for (int i = 0; i < 400 && !f.GlassReady; i++) Thread.Sleep(50);
            if (!float.IsNaN(spec.GrowthSeconds)) f.Age = spec.GrowthSeconds;
            var buf = new float[n];
            void Run(int samples)
            {
                for (int i = 0; i < samples; i++)
                {
                    if (i % 256 == 0) { f.Wind = 3f; f.Age += 256.0 / Rate * 50; f.Control(256f / Rate); }
                    f.NextPlaces(buf);
                }
            }
            Run(Rate);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(Rate * 4);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            _o.WriteLine($"{name}: {allocated} bytes");
            Assert.True(allocated < 1024, $"{name} allocated {allocated} bytes while burning");
        }
    }

    /// <summary>Each fire renders at the level it declares, every place summed, fully developed.</summary>
    [Fact]
    public void EachFireRendersAtTheLevelItDeclares()
    {
        foreach (var (name, make) in FireSpec.Presets)
        {
            var spec = make();
            var f = new FireSynth(spec, Rate, 5, FireSynth.Layout(spec).Length) { Spread = 1f };
            // Declared at 3 m/s at the flames; a crown fire in the field's own wind, which its spread follows.
            // Trees torch in turn, a minute or two apart: their level is a long one.
            float seconds = spec.Fuel == FireFuel.Trees ? 240f : 60f;
            var x = spec.Fuel == FireFuel.Crown ? Render(f, seconds, each: (s, t) => s.ReadWind(0f, 0f, t)) : Render(f, seconds, wind: 3f);
            double db = Db(x);
            _o.WriteLine($"{name}: {db:F1} dB against {spec.SourceLevelDb:F1}");
            Assert.InRange(db, spec.SourceLevelDb - 3, spec.SourceLevelDb + 3);
        }
    }

    // ── /spawn fire ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpawnFireLightsOneInFrontOfYouAndOutPutsItOut()
    {
        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(mapDir, "default.json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.TryGetMap("default", out var world, out _, out _, out _));
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions, new OccupancyService(maps), new HandsService(maps));
        server.Sent = (_, _) => { };
        var commands = new CommandHandler(sessions, maps, server);
        var feet = new Vector3(140, 0, 140);
        var body = world.Create(
            new PlayerComponent { ConnectionId = 1, Username = "cody" }, EntityType.Player,
            new Transform { Position = feet, Rotation = Quaternion.Identity }, new Velocity(),
            new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(0.6f, 1.8f, 0.6f), IsSolid = true });
        maps.IndexEntity("default", body);
        var me = new UserSession { ConnectionId = 1, Username = "cody", Entity = body, CurrentMapId = "default", Welcomed = true, Role = UserRole.Admin };
        sessions.AddSession(1, me);
        string Run(params string[] args)
        {
            var said = new List<string>();
            commands.HandleTextCommand(1, new TextCommand { Command = "spawn", Args = args }, m => { if (m is TextEvent t) said.Add(t.Text); });
            server.DrainCommandBuffer();
            return string.Join(" | ", said);
        }
        List<(Entity E, string Key, Vector3 At)> Fires()
        {
            var found = new List<(Entity, string, Vector3)>();
            world.Query(new QueryDescription().WithAll<Transform, SoundEmitterComponent>(), (Entity e, ref Transform t, ref SoundEmitterComponent em) =>
            {
                if (em.SoundId.StartsWith("fire:", StringComparison.Ordinal)) found.Add((e, em.SoundId, t.Position));
            });
            return found;
        }

        int before = Fires().Count;
        Assert.Contains("bonfire", Run("fire"));
        Assert.Contains("no fire called", Run("fire", "volcano"));
        string said = Run("fire", "bonfire");
        _o.WriteLine(said);
        var lit = Fires().Where(f => f.Key.Contains("/lit=")).ToList();
        var fire = Assert.Single(lit);
        FireSpec.ParseKey(fire.Key, out string preset, out double? at);
        Assert.Equal("bonfire", preset);
        Assert.InRange(WindField.Now() - at!.Value, -1, 60);
        // In front of you, clear of you.
        float ahead = Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(fire.At.X, fire.At.Z));
        Assert.InRange(ahead, 0.5f * FireSpec.Bonfire.AreaDepth + 1f, 10f);
        Assert.Contains("is out", Run("fire", "out"));
        Assert.Equal(before, Fires().Count);
        Assert.Contains("no fire lit", Run("fire", "out"));
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
