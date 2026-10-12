using System.Reflection;
using System.Text.Json.Nodes;
using OpenFPS.Common;
using OpenFPS.Common.Editing;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A preset is two halves joined by its id (docs/PHYSICS_BOUNDARY.md 4.4, stage 2): its mechanics are the
/// physics', its sound the sound's. The sound's half keeps its name, its members and its stored shape, so
/// whoever edits or saves a tyre sees one thing; the numbers that are mechanical live in the physics' half
/// and nowhere else.
/// </summary>
public class PresetHalvesTests
{
    private readonly ITestOutputHelper _o;
    public PresetHalvesTests(ITestOutputHelper o) => _o = o;

    /// <summary>Every tyre preset has both halves under one id, and the sound's half holds the physics' half
    /// of that id.</summary>
    [Fact]
    public void A_tyres_two_halves_are_joined_by_its_id()
    {
        Assert.Equal(TyreProfile.Presets.Keys.OrderBy(k => k, StringComparer.Ordinal), TyreMechanics.Presets.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string id in TyreProfile.Presets.Keys)
            Assert.Equal(TyreMechanics.ByName(id), TyreProfile.ByName(id).Mechanics);
        Assert.Equal(TyreMechanics.SportBikeRoad, TyreProfile.SportBikeRoad.Mechanics);
        Assert.Throws<ArgumentException>(() => TyreMechanics.ByName("no such tyre"));
    }

