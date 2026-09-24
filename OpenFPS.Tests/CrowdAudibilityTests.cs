using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The stand is full of people and you cannot hear them.
///
/// Three separate things had to be true at once for that, and only the last one is about the crowd:
///   * a transient's range was capped at 250 m, and the mixer fades a voice to nothing over the last
///     quarter of its range — so everything past 187 m was on its way out for being far away, and the
///     grandstand is 219 m from where you land;
///   * a crowd was placed as a POINT, with the reference distance of a firework, while the cars beside
///     it are placed at their own size — so the two were not being compared on the same terms;
///   * and every reaction rendered a new three-second buffer and registered a new sound with the
///     mixer, ninety a minute, because nothing quantised the key.
/// </summary>
public class CrowdAudibilityTests
{
    private readonly ITestOutputHelper _o;
    public CrowdAudibilityTests(ITestOutputHelper o) => _o = o;

    private static (MapManager Manager, World World, MapData Data, Dictionary<int, Entity> Lookup) LoadRace()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        var vehicles = new VehicleSystem();
        vehicles.Spawn(manager);
        manager.RefreshEarshotRanges();
        Assert.True(manager.TryGetMap("speedway", out World world, out _, out _, out var lookup));
        var data = manager.GetAllMaps().First(kv => kv.Key == "speedway").Value.data;

