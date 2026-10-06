using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// The wind in a wood, heard past the hand-over as one source per wood (WoodChorus): the woods made from
/// the crowns, the shares that hand a tree over without a step, the woods as the client's own entities,
/// and one synth standing for many trees (FoliageSynth.Trees).
/// </summary>
public class WoodChorusTests
{
    private static List<WoodChorus.Crown> Grid(int n, Vector3 origin, float spacing, int firstId = 1)
    {
        var crowns = new List<WoodChorus.Crown>();
        int side = (int)MathF.Ceiling(MathF.Sqrt(n));
        for (int i = 0; i < n; i++)
            crowns.Add(new WoodChorus.Crown(firstId + i, origin + new Vector3(i % side * spacing, 7f, i / side * spacing), "park_tree"));
        return crowns;
    }

    private static WoodChorus Build(List<WoodChorus.Crown> crowns)
    {
        int next = WoodChorus.FirstId;
        var ids = new Dictionary<(int, int, string), int>();
        return WoodChorus.Build(crowns, (cell, preset) => ids.TryGetValue((cell.X, cell.Z, preset), out int id) ? id : ids[(cell.X, cell.Z, preset)] = next--);
    }

    [Fact]
    public void A_key_says_the_trees_and_the_size_of_the_wood()
    {
        string key = WoodChorus.KeyFor("park_tree", 60f, 40f);
        Assert.Equal("wood:park_tree@60x40", key);
        Assert.True(WoodChorus.ParseKey(key, out var preset, out float rx, out float rz));
        Assert.Equal(("park_tree", 60f, 40f), (preset, rx, rz));
        Assert.False(WoodChorus.ParseKey("foliage:park_tree", out _, out _, out _));
        // A wood's places are spread over the wood, not a crown.
        var layout = ExtendedSources.Layout(key)!;
        Assert.Equal(1 + FoliageSynth.Boughs, layout.Length);
        Assert.InRange(ExtendedSources.Reach(layout), 25f, 41f);
    }

    [Fact]
    public void Trees_make_a_wood_for_each_square_and_kind()
    {
        var crowns = Grid(16, new Vector3(10f, 0f, 10f), 20f);                 // all in cell (0, 0)
        crowns.AddRange(Grid(4, new Vector3(250f, 0f, 10f), 20f, firstId: 100)); // cell (1, 0)
        crowns.Add(new WoodChorus.Crown(200, new Vector3(30f, 9f, 30f), "pine"));
        var chorus = Build(crowns);
        Assert.Equal(3, chorus.Woods.Count);
        var big = chorus.Woods.Single(w => w.Crowns.Length == 16);
        Assert.Equal("park_tree", big.Preset);
        Assert.Equal(new Vector3(40f, 7f, 40f), big.Centre);
        Assert.Equal(40f, big.RadiusX);   // 30 m to the outer crowns' middles, 4 m of crown, to the next 10 m
        // Heard further than one tree: 16 trees are 12 dB louder.
        float single = Loudness.AudibleRange(FoliageSpec.ParkTree.SourceLevelDb);
        Assert.True(big.RangeMetres > single * 1.5f, $"{big.RangeMetres:F0} m against {single:F0} m");
        Assert.All(chorus.Woods, w => Assert.True(ClientWorldState.IsWood(w.Id)));
    }

    /// <summary>Walking from 400 m into the middle of a wood, the power of the trees' own voices and the
    /// wood's together is always that of every tree at its own distance.</summary>
    [Fact]
    public void Handing_trees_over_never_changes_how_much_of_the_wood_there_is()
    {
        var crowns = Grid(25, new Vector3(20f, 0f, 20f), 30f);
        var chorus = Build(crowns);
        var wood = Assert.Single(chorus.Woods);
        var weights = new WoodChorus.Weights();
        float refT = wood.ReferenceMetres;
        for (float z = -400f; z <= wood.Centre.Z; z += 5f)
        {
            var ear = new Vector3(wood.Centre.X, 1.7f, z);
            chorus.Weigh(ear, weights);
            double all = 0, played = 0;
            foreach (var c in crowns)
            {
                float d = MathF.Max(Vector3.Distance(ear, c.At), refT);
                all += 1.0 / (d * d);
                float own = weights.Individual[c.Id];
                played += own * own / (d * d);
            }
            var (trees, gain) = weights.Woods[wood.Id];
            float dw = MathF.Max(Vector3.Distance(ear, wood.Centre), MathF.Max(refT, wood.Extent));
            played += trees * gain * gain / (dw * dw);
            Assert.InRange(10 * Math.Log10(played / all), -0.01, 0.01);
        }
        // Far off it is all the wood's; in the middle of it, the near trees are their own.
        chorus.Weigh(new Vector3(wood.Centre.X, 1.7f, -400f), weights);
        Assert.Equal(25f, weights.Woods[wood.Id].Trees, 3);
        chorus.Weigh(wood.Centre with { Y = 1.7f }, weights);
        Assert.True(weights.Woods[wood.Id].Trees < 25f);
    }