    /// <summary>
    /// Each mechanical number of a tyre is in one place: the sound's half reads and writes its physics half,
    /// member by member, so the wheel model and the editor cannot hold different grips.
    /// </summary>
    [Fact]
    public void A_tyres_mechanical_members_are_its_mechanics()
    {
        var mechanical = typeof(TyreMechanics).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(float)).ToList();
        Assert.Equal(12, mechanical.Count);
        var tyre = TyreProfile.TruckOnAsphalt;
        float n = 1f;
        foreach (var m in mechanical)
        {
            var onSound = typeof(TyreProfile).GetProperty(m.Name);
            Assert.True(onSound != null, $"TyreProfile has no {m.Name}");
            // It reads the mechanics' number...
            Assert.Equal((float)m.GetValue(tyre.Mechanics)!, (float)onSound!.GetValue(tyre)!);
            // ...and setting it sets the mechanics', and nothing else of them.
            float value = 0.123f + n++;
            var set = (TyreProfile)typeof(TyreProfile).GetMethod("<Clone>$")!.Invoke(tyre, null)!;
            onSound.SetValue(set, value);
            Assert.Equal(value, (float)m.GetValue(set.Mechanics)!);
            foreach (var other in mechanical.Where(o => o != m))
                Assert.Equal((float)other.GetValue(tyre.Mechanics)!, (float)other.GetValue(set.Mechanics)!);
            // The editor still shows it as the tyre's own, with what it says of itself.
            Assert.True(onSound.GetCustomAttribute<TunableAttribute>() != null, $"TyreProfile.{m.Name} lost its editor's attribute");
        }
        // The sound's own numbers are not the mechanics'.
        Assert.Null(typeof(TyreMechanics).GetProperty(nameof(TyreProfile.TreadBlocks)));
        Assert.Null(typeof(TyreMechanics).GetProperty(nameof(TyreProfile.SquealHz)));
        // With a `with`, as the catalogue assembles a tyre from a parts list.
        var worn = TyreProfile.SportsOnAsphalt with { TreadDepthMm = 1.6f, PeakGripG = 0.8f };
        Assert.Equal(1.6f, worn.Mechanics.TreadDepthMm);
        Assert.Equal(0.8f, worn.Mechanics.PeakGripG);
        Assert.Equal(220f, worn.Mechanics.InflationKPa);
        Assert.Equal(220f, TyreProfile.SportsOnAsphalt.InflationKPa);
    }

    /// <summary>The presets' numbers are what they were before the halves were parted.</summary>
    [Fact]
    public void The_tyres_numbers_are_what_they_were()
    {
        void Is(TyreProfile t, float grip, float kPa, float tread, int blocks, float roughness, float db, float hz, float q, float squealDb)
        {
            Assert.Equal((grip, kPa, tread), (t.PeakGripG, t.InflationKPa, t.TreadDepthMm));
            Assert.Equal((blocks, roughness, db, hz, q, squealDb), (t.TreadBlocks, t.SurfaceRoughness, t.ReferenceDb, t.SquealHz, t.SquealQ, t.SquealDb));
            Assert.Equal((1.193f, -1.003f, 14.95f, 2.130f), (t.LateralShape, t.LateralCurvature, t.CorneringStiffness, t.CorneringStiffnessLoad));
            Assert.Equal((1.685f, 0.344f, 21.51f, -0.163f, -0.146f), (t.LongitudinalShape, t.LongitudinalCurvature, t.SlipStiffness, t.SlipStiffnessLoad, t.LoadSensitivity));
        }
        Is(TyreProfile.SportsOnAsphalt, 0.95f, 220f, 5f, 68, 0.55f, 84f, 950f, 14f, 92f);
        Is(TyreProfile.SportBikeRoad, 1.1f, 270f, 3f, 0, 0.45f, 80f, 950f, 14f, 92f);
        Is(TyreProfile.RaceSlick, 3.1f, 160f, 0f, 0, 0.35f, 82f, 620f, 17f, 99f);
        Is(TyreProfile.TruckOnAsphalt, 0.75f, 760f, 12f, 96, 0.72f, 85f, 430f, 9f, 97f);
        Assert.Equal(9.80665f, TyreGrip.G);
        Assert.Equal(0.78f, TyreGrip.PatchSlidesFrom);
        // One number for where the patch begins to slide and where the squeal begins.
        Assert.Equal(TyreGrip.PatchSlidesFrom, TyreFriction.SquealOnset);
        // The friction circle: 3 and 4 make 5.
        Assert.Equal(5f / (0.95f * TyreGrip.G), TyreGrip.Demand(3f, 4f, 0.95f), 5);
    }

    /// <summary>
    /// A tyre is stored and edited in the shape it had: one flat object with every member, mechanical or
    /// not, in the order it had, and no part named for its mechanics. A vehicle saved before the halves were
    /// parted reads back the same.
    /// </summary>
    [Fact]
    public void A_tyre_is_stored_and_edited_in_the_shape_it_had()
    {
        string[] was =
        {
            "TreadBlocks", "SurfaceRoughness", "ReferenceDb", "PeakGripG", "SquealHz", "SquealQ", "SquealDb",
            "LateralShape", "LateralCurvature", "CorneringStiffness", "CorneringStiffnessLoad", "LongitudinalShape",
            "LongitudinalCurvature", "SlipStiffness", "SlipStiffnessLoad", "LoadSensitivity", "InflationKPa", "TreadDepthMm",
        };
        var spec = VehicleSpec.Of("i4_midsize") with { Tyres = TyreProfile.TruckOnAsphalt };
        string json = ModelLibrary.SpecJson(spec);
        var tyres = JsonNode.Parse(json)!["Tyres"]!.AsObject();
        Assert.Equal(was, tyres.Select(kv => kv.Key));
        Assert.Equal(0.75f, tyres["PeakGripG"]!.GetValue<float>());
        // Read back, it is the same tyre, mechanics and all.
        var back = (VehicleSpec)ModelLibrary.FromSpecJson(ModelLibrary.Kinds.Vehicle, json);
        Assert.Equal(TyreProfile.TruckOnAsphalt, back.Tyres);
        Assert.Equal(TyreMechanics.TruckOnAsphalt, back.Tyres!.Mechanics);
        Assert.Equal(json, ModelLibrary.SpecJson(back));
        // The editor's fields for a tyre: the same members, each still to be changed.
        var fields = ModelKinds.Describe(typeof(TyreProfile));
        Assert.Equal(was, fields.Select(f => f.Name));
        Assert.All(fields, f => Assert.NotNull(f.Field));
    }

    /// <summary>
    /// A vehicle's physics half is its running gear, its tyres' mechanics and its mass, and the wheel model is
    /// made from that half alone: the same model as before, on the same tyres.
    /// </summary>
    [Fact]
    public void A_vehicles_wheels_are_made_from_its_mechanics()
    {
        foreach (string id in new[] { "i4_midsize", "diesel_truck", "sportbike", "transit_bus" })
        {
            var v = MachineRegistry.VehicleFor(id);
            var half = v.Mechanics;
            // (Its running gear is placed anew each time it is asked for: the same axles in the same places.)
            Assert.Equal(v.Running.Axles.Select(x => (x.Z, x.TrackMetres, x.Driven, x.Steered)), half.Running.Axles.Select(x => (x.Z, x.TrackMetres, x.Driven, x.Steered)));
            Assert.Equal(v.Running.Wheelbase, half.Running.Wheelbase);
            Assert.Same(v.Tyres.Mechanics, half.Tyres);
            Assert.Equal(v.MassKg, half.MassKg);
            var wheels = new WheelDynamics(half);
            Assert.Same(half.Tyres, wheels.Tyre);
            Assert.Equal(v.Tyres.PeakGripG, wheels.GripG);
            Assert.Equal(v.MassKg, wheels.Mass);
            Assert.Equal(0.6f, new WheelDynamics(half, 0.6f).GripG);
            Assert.Equal(v.Running.Axles.Sum(a => a.Wheels), wheels.Wheels.Length);
        }
        // The wheel model is the physics' own now.
        Assert.Equal("OpenFPS.Physics", typeof(WheelDynamics).Assembly.GetName().Name);
        Assert.Equal("OpenFPS.Physics", typeof(TyreMechanics).Assembly.GetName().Name);
        Assert.Equal("OpenFPS.Sound", typeof(TyreProfile).Assembly.GetName().Name);
    }
}