        // Sixty seconds of the real race, so the crowd is reacting to real cars at real speeds.
        CrowdSystem.Reset();
        float dt = 1f / 30f;
        for (int tick = 0; tick < 30 * 60; tick++) vehicles.Update("speedway", world, dt);
        return (manager, world, data, lookup);
    }

    private static List<(Vector3 At, CrowdApplause Spec)> ReactionsOverASecond(World world, double now, string mapId = "speedway")
    {
        var got = new List<(Vector3, CrowdApplause)>();
        for (int tick = 0; tick < 30; tick++)
            CrowdSystem.Update(mapId, world, now + tick / 30.0, (id, at, spec) => got.Add((at, spec)));
        return got;
    }

    /// <summary>The crowd reacts to the field that is actually on the track — nobody had ever checked.</summary>
    [Fact]
    public void TheCrowdReactsToTheRealField()
    {
        var (_, world, _, _) = LoadRace();
        CrowdSystem.Reset();
        var reactions = ReactionsOverASecond(world, 1000.0);
        Assert.NotEmpty(reactions);
        _o.WriteLine($"{reactions.Count} blocks reacted within a second of a real race");
    }

    /// <summary>
    /// Each map is its own world and entity ids repeat between them, so a crowd's cooldown is kept per
    /// map. Keyed by id alone, one map's grandstand reacting silenced the other's with the same id.
    /// </summary>
    [Fact]
    public void TwoMapsDoNotShareACrowdsCooldown()
    {
        var (_, world, _, _) = LoadRace();
        CrowdSystem.Reset();
        var here = ReactionsOverASecond(world, 3000.0, "speedway");
        var there = ReactionsOverASecond(world, 3000.0, "speedway-copy");
        _o.WriteLine($"{here.Count} reactions on one map, {there.Count} on the other at the same moment");
        Assert.NotEmpty(here);
        Assert.Equal(here.Count, there.Count);
    }

    /// <summary>
    /// A crowd carries its own size, and it is the size of the patch the people fill.
    /// </summary>
    [Fact]
    public void ACrowdIsNotAPoint()
    {
        // 400 people at half a square metre each fill 200 m²: a patch about eight metres across.
        float radius = Applause.SpreadRadiusMetres(400);
        Assert.InRange(radius, 6f, 10f);
        // And it grows with the square root of the head count, not with the head count.
        Assert.InRange(Applause.SpreadRadiusMetres(1600) / radius, 1.9f, 2.1f);
    }

    /// <summary>
    /// The one that matters: a full stand across the infield is heard at about the level it really has
    /// relative to the cars in front of it.
    ///
    /// Both sides are worked out the way the mixer works them out — <see cref="Loudness.RenderedGain"/>
    /// is the law the provider applies — and compared against the physics, which is just the inverse
    /// square law from each source's own level. Before this, the crowd came out sixteen decibels under
    /// where the physics puts it, which is the difference between "there is a crowd over there" and
    /// nothing at all.
    /// </summary>
    [Fact]
    public void AStandAcrossTheInfieldIsHeardAgainstTheCars()
    {
        var (_, world, data, _) = LoadRace();
        CrowdSystem.Reset();
        var reactions = ReactionsOverASecond(world, 2000.0);
        Assert.NotEmpty(reactions);

        var spawn = data.SpawnPoint.Position;
        var (at, spec) = reactions.OrderBy(r => Vector3.Distance(r.At, spawn)).First();
        float crowdDist = Vector3.Distance(at, spawn);
        float crowdLevel = Applause.LevelDb(spec.Clappers, spec.Intensity);
        float crowdExtent = Applause.SpreadRadiusMetres(spec.Clappers);

        var crowdPlaced = Loudness.Place(crowdLevel, crowdExtent);
        float crowdRendered = Loudness.RenderedGain(crowdPlaced.Gain, crowdPlaced.ReferenceDistance,
                                                    Loudness.AudibleRange(crowdLevel), crowdDist);

        // A motorcycle on the track in front of you, placed the way ClientAudioSystem places one: its
        // own measured level, and its own size as the reference distance.
        var bike = VehicleProfile.ByName("sportbike");
        float bikeDist = 60f;
        var bikePlaced = Loudness.Place(bike.SourceLevelDb);
        float bikeRendered = Loudness.RenderedGain(bikePlaced.Gain,
            MathF.Max(bikePlaced.ReferenceDistance, 3f),
            Loudness.AudibleRange(bike.SourceLevelDb), bikeDist);

        float renderedDelta = 20f * MathF.Log10(crowdRendered / bikeRendered);
        float physicalDelta = Loudness.SplAt(crowdLevel, crowdDist) - Loudness.SplAt(bike.SourceLevelDb, bikeDist);

        _o.WriteLine($"crowd {spec.Clappers} people, {crowdLevel:F1} dB, {crowdExtent:F1} m across, {crowdDist:F0} m away");
        _o.WriteLine($"  rendered {20 * MathF.Log10(crowdRendered):F1} dBFS   physical {Loudness.SplAt(crowdLevel, crowdDist):F1} dB SPL");
        _o.WriteLine($"bike {bike.SourceLevelDb:F1} dB at {bikeDist:F0} m");
        _o.WriteLine($"  rendered {20 * MathF.Log10(bikeRendered):F1} dBFS   physical {Loudness.SplAt(bike.SourceLevelDb, bikeDist):F1} dB SPL");
        _o.WriteLine($"crowd relative to bike: rendered {renderedDelta:F1} dB, physical {physicalDelta:F1} dB");

        Assert.True(MathF.Abs(renderedDelta - physicalDelta) < 4f,
            $"the crowd is rendered {renderedDelta:F1} dB against the bike where the physics says " +
            $"{physicalDelta:F1} dB — {MathF.Abs(renderedDelta - physicalDelta):F1} dB out.");
    }

    /// <summary>
    /// A stand at the far side of a map must not be faded out for being far away. The mixer fades a
    /// voice over the last quarter of its range, so the range has to be what the level can carry.
    /// </summary>
    [Fact]
    public void ACrowdIsNotFadedOutForBeingAcrossTheTrack()
    {
        float level = Applause.LevelDb(300, 0.7f);
        float range = Loudness.AudibleRange(level);
        float fadeStartsAt = range - 0.25f * (range - Applause.SpreadRadiusMetres(300));
        _o.WriteLine($"a 300-person crowd is {level:F1} dB, carries {range:F0} m, starts fading at {fadeStartsAt:F0} m");
        Assert.True(fadeStartsAt > 500f,
            $"a crowd this loud starts fading at {fadeStartsAt:F0} m, which is inside a racetrack.");
    }

    /// <summary>
    /// A rendered crowd is a cached buffer, so two crowds that differ by a person are one sound.
    ///
    /// What this is NOT is a dramatic saving. The first version of this test counted distinct keys
    /// over a minute of racing and asserted they were few — which they are, quantised or not, because
    /// the intensity is built from two SATURATING terms and a steady race lands on the same handful of
    /// values either way. Measured: about fifteen distinct keys in a minute with the quantiser, and
    /// twenty-two across the whole (cars nearby, fastest car) grid without it. The claim that every
    /// reaction rendered a fresh buffer was wrong, and the test that was supposed to protect it passed
    /// whatever the quantiser did.
    ///
    /// So it asserts the property instead: two crowds a person apart are the same buffer, and a crowd
    /// twice the size is not.
    /// </summary>
    [Fact]
    public void TwoCrowdsThatDifferByAPersonAreOneSound()
    {
        string a = Applause.Key(Applause.Quantise(new CrowdApplause(317, 0.731f, 3.81f)));
        string b = Applause.Key(Applause.Quantise(new CrowdApplause(319, 0.734f, 3.83f)));
        _o.WriteLine($"{a}  vs  {b}");
        Assert.Equal(a, b);

        string twiceAsMany = Applause.Key(Applause.Quantise(new CrowdApplause(640, 0.73f, 3.8f)));
        Assert.NotEqual(a, twiceAsMany);
    }
}

