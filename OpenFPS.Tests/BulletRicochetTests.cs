using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The bullets, round two (Cody, 2026-10-04: a pistol's whizz was "a quick laser", and he expected a
/// ricochet): the whizz is broadband wake noise, a round skips off a hard face below its critical angle
/// and flies on tumbling, and a round striking something is its own event per material.
/// </summary>
public class BulletRicochetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-ricochet-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _o;
    public BulletRicochetTests(ITestOutputHelper o) { _o = o; }
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    private static readonly Air Still = Air.Standard;

    // ── The whizz ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A .45 going by 2 m away is a rush of noise, not a tone: its spectrum is flat enough over
    /// 0.5-16 kHz (the old 96 sine partials measured 0.06-0.19 on the same scenes) and nothing stands
    /// over its own third of an octave by more than chance peaks of noise do.
    /// </summary>
    [Theory]
    [InlineData(2f, 30f)]
    [InlineData(6f, 30f)]
    [InlineData(1f, 15f)]
    public void TheWhizzIsBroadbandNoiseWithNoDominantTone(float off, float downrange)
    {
        var w = WeaponRegistry.ServicePistol;
        var path = Flown(w, new Vector3(0f, 0f, 0.5f), Vector3.UnitZ);
        var ear = new Vector3(off, 0f, downrange);
        var sounds = BulletFlyby.Sounds(path, ear, w, Still);
        Assert.Equal(3, sounds.Count);
        var mix = Lay(sounds, ear);
        var (flatness, tone) = Spectrum.Tonality(mix, 48000);
        _o.WriteLine($"{off} m off at {downrange} m: flatness {flatness:F3}, strongest line {tone:F1} dB over its third-octave");
        Assert.True(flatness > 0.35f, $"flatness {flatness:F3}");
        Assert.True(tone < 14f, $"a line {tone:F1} dB over the noise");
    }

    [Fact]
    public void TheWakeSpectrumIsOneOverAllFrequencies()
    {
        float peak = 4400f, sum = 0f;
        for (float f = 10f; f < 400000f; f *= 1.001f) sum += BulletFlyby.WakeDensity(f, peak) * f * 0.001f;
        Assert.InRange(sum, 0.98f, 1.02f);
        Assert.InRange(BulletFlyby.WakeShareBelow(1e7f, peak), 0.98f, 1.0f);
        // The scale is a little under the hump's peak (0.89 of it), and the hump leans high: a quarter
        // of it lies under its scale.
        Assert.InRange(BulletFlyby.WakeShareBelow(peak, peak), 0.2f, 0.35f);
    }

    // ── Whether it ricochets ────────────────────────────────────────────────────────────────────

    /// <summary>A round skips off a hard face below its critical angle, and digs in above it, every time
    /// clear of the ±15 % the dice decide.</summary>
    [Theory]
    [InlineData("Concrete", 15f)]
    [InlineData("Metal", 25f)]
    [InlineData("Brick", 12f)]
    [InlineData("Asphalt", 10f)]
    public void ARoundRicochetsOnlyBelowTheFacesCriticalAngle(string material, float critical)
    {
        var slug = Slug.Of(WeaponRegistry.Glock);
        Assert.Equal(critical * MathF.PI / 180f, Ricochet.CriticalRadians(material, slug), 4);
        for (int seed = 0; seed < 30; seed++)
        {
            Assert.True(Ricochet.TryBounce(material, Incoming(0.6f * critical, 360f), Vector3.UnitY, slug, new Random(seed), out var o));
            Assert.False(Ricochet.TryBounce(material, Incoming(1.3f * critical, 360f), Vector3.UnitY, slug, new Random(seed), out _));
            // Off a hard face it leaves lower than it came, slower, and tumbling.
            float departure = MathF.Asin(Vector3.Normalize(o.Velocity).Y) * 180f / MathF.PI;
            Assert.InRange(departure, 0.2f, 0.6f * critical);
            Assert.InRange(o.Velocity.Length(), 0.5f * 360f, 0.97f * 360f);
            Assert.True(o.Slug.Tumbling);
            Assert.True(o.DumpedJoules > 0f);
        }
    }

    /// <summary>Water: Birkhoff's 18°/√(ρ_body/ρ_water), 5-7° for a lead-cored bullet; and a round off
    /// water leaves HIGHER than it came (it ploughs a ramp).</summary>
    [Fact]
    public void WaterSkipsBelowBirkhoffsAngleAndThrowsItUp()
    {
        var slug = Slug.Of(WeaponRegistry.ServicePistol);
        float critical = Ricochet.CriticalRadians("Water", slug) * 180f / MathF.PI;
        _o.WriteLine($".45 off water: critical {critical:F1} degrees (slug density {slug.Density:F0} kg/m3)");
        Assert.InRange(critical, 5f, 7f);
        Assert.True(Ricochet.TryBounce("Water", Incoming(3f, 250f), Vector3.UnitY, slug, new Random(1), out var o));
        Assert.True(MathF.Asin(Vector3.Normalize(o.Velocity).Y) * 180f / MathF.PI > 3f * 0.9f);
        Assert.False(Ricochet.TryBounce("Water", Incoming(9f, 250f), Vector3.UnitY, slug, new Random(1), out _));
    }

    /// <summary>Grass, wood, plaster, carpet and the like take the round in at any angle.</summary>
    [Theory]
    [InlineData("Grass")]
    [InlineData("Wood")]
    [InlineData("Plaster")]
    [InlineData("Carpet")]
    [InlineData("Foliage")]
    public void SoftStuffNeverThrowsARoundBack(string material)
    {
        var slug = Slug.Of(WeaponRegistry.Akm);
        Assert.Equal(0f, Ricochet.CriticalRadians(material, slug));
        for (int seed = 0; seed < 20; seed++)
            Assert.False(Ricochet.TryBounce(material, Incoming(1f, 700f), Vector3.UnitY, slug, new Random(seed), out _));
    }

    /// <summary>A tumbling slug slows far faster than the bullet it was: 50 m after skipping off concrete,
    /// a .45's slug has well under four fifths of the speed the bullet would have kept.</summary>
    [Fact]
    public void ATumblingSlugSlowsLikeABluffBody()
    {
        var w = WeaponRegistry.ServicePistol;
        Assert.True(Ricochet.TryBounce("Concrete", Incoming(8f, 250f), Vector3.UnitY, Slug.Of(w), new Random(3), out var o));
        var slug = new BulletState { Velocity = o.Velocity };
        var bullet = new BulletState { Velocity = o.Velocity };
        while (slug.Travelled < 50f) Ricochet.Advance(ref slug, 0.01f, o.Slug, Still, Vector3.Zero);
        while (bullet.Travelled < 50f) ExternalBallistics.Advance(ref bullet, 0.01f, ExternalBallistics.CoefficientOf(w), Still, Vector3.Zero);
        _o.WriteLine($"after 50 m: slug {slug.Velocity.Length():F0} m/s, bullet {bullet.Velocity.Length():F0} m/s (left at {o.Velocity.Length():F0})");
        Assert.True(slug.Velocity.Length() < 0.8f * bullet.Velocity.Length());
    }

    // ── On the server ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 9 mm fired down onto a concrete slab at 6 degrees skips off it, the shooter is told so by the
    /// slab's name, and the slug flies on and hits a person standing beyond, for less than the round
    /// would have done. Somebody beside the slab hears the strike and the whine.
    /// </summary>
    [Fact]
    public void ARicochetFliesOnAndCanHitSomebodyForLess()
    {
        var g = new Range(_dir, "default");
        g.Combat.Scatter = new Random(7);
        var slab = g.Maps.SpawnEntity(g.MapId, w => w.Create(
            new Transform { Position = new Vector3(140f, 0.25f, 160f), Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10f, 0.5f, 40f), IsSolid = true },
            new MaterialComponent { Material = "Concrete" },
            new NameComponent { Name = "test slab" },
            new IdentityComponent { Name = "test slab" }));
        var shooter = g.Player("shooter", new Vector3(140f, 0.5f, 140f));
        shooter.AimAssist = false;   // or the gun is turned onto the walker, and nothing skips
        g.Arm(shooter, "glock");
        float strikeZ = 140f + CombatService.HipHeight / MathF.Tan(6f * MathF.PI / 180f);
        var walker = g.Walker(new Vector3(140f, 0.5f, strikeZ + 2f));
        var beside = g.Player("beside", new Vector3(143f, 0.5f, strikeZ));
        g.AimAt(shooter, new Vector3(140f, 0.5f, strikeZ));

        g.Fire(shooter);
        g.Run(0.3);

        var said = g.Said(shooter);
        _o.WriteLine(string.Join(" | ", said));
        Assert.Contains("Ricochet off test slab.", said);
        var confirm = Assert.Single(g.Confirms(shooter));
        Assert.Equal(walker.Id, confirm.TargetEntityId);
        int health = g.World.Get<HealthComponent>(walker).Current;
        _o.WriteLine($"walker left at {health}");
        Assert.InRange(health, 100 - WeaponRegistry.Glock.Damage + 1, 99);

        var heard = g.SentTo(beside).OfType<WorldAudioEvent>().SelectMany(e => e.Sounds).Select(s => s.SynthKey).ToList();
        _o.WriteLine(string.Join("\n", heard));
        Assert.Contains(heard, k => k.StartsWith(BulletImpact.HitPrefix + "Concrete:", StringComparison.Ordinal));
        Assert.Contains(heard, k => k.StartsWith(Ricochet.WhinePrefix, StringComparison.Ordinal));
        _ = slab;
    }

    /// <summary>The same round fired down at 40 degrees does not skip: it stops in the slab.</summary>
    [Fact]
    public void ASteepShotStopsInTheSlab()
    {
        var g = new Range(_dir, "default");
        g.Maps.SpawnEntity(g.MapId, w => w.Create(
            new Transform { Position = new Vector3(140f, 0.25f, 160f), Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10f, 0.5f, 40f), IsSolid = true },
            new MaterialComponent { Material = "Concrete" },
            new NameComponent { Name = "test slab" }));
        var shooter = g.Player("shooter", new Vector3(140f, 0.5f, 140f));
        g.Arm(shooter, "glock");
        g.AimAt(shooter, new Vector3(140f, 0.5f, 140f + CombatService.HipHeight / MathF.Tan(40f * MathF.PI / 180f)));
        g.Fire(shooter);
        g.Run(0.2);
        Assert.Equal("Hit test slab at 2 metres.", Assert.Single(g.Said(shooter)));
        var keys = g.SentTo(shooter).OfType<WorldAudioEvent>().SelectMany(e => e.Sounds).Select(s => s.SynthKey).ToList();
        Assert.Contains(keys, k => k.StartsWith(BulletImpact.HitPrefix + "Concrete:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith(Ricochet.WhinePrefix, StringComparison.Ordinal));
    }

    // ── Impacts ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Concrete", BulletImpact.Kind.Brittle)]
    [InlineData("Brick", BulletImpact.Kind.Brittle)]
    [InlineData("Asphalt", BulletImpact.Kind.Brittle)]
    [InlineData("Marble", BulletImpact.Kind.Brittle)]
    [InlineData("Metal", BulletImpact.Kind.Metal)]
    [InlineData("Wood", BulletImpact.Kind.Wood)]
    [InlineData("Plaster", BulletImpact.Kind.Panel)]
    [InlineData("Dirt", BulletImpact.Kind.Soil)]
    [InlineData("Grass", BulletImpact.Kind.Soil)]
    [InlineData("Gravel", BulletImpact.Kind.Soil)]
    [InlineData("Water", BulletImpact.Kind.Water)]
    [InlineData("Carpet", BulletImpact.Kind.Soft)]
    public void EachMaterialHasItsOwnStrikeAtTheConventionsLevel(string material, BulletImpact.Kind kind)
    {
        Assert.Equal(kind, BulletImpact.KindOf(material));
        var w = WeaponRegistry.Glock;
        bool wall = kind is BulletImpact.Kind.Brittle or BulletImpact.Kind.Metal or BulletImpact.Kind.Wood or BulletImpact.Kind.Panel
                    && material != "Asphalt";
        var size = kind == BulletImpact.Kind.Metal ? new Vector3(1f, 1f, 0.006f) : new Vector3(2f, 3f, 0.12f);
        var hit = BulletImpact.From(material, 360f, w, wall ? MathF.PI / 2f : MathF.PI / 4f, 0f, size, "Concrete", wall ? 1.2f : 0f);
        string key = BulletImpact.HitKey(hit);
        Assert.StartsWith(BulletImpact.HitPrefix + material + ":", key);
        Assert.True(BulletImpact.TryParseHit(key, out var back));
        Assert.Equal(hit, back);

        var sounds = BulletImpact.Sounds(hit, Vector3.Zero, Vector3.Zero);
        var strike = sounds[0];
        // The declared level is what the buffer's full scale stands for, at a metre.
        Assert.Equal(BulletFlyby.Spl(BulletImpact.HitFullScalePascals(hit)), strike.LevelDb, 3);
        for (int seed = 0; seed < 4; seed++)
        {
            var pcm = BulletImpact.RenderHit(hit, 48000, seed, out float raw);
            Assert.All(pcm, v => Assert.True(float.IsFinite(v)));
            _o.WriteLine($"{material} seed {seed}: {strike.LevelDb:F0} dB, {pcm.Length / 48.0:F0} ms, peak {raw:F2} of full scale");
            Assert.InRange(raw, 0.2f, 1.0f);
        }
        if (sounds.Count > 1)
        {
            Assert.True(BulletImpact.TryParseDebris(sounds[1].SynthKey, out var debris));
            Assert.Equal(BulletFlyby.Spl(BulletImpact.DebrisFullScalePascals(debris)), sounds[1].LevelDb, 3);
            var pcm = BulletImpact.RenderDebris(debris, 48000, 1, out float raw);
            _o.WriteLine($"  debris {sounds[1].LevelDb:F0} dB, {pcm.Length / 48.0:F0} ms, peak {raw:F2}");
            Assert.InRange(raw, 0.1f, 1.0f);
        }
    }

    /// <summary>A strike's character follows its stuff: steel rings for long after the crack, soil and
    /// carpet have almost no crack at all, and water's sound is mostly after the entry.</summary>
    [Fact]
    public void SteelRingsAndSoilThuds()
    {
        var w = WeaponRegistry.Glock;
        var steel = BulletImpact.From("Metal", 360f, w, MathF.PI / 2f, 0f, new Vector3(1f, 1f, 0.006f), "Concrete", 1.2f);
        var dirt = BulletImpact.From("Dirt", 360f, w, MathF.PI / 4f, 0f, new Vector3(10f, 10f, 1f), "Dirt", 0f);
        var concrete = BulletImpact.From("Concrete", 360f, w, MathF.PI / 2f, 0f, new Vector3(2f, 3f, 0.2f), "Concrete", 1.2f);
        float Tail(float[] pcm, float after) { int i = (int)(after * 48000); return i >= pcm.Length ? 0f : pcm[i..].Max(MathF.Abs); }
        var s = BulletImpact.RenderHit(steel, 48000, 1);
        var c = BulletImpact.RenderHit(concrete, 48000, 1);
        _o.WriteLine($"steel after 100 ms {Tail(s, 0.1f):F3}, concrete {Tail(c, 0.1f):F3}");
        Assert.True(Tail(s, 0.1f) > 3f * Tail(c, 0.1f));
        Assert.True(BulletImpact.CrackPascals(dirt) < 0.01f * BulletImpact.CrackPascals(concrete));
    }

    /// <summary>
    /// The strike's peak checked by a second road: a point force F on a plate of surface mass ρh radiates
    /// ρ0·F/(2π·ρh·r) (Cremer and Heckl, the infinite plate below coincidence). With F the 9 mm's peak
    /// force, π·I/2τ, that is 120 dB at a metre off a 20 cm concrete wall and 141 off 6 mm steel; the
    /// model's declared levels (the crack, the plate's modes summed with their own signs, the rest) land
    /// within 6 dB of it. Summed in step the steel came out at 162 and the concrete at 138, and both were
    /// heard no quieter at 30 m than at 5 (the coordinator, 2026-10-04).
    /// </summary>
    [Theory]
    [InlineData("Concrete", 2400f, 0.2f)]
    [InlineData("Metal", 7850f, 0.006f)]
    public void AStrikesPeakAgreesWithAPointForceOnAPlate(string material, float density, float thickness)
    {
        var hit = BulletImpact.From(material, 365f, WeaponRegistry.Glock, MathF.PI / 2f, 0f,
                                    new Vector3(2f, 3f, thickness), "Concrete", 1.2f);
        float force = MathF.PI * hit.Impulse / (2f * BulletImpact.ContactSeconds(hit));
        float heckl = 1.2f * force / (2f * MathF.PI * density * thickness);
        float declared = BulletImpact.Sounds(hit, Vector3.Zero, Vector3.Zero)[0].LevelDb;
        var (crack, ring, efficiency) = BulletImpact.Radiated(hit);
        _o.WriteLine($"{material}: point force {force / 1000f:F0} kN, {BulletFlyby.Spl(heckl):F0} dB by the plate law, declared {declared:F0} dB; sound {crack + ring:E1} J, {efficiency:E1} of the energy");
        Assert.InRange(declared - BulletFlyby.Spl(heckl), -6f, 6f);
        Assert.True(efficiency < 3e-3f);
    }

    /// <summary>
    /// A splash is a rush, not a tinkle (Cody, 2026-10-04: "the water doesn't sound like it is splashing,
    /// it's just tinkling"): thousands of drops falling back, each a broadband splat, one in twenty
    /// ringing a bubble. Measured on the fall-back alone it is flat noise with no line standing out;
    /// it was a few dozen drops, a third of them ringing, flatness 0.05-0.10.
    /// </summary>
    [Theory]
    [InlineData("glock", 10f)]
    [InlineData("akm", 30f)]
    public void AWaterStrikeSplashesRatherThanTinkles(string weaponId, float metres)
    {
        var w = WeaponRegistry.Get(weaponId)!;
        float speed = ExternalBallistics.Fly(w, metres, 0f, 0f, Air.Standard, Vector3.Zero).Speed;
        var hit = BulletImpact.From("Water", speed, w, MathF.PI / 4f, 0f, new Vector3(10f, 10f, 1f), "Water", 0f);
        var drops = BulletImpact.Splash(hit);
        Assert.InRange(drops.Count, 300, BulletImpact.MaxRenderedDrops);
        var pcm = BulletImpact.RenderDebris(hit, 48000, 1, out float raw);
        Assert.InRange(raw, 0.1f, 1f);
        var (flatness, tone) = Spectrum.Tonality(pcm, 48000);
        _o.WriteLine($"{weaponId}: {drops.Count} drops rendered (each for {drops[0].Weight * drops[0].Weight:F1}), {pcm.Length / 48.0:F0} ms, flatness {flatness:F3}, strongest line {tone:F1} dB");
        Assert.True(flatness > 0.35f, $"flatness {flatness:F3}");
        Assert.True(tone < 14f, $"a line {tone:F1} dB over the noise");
    }

    // ── The whine ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The whine as heard from the side: three pieces at the whine's level, rendered within
    /// full scale, a line in it (the tumble) that falls in pitch as the slug slows and goes away.</summary>
    [Fact]
    public void TheWhineIsATumblingToneThatFalls()
    {
        var w = WeaponRegistry.ServicePistol;
        Assert.True(Ricochet.TryBounce("Concrete", Incoming(8f, 250f), Vector3.UnitY, Slug.Of(w), new Random(2), out var o));
        var ear = new Vector3(10f, 1.7f, 0f);
        var sounds = Ricochet.WhineSounds(Vector3.Zero, o.Velocity, o.Slug, 100f, 0.1f, ear, Still);
        Assert.NotEmpty(sounds);
        var renders = new List<float[]>();
        foreach (var s in sounds)
        {
            Assert.True(Ricochet.TryParseWhine(s.SynthKey, out var key));
            Assert.Equal(BulletFlyby.Spl(Ricochet.WhineFullScalePascals(key)), s.LevelDb, 3);
            var pcm = Ricochet.RenderWhine(key, 48000, 1, out float raw);
            Assert.True(raw <= 1.0f, $"raw peak {raw:F2}");
            renders.Add(pcm);
        }
        var last = renders[^1];
        float early = ZeroCrossingHz(last[..(last.Length / 5)]), late = ZeroCrossingHz(last[(4 * last.Length / 5)..]);
        _o.WriteLine($"whine: {renders.Count} pieces, last piece {last.Length / 48.0:F0} ms, early ~{early:F0} Hz, late ~{late:F0} Hz");
        Assert.True(early > 1.2f * late);
        // Tonal, unlike the whizz: a line well over its third of an octave.
        var (_, tone) = Spectrum.Tonality(last[..Math.Min(last.Length, 9600)], 48000, 300f, 12000f);
        Assert.True(tone > 15f, $"tone {tone:F1} dB");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static Vector3 Incoming(float degrees, float speed)
    {
        float a = degrees * MathF.PI / 180f;
        return new Vector3(0f, -MathF.Sin(a), MathF.Cos(a)) * speed;
    }

    private static float ZeroCrossingHz(float[] pcm)
    {
        int crossings = 0;
        for (int i = 1; i < pcm.Length; i++) if ((pcm[i - 1] < 0f) != (pcm[i] < 0f)) crossings++;
        return crossings * 48000f / (2f * MathF.Max(1, pcm.Length));
    }

    private static List<FlightSample> Flown(WeaponDefinition w, Vector3 from, Vector3 dir)
    {
        var s = new BulletState { Position = from, Velocity = Vector3.Normalize(dir) * w.MuzzleVelocity };
        var path = new List<FlightSample> { new(0f, s.Position, s.Velocity) };
        float bc = ExternalBallistics.CoefficientOf(w);
        while (s.Seconds < 2f)
        {
            ExternalBallistics.Advance(ref s, 0.01f, bc, Still, Vector3.Zero);
            path.Add(new FlightSample(s.Seconds, s.Position, s.Velocity));
        }
        return path;
    }

    /// <summary>The sounds laid down as they arrive at the ear, by the inverse law from where each is
    /// placed (the rendered buffers carry each piece back to a metre).</summary>
    private static float[] Lay(List<TransientSound> sounds, Vector3 ear)
    {
        var laid = new List<(float At, float[] Pcm, float Gain)>();
        foreach (var s in sounds)
        {
            Assert.True(BulletFlyby.TryParseWhizz(s.SynthKey, out var w));
            float r = Vector3.Distance(ear, s.Position);
            laid.Add((s.DelaySeconds + r / Still.SpeedOfSound, BulletFlyby.RenderWhizz(w, 48000, 1),
                      MathF.Pow(10f, s.LevelDb / 20f) * 20e-6f / MathF.Max(1f, r)));
        }
        float first = laid.Min(l => l.At), end = laid.Max(l => l.At + l.Pcm.Length / 48000f);
        var mix = new float[(int)((end - first) * 48000) + 1];
        foreach (var (at, pcm, gain) in laid)
        {
            int o = (int)((at - first) * 48000);
            for (int i = 0; i < pcm.Length && o + i < mix.Length; i++) mix[o + i] += pcm[i] * gain;
        }
        return mix;
    }

    /// <summary>A real map with the real combat service, its clock in the test's hand and no scatter.</summary>
    private sealed class Range
    {
        public readonly MapManager Maps;
        public readonly HandsService Hands;
        public readonly SessionManager Sessions = new();
        public readonly GameServer Server;
        public readonly CombatService Combat;
        public readonly string MapId;
        public World World = null!;
        public SpatialGrid<Entity> Grid = null!;
        public double Now = 100;
        public readonly List<(UserSession To, IMessage Message)> ServerSent = new();
        private int _next = 1;

        public Range(string dir, string mapId)
        {
            MapId = mapId;
            string mapDir = Path.Combine(dir, Guid.NewGuid().ToString("N"), "maps");
            Directory.CreateDirectory(mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", mapId + ".json"), Path.Combine(mapDir, mapId + ".json"));
            Maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            Maps.Initialize();
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out Grid, out _));
            Hands = new HandsService(Maps);
            Server = new GameServer(new NoUsers());
            Server.Attach(Maps, Sessions, new OccupancyService(Maps), Hands);
            Server.Sent = (to, m) => ServerSent.Add((to, m));
            Combat = new CombatService(Maps, Server, Sessions) { Clock = () => Now, HipDispersionRadians = 0f };
        }

        public UserSession Player(string name, Vector3 feet)
        {
            int connection = _next++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = name },
                EntityType.Player,
                new Transform { Position = feet, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = name },
                new HealthComponent { Current = 100, Max = 100 },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = connection, Username = name, Entity = entity, CurrentMapId = MapId, Welcomed = true };
            Sessions.AddSession(connection, session);
            return session;
        }

        public void Face(UserSession who, float yaw, float pitch)
            => World.Get<Transform>(who.Entity).Rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f);

        /// <summary>Points a player's gun at a point, from the hip. Increasing pitch looks down.</summary>
        public void AimAt(UserSession who, Vector3 point)
        {
            Vector3 to = point - (World.Get<Transform>(who.Entity).Position + new Vector3(0f, CombatService.HipHeight, 0f));
            Face(who, MathF.Atan2(to.X, to.Z), -MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length()));
        }

        public Entity Walker(Vector3 at) => Maps.SpawnEntity(MapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false },
            new NameComponent { Name = "someone walking" },
            new IdentityComponent { Name = "someone walking" },
            new Pedestrian { Voice = "", Pair = "" },
            new HealthComponent { Current = 100, Max = 100 }));

        public Entity Arm(UserSession who, string weaponId)
        {
            var w = WeaponRegistry.Get(weaponId)!;
            var gun = Maps.SpawnEntity(MapId, wd => wd.Create(
                new Transform { Position = World.Get<Transform>(who.Entity).Position + new Vector3(0.3f, 0, 0), Rotation = Quaternion.Identity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.1f, 0.1f, 1f), IsSolid = false },
                new MaterialComponent { Material = "Metal" },
                new IdentityComponent { Name = w.DisplayName },
                new ItemComponent { MassKg = 3.5f, Hands = 2, WeaponId = weaponId },
                EntityType.Item));
            Assert.True(Hands.Take(who, w.DisplayName, out string message), message);
            return gun;
        }

        public void Fire(UserSession who)
        {
            RefreshGrid();
            Combat.Fire(who, Array.Empty<string>(), m => ServerSent.Add((who, m)), canNameAny: false);
        }

        public void Run(double seconds)
        {
            int ticks = (int)Math.Ceiling(seconds / PhysicsConstants.FixedDeltaTime - 1e-6);
            for (int i = 0; i < ticks; i++)
            {
                Now += PhysicsConstants.FixedDeltaTime;
                RefreshGrid();
                Combat.Update(MapId, World);
            }
        }

        private void RefreshGrid()
        {
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
        }

        public IEnumerable<IMessage> SentTo(UserSession who) => ServerSent.Where(s => s.To == who).Select(s => s.Message);
        public List<string> Said(UserSession who) => SentTo(who).OfType<TextEvent>().Select(t => t.Text).ToList();
        public List<HitConfirm> Confirms(UserSession who) => SentTo(who).OfType<HitConfirm>().ToList();
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
