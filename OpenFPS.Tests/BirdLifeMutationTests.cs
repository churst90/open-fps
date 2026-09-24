using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The birds, held to the edges Stryker found untested (2026-09-24): which species a map's hedges
/// and roofs get and which things are not habitat at all, where each bird sits, the rhythm of bouts
/// and the contagion between neighbours, a bang and the way they come back, and the geese going
/// over. The timing is random by design, so it is tested as distributions over many birds, with the
/// birds' own random numbers seeded so a run is repeatable.
/// </summary>
public class BirdLifeMutationTests
{
    // ── A world, a bank of calls, and the birds ─────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public readonly string Bank = Path.Combine(Path.GetTempPath(), "openfps-birds-" + Guid.NewGuid().ToString("N"));
        public readonly EmitterRecordingProvider Mixer = new();
        public readonly AudioEngineFacade Audio;
        public readonly BirdLife Birds;
        public readonly List<(string Species, Vector3 At, double T)> Calls = new();
        public double Now;
        public bool Pump;

        public Rig(int seed, SpatialAcoustics? acoustics = null)
        {
            foreach (var folder in new[] { "SPARROW", "DOVE", "PIGEON", "CROW", "GOOSE" })
            {
                Directory.CreateDirectory(Path.Combine(Bank, "BIRDS", folder));
                for (int i = 0; i < 6; i++)
                    File.WriteAllBytes(Path.Combine(Bank, "BIRDS", folder, $"{folder.ToLowerInvariant()}_{i}.wav"), Array.Empty<byte>());
            }
            Audio = new AudioEngineFacade(Mixer);
            Audio.InitializeForTest(Bank);
            Birds = new BirdLife(Audio, acoustics);
            typeof(BirdLife).GetField("_rng", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Birds, new Random(seed));
            Birds.OnCall = (sp, at) => Calls.Add((sp, at, Now));
        }

        public void Run(WorldSnapshot world, Vector3 ear, double from, double seconds, double dt = 0.05)
        {
            Audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
            for (int i = 0; i * dt < seconds; i++)
            {
                Now = from + i * dt;
                Birds.Update(world, ear, Now);
                if (Pump) for (int k = 0; k < 2; k++) Audio.PumpForTest();
            }
        }