/// <summary>
/// The stand answers the people sitting on it.
///
/// "A cheer arriving off the deck a beat after the direct sound is most of what makes a stand sound
/// occupied" has been in the todo since the crowd was written, and the reason it never happened is
/// that reflections lived inside the ENGINE echo system and a crowd is not an engine. It is the same
/// geometry either way — a wall does not care what made the sound — so the image-source search that
/// answers a car now answers anything the world reports, and a transient's echo is simply the same
/// sound queued again for the moment it arrives.
/// </summary>
public class CrowdReflectionTests
{
    private readonly ITestOutputHelper _o;
    public CrowdReflectionTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void ACheerComesBackOffTheBackOfTheStand()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        Assert.True(manager.TryGetMap("speedway", out World world, out _, out _, out _));
        var data = manager.GetAllMaps().First(kv => kv.Key == "speedway").Value.data;

        // The reflecting faces of the map, built the way EngineReflections builds them.
        var surfaces = new List<ReflectingSurface>();
        var six = new ReflectingSurface[6];
        int id = 1;
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                if (!c.IsSolid || c.Shape != ColliderShape.Box) return;
                if (c.Size.X <= 0 || c.Size.Y <= 0 || c.Size.Z <= 0) return;
                string material = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : "Generic";
                float absorption = AcousticRegistry.GetProperties(material).Absorption;
                int n = ImageSource.FacesOfBox(t.Position, c.Size, t.Rotation, absorption, id, six);
                id += 6;
                for (int i = 0; i < n; i++) surfaces.Add(six[i]);
            });
        _o.WriteLine($"{surfaces.Count} reflecting faces on the speedway");
        Assert.NotEmpty(surfaces);

        // A crowd in the middle of the stand, and a listener on the racing line in front of it.
        Vector3 crowd = default;
        bool found = false;
        world.Query(new QueryDescription().WithAll<Transform, CrowdComponent>(), (Entity e, ref Transform t, ref CrowdComponent c) =>
        {
            if (!found || MathF.Abs(t.Position.X) < MathF.Abs(crowd.X)) { crowd = t.Position; found = true; }
        });
        Assert.True(found, "the speedway has no crowd in it.");

        var line = data.Tracks![0].Waypoints.Select(w => new Vector3(w.X, w.Y + 1.7f, w.Z)).ToList();
        var listener = line.OrderBy(w => Vector3.Distance(w, crowd)).First();

        Span<Reflection> echoes = stackalloc Reflection[4];
        int count = ImageSource.FirstOrder(surfaces.ToArray(), crowd, listener,
                                           OpenFPS.Client.AudioEngine.Core.AudioPhysics.SpeedOfSound, echoes);
        _o.WriteLine($"crowd at {crowd}, listener on the line at {listener} ({Vector3.Distance(crowd, listener):F0} m)");
        for (int i = 0; i < count; i++)
            _o.WriteLine($"  echo {i}: +{echoes[i].DelaySeconds * 1000f:F0} ms, path {echoes[i].PathLength:F0} m, " +
                         $"gain {echoes[i].Gain:F3}, off {echoes[i].BouncePoint}");

        Assert.True(count > 0, "nothing in the map reflects a cheer back to the track.");
        bool offTheBack = false;
        for (int i = 0; i < count; i++)
            if (echoes[i].BouncePoint.Z < crowd.Z && echoes[i].Gain >= ImageSource.EchoAudibleRatio) offTheBack = true;
        Assert.True(offTheBack, "no reflection comes off the wall BEHIND the seats, which is the one that matters.");

        // And your own footsteps, standing in the same place, get nothing — because the echo of a
        // sound a metre away off a wall fifty metres away is forty decibels down, and forty decibels
        // down is not an echo, it is an artefact. Same rule, same geometry, opposite answer.
        Vector3 underfoot = listener - new Vector3(0, 1.7f, 0);
        Span<Reflection> steps = stackalloc Reflection[4];
        int stepEchoes = ImageSource.FirstOrder(surfaces.ToArray(), underfoot, listener,
                                                OpenFPS.Client.AudioEngine.Core.AudioPhysics.SpeedOfSound, steps);
        int audible = 0;
        for (int i = 0; i < stepEchoes; i++)
        {
            _o.WriteLine($"  footstep echo {i}: +{steps[i].DelaySeconds * 1000f:F0} ms, gain {steps[i].Gain:F4} " +
                         $"({20f * MathF.Log10(MathF.Max(1e-6f, steps[i].Gain)):F0} dB under the step)");
            if (steps[i].Gain >= ImageSource.EchoAudibleRatio) audible++;
        }
        Assert.Equal(0, audible);
    }
}