    [Fact]
    public void A_tree_the_budget_left_without_a_voice_is_heard_in_its_wood()
    {
        var crowns = Grid(9, new Vector3(20f, 0f, 20f), 10f);
        var chorus = Build(crowns);
        var wood = Assert.Single(chorus.Woods);
        var weights = new WoodChorus.Weights();
        var ear = wood.Centre with { Y = 1.7f };
        chorus.Weigh(ear, weights);
        Assert.Equal(0f, weights.Woods[wood.Id].Trees, 3);           // all near: all their own
        chorus.Weigh(ear, weights, id => id <= 4);                     // the budget voiced four
        Assert.Equal(5f, weights.Woods[wood.Id].Trees, 3);
        Assert.Equal(1f, weights.Individual[9]);                        // still ranked as itself
    }

    [Fact]
    public void The_client_makes_its_woods_as_entities_of_its_own_and_takes_them_away()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(2000, 100, 2000));
        foreach (var c in Grid(10, new Vector3(20f, 0f, 20f), 15f).Concat(Grid(6, new Vector3(420f, 0f, 20f), 15f, firstId: 50)))
        {
            var def = new EntityDefinition { EntityId = c.Id, Transform = new Transform { Position = c.At, Rotation = Quaternion.Identity } };
            def.SoundEmitter = new SoundEmitterComponent { IsSynth = true, SoundId = "foliage:park_tree", Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 90f };
            world.RegisterDefinition(def);
        }
        Assert.Empty(world.RefreshWoods());
        var snap = world.GetSnapshot();
        var woods = snap.Entities.Values.Where(e => ClientWorldState.IsWood(e.Id)).ToList();
        Assert.Equal(2, woods.Count);
        Assert.All(woods, w => Assert.StartsWith("wood:park_tree@", w.Definition.SoundEmitter.SoundId));
        Assert.All(woods, w => Assert.Contains(w.Id, snap.AudioEntityIds));
        Assert.NotNull(snap.Woods);
        // Not something to pick up or talk to.
        Assert.Null(world.GetClosestEntityId(woods[0].Transform.Position));

        // A refresh with nothing changed changes nothing.
        long version = world.Version;
        Assert.Empty(world.RefreshWoods());
        Assert.Equal(woods.Select(w => w.Definition), world.GetSnapshot().Entities.Values.Where(e => ClientWorldState.IsWood(e.Id)).Select(e => e.Definition));

        // The far wood's trees leave with their tile: its wood goes too, and the other keeps its id.
        world.RemoveEntities(Enumerable.Range(50, 6));
        var gone = world.RefreshWoods();
        int farId = woods.Single(w => w.Transform.Position.X > 300f).Id;
        Assert.Equal(new[] { farId }, gone);
        Assert.False(world.GetSnapshot().Entities.ContainsKey(farId));
        Assert.True(world.GetSnapshot().Entities.ContainsKey(woods.Single(w => w.Id != farId).Id));
    }

    /// <summary>One synth standing for eight trees is eight trees' power: the strikes three times as
    /// often, the rest in amplitude.</summary>
    [Fact]
    public void One_synth_for_eight_trees_has_eight_trees_power()
    {
        const int rate = 24000;
        double Power(float trees)
        {
            var s = new FoliageSynth(FoliageSpec.ParkTree, rate, 11) { Trees = trees };
            double sum = 0;
            int n = rate * 40;
            for (int k = 0; k < n; k++)
            {
                if (k % 256 == 0) { s.Wind = 4.5f; s.Control(256f / rate); }
                float y = s.Next();
                if (k > rate * 2) sum += y * (double)y;
            }
            return sum;
        }
        double one = Power(1f), eight = Power(8f);
        Assert.InRange(10 * Math.Log10(eight / one), 9.0 - 1.0, 9.0 + 1.0);
        Assert.Equal(0.0, Power(0f));
    }
}