        public void Dispose() { try { Directory.Delete(Bank, true); } catch { } }
    }

    private static EntitySnapshot Box(int id, Vector3 at, Vector3 size, string material, bool solid = true,
                                      Vector3? velocity = null, ColliderShape shape = ColliderShape.Box, float yaw = 0f)
        => new()
        {
            Id = id,
            Definition = new EntityDefinition
            {
                EntityId = id,
                Type = EntityType.StaticObject,
                Collider = new ColliderComponent { Shape = shape, Size = size, IsSolid = solid },
                Material = new MaterialComponent { Material = material, Variant = "0" },
            },
            Transform = new Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f), Scale = Vector3.One },
            Velocity = velocity ?? Vector3.Zero,
        };

    private static WorldSnapshot World(IEnumerable<EntitySnapshot> things)
    {
        var w = new WorldSnapshot();
        foreach (var t in things) w.Entities[t.Id] = t;
        return w;
    }

    private static readonly Vector3 Hedge = new(3f, 2f, 3f), Slab = new(20f, 1f, 20f);

    /// <summary>n things in a square grid <paramref name="spacing"/> apart, ids from <paramref name="firstId"/>.</summary>
    private static IEnumerable<EntitySnapshot> Grid(int firstId, int n, float spacing, float y, Func<int, Vector3, EntitySnapshot> make,
                                                    Vector3? offset = null)
    {
        int side = (int)MathF.Ceiling(MathF.Sqrt(n));
        var o = offset ?? Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            var at = o + new Vector3((i % side - side / 2f) * spacing, y, (i / side - side / 2f) * spacing);
            yield return make(firstId + i, at);
        }
    }

    private static Dictionary<string, int> GroupsOf(WorldSnapshot world, SpatialAcoustics? acoustics = null)
    {
        using var rig = new Rig(1, acoustics);
        rig.Birds.Update(world, new Vector3(0f, 1.6f, 0f), 0);
        return rig.Birds.Groups.GroupBy(g => g.Species).ToDictionary(g => g.Key, g => g.Count());
    }

    // ── Where they live ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A sparrow group in one hedge in six and a dove in one in twelve; pigeons on half the roofs and
    /// crows on about one in seven. Birds of the foliage are never on a roof, nor the other way round.
    /// </summary>
    [Fact]
    public void HedgesAndRoofsGetTheirSpeciesInTheirProportions()
    {
        var hedges = Grid(1, 3000, 5f, 1f, (id, at) => Box(id, at, Hedge, "Foliage"));
        var roofs = Grid(5001, 2000, 30f, 15f, (id, at) => Box(id, at, Slab, "Concrete"), new Vector3(3000f, 0f, 0f));
        using var rig = new Rig(1);
        rig.Birds.Update(World(hedges.Concat(roofs)), Vector3.Zero, 0);
        var groups = rig.Birds.Groups.ToList();
        double Share(string sp, float y, int of) => groups.Count(g => g.Species == sp && g.Centre.Y == y) / (double)of;

        Assert.InRange(Share("house sparrow", 1f, 3000), 0.13, 0.19);
        Assert.InRange(Share("dove", 1f, 3000), 0.06, 0.10);
        Assert.InRange(Share("pigeon", 15f, 2000), 0.46, 0.54);
        Assert.InRange(Share("crow", 15f, 2000), 0.12, 0.18);
        Assert.DoesNotContain(groups, g => (g.Species is "pigeon" or "crow") && g.Centre.Y != 15f);
        Assert.DoesNotContain(groups, g => (g.Species is "house sparrow" or "dove") && g.Centre.Y != 1f);
        Assert.Equal(groups.Sum(g => g.Birds), rig.Birds.Census.Values.Sum());
    }

    /// <summary>
    /// Some things are not habitat whatever they are made of: anything moving, anything that is not
    /// a box, a box with no size along any one axis, a roof that is not solid, too low (a top under
    /// 10 m) or too small (under 60 square metres). And a tree's canopy is foliage however high and
    /// broad it is — sparrows and doves, never pigeons.
    /// </summary>
    [Fact]
    public void WhatIsNotHabitatHasNoBirds()
    {
        int id = 1;
        IEnumerable<EntitySnapshot> Many(Func<int, Vector3, EntitySnapshot> make) => Grid((id += 1000) - 1000, 400, 40f, 0f, make);

        void Empty(IEnumerable<EntitySnapshot> things, string what)
            => Assert.True(GroupsOf(World(things)).Count == 0, $"{what} has birds");

        Empty(Many((i, at) => Box(i, at + Vector3.UnitY, Hedge, "Foliage", velocity: new Vector3(3f, 0f, 0f))), "a moving hedge");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY * 15f, Slab, "Concrete", velocity: new Vector3(0f, 0f, 1f))), "a moving roof");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY, Hedge, "Foliage", shape: ColliderShape.Sphere)), "a round bush");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY, new Vector3(0f, 2f, 3f), "Foliage")), "a hedge with no width");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY, new Vector3(3f, 0f, 3f), "Foliage")), "a hedge with no height");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY, new Vector3(3f, 2f, 0f), "Foliage")), "a hedge with no depth");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY * 15f, Slab, "Concrete", solid: false)), "a roof that is not solid");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY * 8.5f, new Vector3(20f, 2f, 20f), "Concrete")), "a roof 9.5 m up");
        Empty(Many((i, at) => Box(i, at + Vector3.UnitY * 15f, new Vector3(7f, 1f, 8f), "Concrete")), "a 56 square metre roof");

        var canopy = GroupsOf(World(Many((i, at) => Box(i, at + Vector3.UnitY * 15f, Slab, "Foliage"))));
        Assert.True(canopy.GetValueOrDefault("house sparrow") > 0);
        Assert.False(canopy.ContainsKey("pigeon") || canopy.ContainsKey("crow"), "pigeons in a tree canopy");
    }

    /// <summary>A roof whose top is exactly 10 m up, or whose area is exactly 60 m², is a roof.</summary>
    [Fact]
    public void ARoofExactlyAtTheLimitsIsARoof()
    {
        var atHeight = GroupsOf(World(Grid(1, 300, 40f, 9.5f, (i, at) => Box(i, at, Slab, "Concrete"))));
        var atArea = GroupsOf(World(Grid(1, 300, 40f, 15f, (i, at) => Box(i, at, new Vector3(6f, 1f, 10f), "Concrete"))));
        Assert.True(atHeight.GetValueOrDefault("pigeon") > 100);
        Assert.True(atArea.GetValueOrDefault("pigeon") > 100);
    }

    /// <summary>
    /// Re-surveying a map whose geometry changed finds the birds afresh, not a second copy of them.
    /// </summary>
    [Fact]
    public void ReSurveyingDoesNotDoubleTheBirds()
    {
        var things = Grid(1, 400, 5f, 1f, (i, at) => Box(i, at, Hedge, "Foliage")).ToList();
        var world = World(things);
        using var rig = new Rig(1);
        rig.Birds.Update(world, Vector3.Zero, 0);
        var before = rig.Birds.Census.Values.Sum();
        Assert.True(before > 0);
        var extra = Box(99999, new Vector3(0f, -5f, 0f), new Vector3(1f, 1f, 1f), "Concrete");
        world.Entities[extra.Id] = extra;
        rig.Birds.Update(world, Vector3.Zero, 2.5);
        Assert.Equal(before, rig.Birds.Census.Values.Sum());
    }

    /// <summary>
    /// A roof has sky over most of it. Asked at a 4 by 4 grid of points: a stair head over a quarter
    /// leaves it a roof; cover over half of it, either way across, or all of it, and it is a floor
    /// with something above, not a roof.
    /// </summary>
    [Fact]
    public void ARoofNeedsSkyOverMostOfIt()
    {
        var acoustics = new SpatialAcoustics(new SpatialService());
        // Where the survey asks, along each axis: -7.5, -2.5, 2.5, 7.5 m from the middle of a 20 m slab.
        float[] at = { -7.5f, -2.5f, 2.5f, 7.5f };
        int Pigeons(Func<int, int, bool> covered)
        {
            var things = new List<EntitySnapshot>();
            int id = 1, cover = 100000;
            for (int r = 0; r < 60; r++)
            {
                var centre = new Vector3((r % 8) * 60f, 15f, (r / 8) * 60f);
                things.Add(Box(id++, centre, Slab, "Concrete"));
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                        if (covered(i, j))   // a small canopy two metres over that point, too small to be a roof
                            things.Add(Box(cover++, centre + new Vector3(at[i], 3f, at[j]), new Vector3(2f, 0.5f, 2f), "Concrete"));
            }
            return GroupsOf(World(things), acoustics).GetValueOrDefault("pigeon");
        }

        Assert.True(Pigeons((i, j) => false) > 15, "an open roof has no pigeons");
        Assert.True(Pigeons((i, j) => i == 3) > 15, "a roof with a quarter covered has no pigeons");
        Assert.Equal(0, Pigeons((i, j) => i >= 2));      // the +x half
        Assert.Equal(0, Pigeons((i, j) => j >= 2));      // the +z half
        Assert.Equal(0, Pigeons((i, j) => true));
    }

    // ── Where each bird sits ────────────────────────────────────────────────────────────────

    private static Vector3 Local(Vector3 p, Vector3 centre, float yaw)
        => Vector3.Transform(p - centre, Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f)));

    /// <summary>
    /// A sparrow sits inside its hedge, in the upper half, anywhere across it; a pigeon sits on an
    /// edge of its roof just above the top, anywhere along it, and on all four edges between them.
    /// Hedges and roofs are turned, so this is in each one's own frame.
    /// </summary>
    [Fact]
    public void EachBirdSitsWhereItsSpeciesDoes()
    {
        const float yaw = 0.6f;
        var hedge = new Vector3(4f, 2f, 3f);
        var hedges = Grid(1, 225, 8f, 1f, (i, at) => Box(i, at, hedge, "Foliage", yaw: yaw), new Vector3(4f, 0f, 4f)).ToList();
        using (var rig = new Rig(3))
        {
            rig.Run(World(hedges), new Vector3(0f, 1.6f, 0f), 0, 300, dt: 0.1);
            var sparrows = rig.Calls.Where(c => c.Species == "house sparrow").Select(c => c.At).Distinct().ToList();
            Assert.True(sparrows.Count > 50, $"only {sparrows.Count} sparrows heard");
            float widest = 0f;
            foreach (var p in sparrows)
            {
                var h = hedges.OrderBy(e => Vector3.DistanceSquared(e.Transform.Position, p)).First();
                var l = Local(p, h.Transform.Position, yaw);
                Assert.InRange(l.X, -hedge.X / 2 - 1e-3f, hedge.X / 2 + 1e-3f);
                Assert.InRange(l.Z, -hedge.Z / 2 - 1e-3f, hedge.Z / 2 + 1e-3f);
                Assert.InRange(l.Y, 0.05f * hedge.Y - 1e-3f, 0.45f * hedge.Y + 1e-3f);
                widest = MathF.Max(widest, MathF.Abs(l.X));
            }
            Assert.True(widest > 0.35f * hedge.X, "the sparrows are all bunched in the middle of their hedges");
        }

        var roof = new Vector3(16f, 1f, 12f);
        var roofs = Grid(1, 36, 24f, 12f, (i, at) => Box(i, at, roof, "Concrete", yaw: yaw), new Vector3(12f, 0f, 12f)).ToList();
        using (var rig = new Rig(4))
        {
            rig.Run(World(roofs), new Vector3(0f, 1.6f, 0f), 0, 300, dt: 0.1);
            var pigeons = rig.Calls.Where(c => c.Species == "pigeon").Select(c => c.At).Distinct().ToList();
            Assert.True(pigeons.Count > 30, $"only {pigeons.Count} pigeons heard");
            var edges = new HashSet<string>();
            float alongX = 0f, alongZ = 0f;
            foreach (var p in pigeons)
            {
                var r = roofs.OrderBy(e => Vector3.DistanceSquared(e.Transform.Position, p)).First();
                var l = Local(p, r.Transform.Position, yaw);
                Assert.Equal(roof.Y / 2 + 0.15f, l.Y, 3);
                bool onX = MathF.Abs(MathF.Abs(l.X) - roof.X / 2) < 1e-2f, onZ = MathF.Abs(MathF.Abs(l.Z) - roof.Z / 2) < 1e-2f;
                Assert.True(onX || onZ, $"a pigeon at {l} is not on an edge");
                Assert.InRange(l.X, -roof.X / 2 - 1e-2f, roof.X / 2 + 1e-2f);
                Assert.InRange(l.Z, -roof.Z / 2 - 1e-2f, roof.Z / 2 + 1e-2f);
                if (onX) { edges.Add(l.X > 0 ? "+x" : "-x"); alongZ = MathF.Max(alongZ, MathF.Abs(l.Z) / roof.Z); }
                if (onZ) { edges.Add(l.Z > 0 ? "+z" : "-z"); alongX = MathF.Max(alongX, MathF.Abs(l.X) / roof.X); }
            }
            Assert.Equal(4, edges.Count);
            Assert.True(alongX > 0.35f && alongZ > 0.35f, "the pigeons are all in the middle of their edges");
        }
    }

    /// <summary>
    /// A flock is between the species' smallest and largest, and a bigger perch holds more of it:
    /// on hedges 12 m across the sparrow groups are spread evenly over 2 to 6, averaging 4; on bushes
    /// a metre across they are mostly the smallest a group can be.
    /// </summary>
    [Fact]
    public void ABiggerPerchHoldsMoreOfTheFlock()
    {
        List<int> Sizes(Vector3 size)
        {
            using var rig = new Rig(1);
            rig.Birds.Update(World(Grid(1, 3000, 15f, 1f, (i, at) => Box(i, at, size, "Foliage"))), Vector3.Zero, 0);
            return rig.Birds.Groups.Where(g => g.Species == "house sparrow").Select(g => g.Birds).ToList();
        }
        var big = Sizes(new Vector3(12f, 2f, 12f));
        var small = Sizes(new Vector3(1f, 1f, 1f));
        Assert.All(big.Concat(small), n => Assert.InRange(n, 2, 6));
        Assert.InRange(big.Average(), 3.6, 4.4);
        Assert.True(small.Average() < 2.8, $"bushes a metre across hold {small.Average():F2} sparrows on average");
    }

    // ── What reaches the mixer ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every call is a one-off sound at the bird, from its own species' folder, at its own pitch a few
    /// percent from its neighbours', at the species' level and audible range, still, with reverb —
    /// and with no acoustics to ask, straight and in no particular region. Calls take their voices
    /// from a pool of 48, counting down and round again.
    /// </summary>
    [Fact]
    public void EveryCallReachesTheMixerAsTheSpeciesSoundsAtTheBird()
    {
        using var rig = new Rig(5) { Pump = true };
        var ear = new Vector3(0f, 1.6f, 0f);
        rig.Run(World(Grid(1, 200, 6f, 1f, (i, at) => Box(i, at, Hedge, "Foliage"), new Vector3(3f, 0f, 3f))), ear, 0, 150, dt: 0.1);
        var played = rig.Mixer.Played;
        Assert.True(played.Count > 100, $"only {played.Count} calls played");
        Assert.Equal(rig.Calls.Count, played.Count);

        foreach (var sp in new[] { BirdSpecies.HouseSparrow, BirdSpecies.Dove })
        {
            var mine = played.Where(e => e.SoundId.StartsWith("BIRDS/" + sp.Folder + "/", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(mine);
            var (gain, reference) = Loudness.Place(sp.CallDb);
            foreach (var e in mine)
            {
                Assert.InRange(e.Pitch, 1f - sp.PitchSpread - 1e-4f, 1f + sp.PitchSpread + 1e-4f);
                Assert.Equal(gain, e.Volume, 5);
                Assert.Equal(reference, e.MinDistance, 5);
                Assert.Equal(Loudness.AudibleRange(sp.CallDb), e.Range, 3);
            }
            Assert.True(mine.Select(e => e.Pitch).Distinct().Count() > 3, $"every {sp.Name} has the same voice");
        }
        foreach (var e in played)
        {
            Assert.True(e.IsEvent);
            Assert.True(e.EnableReverb);
            Assert.Equal(PlaybackMode.Single, e.Mode);
            Assert.Equal(Vector3.Zero, e.Velocity);
            Assert.Equal(e.Position, e.ApparentPosition);
            Assert.Equal(Vector3.Distance(ear, e.Position), e.EffectiveDistance, 3);
            Assert.Equal(0f, e.Occlusion);
            Assert.Equal(-1, e.TargetRegionId);
            Assert.Equal(1f, e.EqHigh);
        }

        var ids = played.Select(e => e.EntityId).ToList();
        Assert.Equal(48, ids.Distinct().Count());
        // Counting down from the first: the first call has the top of the pool (give or take calls
        // in the same frame, which the mixer takes in its own order), and every id is at or below it.
        int top = ids.Max();
        Assert.True(top < 0);
        Assert.InRange(ids[0], top - 3, top);
        Assert.All(ids, id => Assert.InRange(id, top - 47, top));
    }

    /// <summary>
    /// A call is heard through the same acoustic path as everything else: sparrows in hedges behind
    /// a wall are behind the wall. The path is asked on behalf of no entity, so nothing in the world —
    /// the wall included — is left out of it.
    /// </summary>
    [Fact]
    public void ACallBehindAWallIsHeardThroughIt()
    {
        var acoustics = new SpatialAcoustics(new SpatialService());
        var wall = Box(1, new Vector3(0f, 5f, 5f), new Vector3(200f, 10f, 0.3f), "Concrete");
        var hedges = Grid(2, 100, 6f, 1f, (i, at) => Box(i, at, Hedge, "Foliage"), new Vector3(0f, 0f, 40f));
        var world = World(hedges.Append(wall));
        using var rig = new Rig(13, acoustics) { Pump = true };
        rig.Run(world, Ear, 0, 120, dt: 0.1);
        var played = rig.Mixer.Played;
        Assert.True(played.Count > 10, $"only {played.Count} calls");
        foreach (var e in played)
        {
            var expected = acoustics.CalculateAcousticPath(world, -1, Ear, e.Position);
            Assert.True(expected.Occlusion > 0.1f, $"a call at {e.Position} is barely behind the wall ({expected.Occlusion:F2})");
            Assert.Equal(expected.Occlusion, e.Occlusion, 4);
            Assert.Equal(expected.TransmissionBleed, e.TransmissionBleed, 4);
            Assert.Equal(expected.EqLow, e.EqLow, 4);
            Assert.Equal(expected.EqMid, e.EqMid, 4);
            Assert.Equal(expected.EqHigh, e.EqHigh, 4);
            Assert.Equal(expected.ApertureFactor, e.ApertureFactor, 4);
            Assert.Equal(expected.EffectiveDistance, e.EffectiveDistance, 3);
            Assert.Equal(expected.ApparentPosition, e.ApparentPosition);
            Assert.Equal(expected.RegionId, e.TargetRegionId);
        }
    }

    /// <summary>
    /// A call too quiet to hear at the ear is not made at all: pigeons on a roof 120 m off, inside
    /// the distance at which they are awake but well past where a 64 dB coo is heard. From 30 m they
    /// are.
    /// </summary>
    [Fact]
    public void ACallTooQuietToHearIsNotMade()
    {
        var (gain, reference) = Loudness.Place(BirdSpecies.Pigeon.CallDb);
        float range = Loudness.AudibleRange(BirdSpecies.Pigeon.CallDb);
        Assert.True(Loudness.RenderedGain(gain, reference, range, 125f) < 0.001f && range > 130f,
                    "at this distance a pigeon would be heard, so this proves nothing");

        var roofs = Grid(1, 16, 22f, 15f, (i, at) => Box(i, at, Slab, "Concrete")).ToList();
        using var rig = new Rig(6);
        rig.Run(World(roofs), new Vector3(0f, 15f, 120f + 40f), 0, 200, dt: 0.1);
        Assert.DoesNotContain(rig.Calls, c => c.Species == "pigeon");
        rig.Run(World(roofs), new Vector3(0f, 16f, 45f), 200, 200, dt: 0.1);
        Assert.Contains(rig.Calls, c => c.Species == "pigeon");
    }

    // ── Rhythm ──────────────────────────────────────────────────────────────────────────────

    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);

    /// <summary>A hedge-filled neighbourhood around the ear, no hedge within 5 m of it.</summary>
    private static WorldSnapshot Neighbourhood(int n = 400)
        => World(Grid(1, n, 6f, 1f, (i, at) => Box(i, at, Hedge, "Foliage"), new Vector3(3f, 0f, 3f))
                 .Where(e => new Vector2(e.Transform.Position.X, e.Transform.Position.Z).Length() > 5f));

    /// <summary>Each sparrow's calls, by the bird: a bird is its place in the hedge.</summary>
    private static List<List<double>> ByBird(Rig rig, string species = "house sparrow")
        => rig.Calls.Where(c => c.Species == species).GroupBy(c => c.At).Select(g => g.Select(c => c.T).OrderBy(t => t).ToList()).ToList();

    /// <summary>
    /// Sparrows call in BOUTS: three to ten calls 0.35 to 1.1 s apart, then at least fifteen seconds'
    /// rest — never anything in between, and never a rest cut short by a neighbour before it has
    /// lasted the species' shortest rest.
    /// </summary>
    [Fact]
    public void SparrowsCallInBoutsWithRestsBetween()
    {
        var sp = BirdSpecies.HouseSparrow;
        using var rig = new Rig(7);
        rig.Run(Neighbourhood(), Ear, 0, 900);
        var lengths = new List<int>();
        int birds = 0;
        foreach (var calls in ByBird(rig))
        {
            birds++;
            int run = 1;
            for (int i = 1; i < calls.Count; i++)
            {
                double gap = calls[i] - calls[i - 1];
                if (gap < 5)
                {
                    Assert.InRange(gap, sp.CallGapMin - 1e-6, sp.CallGapMax + 0.06);
                    run++;
                }
                else
                {
                    Assert.True(gap >= sp.BoutGapMin - 1e-6, $"a rest of {gap:F2} s");
                    lengths.Add(run);
                    run = 1;
                }
            }
        }
        Assert.True(birds > 40 && lengths.Count > 300, $"{birds} birds, {lengths.Count} bouts");
        Assert.All(lengths, n => Assert.InRange(n, sp.BoutCallsMin, sp.BoutCallsMax));
        Assert.Contains(lengths, n => n == sp.BoutCallsMax);
    }

    /// <summary>
    /// A hedge breaks out together: when one sparrow starts a bout, its neighbours in the same hedge
    /// start one in the next few seconds far more often than they do at any other moment — but not
    /// in the same instant: a neighbour answers, it does not echo.
    /// </summary>
    [Fact]
    public void AHedgeBreaksOutTogether()
    {
        using var rig = new Rig(8);
        var world = Neighbourhood();
        rig.Run(world, Ear, 0, 900);
        var groups = rig.Birds.Groups.ToList();
        // Bout starts, by bird, and each bird's hedge.
        var starts = rig.Calls.Where(c => c.Species == "house sparrow").GroupBy(c => c.At).Select(g =>
        {
            var t = g.Select(c => c.T).OrderBy(x => x).ToList();
            var s = new List<double> { t[0] };
            for (int i = 1; i < t.Count; i++) if (t[i] - t[i - 1] >= 5) s.Add(t[i]);
            var hedge = groups.OrderBy(h => Vector3.DistanceSquared(h.Centre, g.Key)).First().Centre;
            return (Hedge: hedge, Starts: s);
        }).ToList();

        int Joined(double from, double to)
        {
            int n = 0;
            foreach (var a in starts)
                foreach (double s in a.Starts)
                    foreach (var b in starts)
                        if (!ReferenceEquals(a.Starts, b.Starts) && a.Hedge == b.Hedge && b.Starts.Any(x => x > s + from && x <= s + to)) n++;
            return n;
        }
        int together = Joined(0, 3.1), chance = Joined(100, 103.1);
        int instant = Joined(0, 0.35), instantChance = Joined(100, 100.35);
        Assert.True(together > 2 * chance + 20, $"{together} joined within 3 s against {chance} by chance");
        Assert.True(instant <= 2 * instantChance + 5, $"{instant} joined within 0.35 s against {instantChance} by chance");
    }

    /// <summary>
    /// Coming into earshot of a group mid-afternoon, not at its dawn: the birds do not all start at
    /// once, but each is somewhere in its cycle — and every one has called within half its species'
    /// longest rest.
    /// </summary>
    [Fact]
    public void BirdsComeIntoEarshotMidCycle()
    {
        using var rig = new Rig(9);
        var world = Neighbourhood();
        rig.Run(world, new Vector3(2000f, 1.6f, 0f), 0, 10);   // out of earshot: asleep
        Assert.Empty(rig.Calls);
        rig.Run(world, Ear, 10, 45);
        var groups = rig.Birds.Groups.Where(g => g.Species == "house sparrow").ToList();
        var first = rig.Calls.Where(c => c.Species == "house sparrow").GroupBy(c => c.At).ToDictionary(g => g.Key, g => g.Min(c => c.T) - 10);
        double window = BirdSpecies.HouseSparrow.BoutGapMax * 0.5 + 0.06;
        Assert.Equal(groups.Sum(g => g.Birds), first.Count(kv => kv.Value <= window));
        Assert.True(first.Values.Count(t => t < 0.5) < 0.3 * first.Count, "they all started together");
    }

    // ── A bang ──────────────────────────────────────────────────────────────────────────────

    private static WorldAudioEvent Bang(Vector3 at, float db)
        => new() { Label = "bang", Sounds = new List<TransientSound> { new() { Position = at, LevelDb = db } } };

    /// <summary>
    /// A shot among them shuts them all up for at least eight seconds; they come back, and they come
    /// back one at a time — no two birds of a hedge on the same instant.
    /// </summary>
    [Fact]
    public void ABangShutsThemUpAndTheyComeBackOneAtATime()
    {
        using var rig = new Rig(10);
        var world = Neighbourhood();
        rig.Run(world, Ear, 0, 60);
        rig.Birds.Heard(Bang(new Vector3(6f, 1f, 6f), 150f), 60);
        int before = rig.Calls.Count;
        rig.Run(world, Ear, 60, 120);
        var after = rig.Calls.Skip(before).ToList();
        Assert.DoesNotContain(after, c => c.T < 68);
        Assert.True(after.Count(c => c.T < 110) > 20, "they never came back");

        var groups = rig.Birds.Groups.ToList();
        int sameInstant = 0, compared = 0;
        foreach (var g in after.GroupBy(c => groups.OrderBy(h => Vector3.DistanceSquared(h.Centre, c.At)).First().Centre))
        {
            var returns = g.GroupBy(c => c.At).Select(b => b.Min(c => c.T)).OrderBy(t => t).ToList();
            for (int i = 1; i < returns.Count; i++) { compared++; if (returns[i] - returns[i - 1] < 0.06) sameInstant++; }
        }
        Assert.True(compared > 20);
        Assert.True(sameInstant < 0.1 * compared, $"{sameInstant} of {compared} came back on the same instant");
    }

    /// <summary>
    /// A bang that is under a species' startle level by the time it reaches them — 100 dB at the ear
    /// and every hedge ten metres or more off — does not quiet them; and a message with no sounds in
    /// it is nothing.
    /// </summary>
    [Fact]
    public void AFarOrQuietBangDoesNotQuietThem()
    {
        using var rig = new Rig(11);
        var world = World(Grid(1, 900, 6f, 1f, (i, at) => Box(i, at, Hedge, "Foliage"))
                          .Where(e => new Vector2(e.Transform.Position.X, e.Transform.Position.Z).Length() is > 12f and < 40f));
        rig.Run(world, Ear, 0, 60);
        rig.Birds.Heard(new WorldAudioEvent { Label = "nothing", Sounds = null! }, 60);
        rig.Birds.Heard(Bang(Ear, 100f), 60);
        int before = rig.Calls.Count;
        rig.Run(world, Ear, 60, 8);
        Assert.True(rig.Calls.Count - before > 5, $"{rig.Calls.Count - before} calls in the 8 s after a distant bang");
    }

    /// <summary>
    /// A group out of earshot is asleep and hears nothing: a shot beside a hedge nobody is near does
    /// not silence it for whoever arrives a second later.
    /// </summary>
    [Fact]
    public void AnAsleepGroupHearsNothing()
    {
        using var rig = new Rig(12);
        var world = Neighbourhood();
        rig.Run(world, new Vector3(2000f, 1.6f, 0f), 0, 5);
        rig.Birds.Heard(Bang(new Vector3(6f, 1f, 6f), 150f), 5);
        rig.Run(world, Ear, 5, 8);
        Assert.True(rig.Calls.Count > 5, $"{rig.Calls.Count} calls on arriving after a bang nobody was near");
    }

    // ── Geese ───────────────────────────────────────────────────────────────────────────────

    private static List<List<(Vector3 At, double T)>> Skeins(Rig rig)
    {
        var skeins = new List<List<(Vector3, double)>>();
        foreach (var c in rig.Calls.Where(c => c.Species == "goose"))
        {
            if (skeins.Count == 0 || c.T - skeins[^1][^1].Item2 > 100) skeins.Add(new());
            skeins[^1].Add((c.At, c.T));
        }
        return skeins;
    }

    /// <summary>
    /// Now and then a skein of geese goes over: the first not in the first minute and within about
    /// seven minutes, and then one on average every seven minutes after the last has gone — each a
    /// passing somewhere near, not always overhead: heard for the better part of a minute, and gone.
    /// </summary>
    [Fact]
    public void GeeseGoOverNowAndThen()
    {
        var firsts = new List<double>();
        var gaps = new List<double>();
        var spans = new List<double>();
        var closest = new List<float>();
        for (int seed = 1; seed <= 12; seed++)
        {
            using var rig = new Rig(100 + seed);
            rig.Run(new WorldSnapshot(), Ear, 0, 2600, dt: 0.1);
            var skeins = Skeins(rig);
            Assert.True(skeins.Count >= 2, $"seed {seed}: {skeins.Count} skeins in 2600 s");
            firsts.Add(skeins[0][0].Item2);
            for (int i = 1; i < skeins.Count; i++) gaps.Add(skeins[i][0].Item2 - skeins[i - 1][0].Item2);
            spans.AddRange(skeins.Take(skeins.Count - 1).Select(s => s[^1].Item2 - s[0].Item2));
            closest.AddRange(skeins.Select(s => s.Min(c => new Vector2(c.Item1.X, c.Item1.Z).Length())));
        }
        Assert.All(firsts, t => Assert.InRange(t, 60, 470));
        // A pass lasts 2 x 650 m / 14 m/s; the next is 210 to 630 s after it has gone.
        Assert.All(gaps, g => Assert.InRange(g, 93 + 210, 93 + 630 + 40));
        Assert.InRange(gaps.Average(), 93 + 420 - 90, 93 + 420 + 90);
        Assert.All(spans, s => Assert.InRange(s, 25, 93));
        // Somewhere near, not always overhead: within about 150 m, and not always within 40.
        Assert.All(closest, d => Assert.True(d < 185f));
        Assert.True(closest.Count(d => d > 40f) > closest.Count / 4, "the geese always go straight over");
    }

    /// <summary>
    /// A skein flies at fourteen metres a second, straight, sixty to a hundred and ten metres up, and
    /// passes within about 150 m of you; its honks carry that velocity for their Doppler, every goose
    /// has its own voice, and they honk in bouts — roughly six or seven honks to every eight seconds
    /// of each goose's cycle.
    /// </summary>
    [Fact]
    public void ASkeinFliesOverStraightAndHonks()
    {
        var sp = BirdSpecies.Goose;
        for (int seed = 1; seed <= 3; seed++)
        {
            using var rig = new Rig(200 + seed) { Pump = true };
            rig.Run(new WorldSnapshot(), Ear, 0, 700, dt: 0.1);
            var skein = Skeins(rig)[0];
            Assert.True(skein.Count > 50, $"seed {seed}: {skein.Count} honks");
            foreach (var (at, _) in skein) Assert.InRange(at.Y - Ear.Y, 58f, 112f);
            Assert.True(skein.Min(c => new Vector2(c.At.X, c.At.Z).Length()) < 185f, "it never came near");

            // Speed and heading, from where the honks came from over time.
            double tm = skein.Average(c => c.T), xm = skein.Average(c => c.At.X), zm = skein.Average(c => c.At.Z);
            double vt = skein.Sum(c => (c.T - tm) * (c.T - tm));
            var v = new Vector2((float)(skein.Sum(c => (c.T - tm) * (c.At.X - xm)) / vt), (float)(skein.Sum(c => (c.T - tm) * (c.At.Z - zm)) / vt));
            Assert.InRange(v.Length(), 12.5f, 15.5f);

            var honks = rig.Mixer.Played.Where(e => e.SoundId.StartsWith("BIRDS/GOOSE/", StringComparison.Ordinal)).ToList();
            Assert.Equal(rig.Calls.Count(c => c.Species == "goose"), honks.Count);
            honks = honks.Take(skein.Count).ToList();
            foreach (var e in honks)
            {
                Assert.Equal(14f, e.Velocity.Length(), 3);
                Assert.Equal(0f, e.Velocity.Y);
                Assert.True(Vector2.Dot(Vector2.Normalize(new Vector2(e.Velocity.X, e.Velocity.Z)), Vector2.Normalize(v)) > 0.95f,
                            "the honks move one way and their Doppler says another");
                Assert.InRange(e.Pitch, 1f - sp.PitchSpread - 1e-4f, 1f + sp.PitchSpread + 1e-4f);
            }
            int geese = honks.Select(e => e.Pitch).Distinct().Count();
            Assert.InRange(geese, sp.GroupMin, sp.GroupMax);
            double span = skein[^1].T - skein[0].T;
            // A bout is 3-10 honks 0.5-1.4 s apart, then 1-5 s of quiet: 6.5 honks per 8.2 s.
            double rate = skein.Count / (geese * span);
            Assert.InRange(rate, 0.6, 1.0);
        }
    }
}