/// <summary>
/// A surface that scatters answers with a wash, not a copy.
///
/// The grandstand was a flat concrete slab, so the applause came back off it as an exact copy of
/// itself at nearly the level of the direct sound — "the clapping reflections are crisp and they
/// shouldn't be". What a grandstand actually presents to a racetrack is tiered seating with people
/// in it: the most absorbent and the most scattering thing in ordinary acoustics. That is a MATERIAL,
/// so the retaining wall beside the track is unaffected and still slaps.
/// </summary>
public class ScatteringTests
{
    private readonly ITestOutputHelper _o;
    public ScatteringTests(ITestOutputHelper o) => _o = o;

    private static ReflectingSurface Wall(string material, float halfWide, float halfHigh, float z)
    {
        var p = AcousticRegistry.GetProperties(material);
        // Facing +Z: the source and the listener both stand in front of it.
        return new ReflectingSurface(new Vector3(0, halfHigh, z), Vector3.UnitZ,
                                     new Vector3(halfWide, 0, 0), new Vector3(0, halfHigh, 0),
                                     p.Absorption, 1, p.Scattering);
    }

    /// <summary>The same wall, the same geometry, two materials.</summary>
    [Fact]
    public void ASlabMirrorsAndAStandFullOfPeopleScatters()
    {
        // In front of the wall and low enough that the mirror image lands on it — otherwise the slab
        // has no specular path either and the comparison measures nothing.
        var source = new Vector3(0, 6f, -10f);
        var listener = new Vector3(0, 1.7f, 300f);     // across the circuit
        Span<Reflection> got = stackalloc Reflection[6];

        int nSlab = ImageSource.FirstOrder(new[] { Wall("Concrete", 20f, 4f, -20f) },
                                           source, listener, 343f, got, null, 2);
        float slabSpecular = 0f, slabDiffuse = 0f;
        for (int i = 0; i < nSlab; i++)
            if (got[i].IsDiffuse) slabDiffuse += got[i].Gain; else slabSpecular += got[i].Gain;

        int nStand = ImageSource.FirstOrder(new[] { Wall("Audience", 20f, 4f, -20f) },
                                            source, listener, 343f, got, null, 2);
        float standSpecular = 0f, standDiffuse = 0f;
        for (int i = 0; i < nStand; i++)
            if (got[i].IsDiffuse) standDiffuse += got[i].Gain; else standSpecular += got[i].Gain;

        _o.WriteLine($"concrete slab: specular {slabSpecular:F3}, scattered {slabDiffuse:F3}");
        _o.WriteLine($"full stand:    specular {standSpecular:F3}, scattered {standDiffuse:F3}");

        // A slab is a mirror: nearly all of what it returns is the image.
        Assert.True(slabSpecular > slabDiffuse * 2f);
        // A stand is not: what little it returns comes back scattered.
        Assert.True(standDiffuse > standSpecular);
        // And it returns much less of it either way.
        Assert.True(standSpecular < slabSpecular * 0.25f,
            $"a stand full of people reflected {standSpecular:F3} where a slab reflected {slabSpecular:F3}.");
    }

    /// <summary>A scattered arrival comes from the surface, not from a point behind it.</summary>
    [Fact]
    public void AScatteredArrivalComesFromTheWallItself()
    {
        var wall = Wall("Audience", 20f, 4f, -20f);
        var source = new Vector3(0, 13.5f, -10f);
        // Off the wall's axis of symmetry, or the two taps are the same distance away and the spread
        // that makes a wash is genuinely zero — which is correct, and measures nothing.
        var listener = new Vector3(90f, 1.7f, 300f);
        Span<Reflection> got = stackalloc Reflection[6];
        int n = ImageSource.FirstOrder(new[] { wall }, source, listener, 343f, got, null, 2);

        int diffuse = 0;
        var arrivalTimes = new List<float>();
        for (int i = 0; i < n; i++)
        {
            if (!got[i].IsDiffuse) continue;
            diffuse++;
            arrivalTimes.Add(got[i].DelaySeconds);
            // On the wall's own plane, not mirrored through it.
            Assert.True(MathF.Abs(got[i].ApparentPosition.Z - wall.Centre.Z) < 0.01f,
                $"a scattered arrival is at {got[i].ApparentPosition}, which is not on the surface.");
        }
        Assert.Equal(2, diffuse);
        // Two taps across the face arrive at different times — that spread is what makes it a wash.
        Assert.NotEqual(arrivalTimes[0], arrivalTimes[1]);
        _o.WriteLine($"two taps arrive {MathF.Abs(arrivalTimes[0] - arrivalTimes[1]) * 1000f:F1} ms apart");
    }

    /// <summary>The speedway's stand is seating; its retaining walls are still concrete.</summary>
    [Fact]
    public void TheStandIsSeatingAndTheWallsAreNot()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        Assert.True(manager.TryGetMap("speedway", out World world, out _, out _, out _));

        int audience = 0, concrete = 0;
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent, MaterialComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c, ref MaterialComponent m) =>
            {
                if (!c.IsSolid) return;
                if (m.Material.Equals("Audience", StringComparison.OrdinalIgnoreCase)) audience++;
                if (m.Material.Equals("Concrete", StringComparison.OrdinalIgnoreCase)) concrete++;
            });

        _o.WriteLine($"{audience} seating box(es), {concrete} concrete one(s)");
        Assert.True(audience >= 12, "the grandstand is not surfaced in seating.");
        Assert.True(concrete > audience, "the retaining walls should still be concrete.");
        Assert.True(AcousticRegistry.GetProperties("Audience").Scattering > 0.5f);
        Assert.True(AcousticRegistry.GetProperties("Concrete").Scattering < 0.2f);
    }
}
