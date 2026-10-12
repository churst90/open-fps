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

    // ── The vehicle ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each mechanical number of a vehicle is in one place, its physics half: the sound's half reads and writes
    /// it member by member, so the server's car, the wheel model and the sound's own driveline cannot hold
    /// different masses.
    /// </summary>
    [Fact]
    public void A_vehicles_mechanical_members_are_its_mechanics()
    {
        var mechanical = typeof(VehicleMechanics).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.SetMethod != null && m.Name is not (nameof(VehicleMechanics.Tyres) or nameof(VehicleMechanics.Engine))).ToList();
        Assert.Equal(new[] { "MassKg", "DragArea", "RollingResistance", "FrontAxleZ", "RearAxleZ", "LengthMetres", "WidthMetres",
                             "HeightMetres", "TyreCount", "Chassis", "Gearbox" }, mechanical.Select(m => m.Name));
        var v = MachineRegistry.VehicleFor("diesel_truck");
        var clone = typeof(VehicleProfile).GetMethod("<Clone>$")!;
        float n = 1f;
        foreach (var m in mechanical)
        {
            var onSound = typeof(VehicleProfile).GetProperty(m.Name);
            Assert.True(onSound != null, $"VehicleProfile has no {m.Name}");
            Assert.Equal(m.GetValue(v.Mechanics), onSound!.GetValue(v));
            object value = m.PropertyType == typeof(float) ? 0.5f + n++
                         : m.PropertyType == typeof(int) ? 6
                         : m.Name == "Gearbox" ? Gearbox.SixSpeedSports
                         : ChassisSpec.Default(2, 0.8f, 200f);
            var set = (VehicleProfile)clone.Invoke(v, null)!;
            onSound.SetValue(set, value);
            Assert.Equal(value, m.GetValue(set.Mechanics));
            // Nothing else of the mechanics moved, and the vehicle it was copied from is as it was.
            foreach (var other in mechanical.Where(o => o != m))
                Assert.Equal(other.GetValue(v.Mechanics), other.GetValue(set.Mechanics));
            Assert.Same(v.Mechanics.Tyres, set.Mechanics.Tyres);
            Assert.Same(v.Mechanics.Engine, set.Mechanics.Engine);
            Assert.NotEqual(value, m.GetValue(v.Mechanics));
        }
        // The sound's own numbers are not the mechanics'.
        Assert.Null(typeof(VehicleMechanics).GetProperty(nameof(VehicleProfile.SourceLevelDb)));
        Assert.Null(typeof(VehicleMechanics).GetProperty(nameof(VehicleProfile.ExhaustOffsetZ)));
        // A vehicle nobody has set a number of has the numbers a vehicle always had.
        var plain = new VehicleMechanics();
        Assert.Equal((1620f, 0.62f, 0.013f, 1.25f, -1.35f), (plain.MassKg, plain.DragArea, plain.RollingResistance, plain.FrontAxleZ, plain.RearAxleZ));
        Assert.Equal((4.6f, 1.9f, 1.4f, 4), (plain.LengthMetres, plain.WidthMetres, plain.HeightMetres, plain.TyreCount));
        Assert.Null(plain.Chassis);
    }

    /// <summary>
    /// A vehicle's tyres and gearbox are its mechanics' too, the same objects, whatever it was made from: a
    /// preset, a parts list, or another vehicle with other tyres. And its mechanics are held, not made each time
    /// they are asked for.
    /// </summary>
    [Fact]
    public void A_vehicles_tyres_and_gearbox_are_its_mechanics_too()
    {
        MachineRegistry.EnsureLoaded(Path.Combine(AppContext.BaseDirectory, "machines"));
        int vehicles = 0;
        foreach (string id in MachineRegistry.Ids)
        {
            var v = MachineRegistry.VehicleFor(id);
            vehicles++;
            Assert.Same(v.Tyres.Mechanics, v.Mechanics.Tyres);
            Assert.Same(v.Engine.Mechanics, v.Mechanics.Engine);
            Assert.Same(v.Gearbox, v.Mechanics.Gearbox);
            Assert.Same(v.Mechanics, v.Mechanics);
            Assert.Equal(v.MassKg, v.Mechanics.MassKg);
            Assert.Equal(v.Running.Wheelbase, v.Mechanics.Running.Wheelbase);
        }
        Assert.True(vehicles >= 40, $"only {vehicles} vehicles");

        var car = MachineRegistry.VehicleFor("i4_midsize");
        var onSlicks = car with { Tyres = TyreProfile.RaceSlick };
        Assert.Same(onSlicks.Tyres.Mechanics, onSlicks.Mechanics.Tyres);
        Assert.Equal(3.1f, onSlicks.Mechanics.Tyres.PeakGripG);
        Assert.Equal(car.MassKg, onSlicks.MassKg);
        // The car it was made from keeps its own tyres.
        Assert.Same(car.Tyres.Mechanics, car.Mechanics.Tyres);
        Assert.NotEqual(3.1f, car.Mechanics.Tyres.PeakGripG);
        // A grip changed on the sound's half of the tyres reaches the vehicle's mechanics with the tyres.
        var worn = car with { Tyres = car.Tyres with { PeakGripG = 0.6f } };
        Assert.Equal(0.6f, worn.Mechanics.Tyres.PeakGripG);
        Assert.Equal(0.6f, new WheelDynamics(worn.Mechanics).GripG);
    }

    // ── The engine ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four numbers of an engine that move a car (where it idles, how far it revs, the most torque it
    /// makes and where) are in one place, its physics half: the sound's half reads and writes it member by
    /// member, and keeps what the editor says of each.
    /// </summary>
    [Fact]
    public void An_engines_mechanical_members_are_its_mechanics()
    {
        var mechanical = typeof(EngineMechanics).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(m => m.SetMethod != null).ToList();
        Assert.Equal(new[] { "IdleRpm", "RedlineRpm", "PeakTorqueNm", "PeakTorqueRpm" }, mechanical.Select(m => m.Name));
        int engines = 0;
        foreach (var (id, make) in EngineProfile.Presets)
        {
            var e = make();
            engines++;
            Assert.Equal((e.IdleRpm, e.RedlineRpm, e.PeakTorqueNm, e.PeakTorqueRpm),
                         (e.Mechanics.IdleRpm, e.Mechanics.RedlineRpm, e.Mechanics.PeakTorqueNm, e.Mechanics.PeakTorqueRpm));
            Assert.True(e.Mechanics.IdleRpm > 0f && e.Mechanics.RedlineRpm > e.Mechanics.IdleRpm, $"{id}: idles at {e.IdleRpm}, revs to {e.RedlineRpm}");
            Assert.Same(e.Mechanics, e.Mechanics);
        }
        Assert.True(engines >= 40, $"only {engines} engines");

        var engine = EngineProfile.Presets.First().Value();
        var clone = typeof(EngineProfile).GetMethod("<Clone>$")!;
        float n = 1f;
        foreach (var m in mechanical)
        {
            var onSound = typeof(EngineProfile).GetProperty(m.Name);
            Assert.True(onSound != null, $"EngineProfile has no {m.Name}");
            float value = 1234.5f + n++;
            var set = (EngineProfile)clone.Invoke(engine, null)!;
            onSound!.SetValue(set, value);
            Assert.Equal(value, (float)m.GetValue(set.Mechanics)!);
            foreach (var other in mechanical.Where(o => o != m))
                Assert.Equal((float)other.GetValue(engine.Mechanics)!, (float)other.GetValue(set.Mechanics)!);
            Assert.NotEqual(value, (float)m.GetValue(engine.Mechanics)!);
            Assert.True(onSound.GetCustomAttribute<TunableAttribute>() != null, $"EngineProfile.{m.Name} lost its editor's attribute");
        }
        // The sound's own numbers are not the mechanics': its inertia and friction are read by its own engine only.
        Assert.Null(typeof(EngineMechanics).GetProperty(nameof(EngineProfile.InertiaKgM2)));
        Assert.Null(typeof(EngineMechanics).GetProperty(nameof(EngineProfile.FrictionNm)));
        Assert.Null(typeof(EngineMechanics).GetProperty(nameof(EngineProfile.BoreMm)));
        // An engine nobody set the torque of makes what an engine always did.
        Assert.Equal((500f, 4200f), (new EngineMechanics().PeakTorqueNm, new EngineMechanics().PeakTorqueRpm));
        Assert.Equal("OpenFPS.Physics", typeof(EngineMechanics).Assembly.GetName().Name);
    }

    /// <summary>
    /// An engine is stored and edited in the shape it had: the four numbers where they were among its
    /// members, no part named for its mechanics, and it reads back the same.
    /// </summary>
    [Fact]
    public void An_engine_is_stored_and_edited_in_the_shape_it_had()
    {
        var engine = EngineProfile.Presets.First().Value();
        string json = ModelLibrary.SpecJson(engine);
        var keys = JsonNode.Parse(json)!.AsObject().Select(kv => kv.Key).ToList();
        Assert.DoesNotContain("Mechanics", keys);
        // Each still between the members it stood between.
        Assert.Equal(new[] { "IdleRoughness", "IdleRpm", "RedlineRpm", "CrankingRpm" }, keys.Skip(keys.IndexOf("IdleRoughness")).Take(4));
        Assert.Equal(new[] { "FrictionNmPerKrpm", "PeakTorqueNm", "PeakTorqueRpm", "IdleGovernorGain" }, keys.Skip(keys.IndexOf("FrictionNmPerKrpm")).Take(4));
        var back = (EngineProfile)ModelLibrary.FromSpecJson(ModelLibrary.Kinds.Engine, json);
        Assert.Equal(engine.Mechanics, back.Mechanics);
        Assert.Equal(json, ModelLibrary.SpecJson(back));
        // An engine saved without its idle is refused, as it was: the member is still required.
        var without = JsonNode.Parse(json)!.AsObject();
        without.Remove("IdleRpm");
        Assert.ThrowsAny<Exception>(() => ModelLibrary.FromSpecJson(ModelLibrary.Kinds.Engine, without.ToJsonString()));
        // The editor lists the four as the engine's own, each to be changed.
        var fields = ModelKinds.Describe(typeof(EngineProfile));
        Assert.DoesNotContain(fields, f => f.Name == "Mechanics");
        foreach (string name in new[] { "IdleRpm", "RedlineRpm", "PeakTorqueNm", "PeakTorqueRpm" })
            Assert.NotNull(fields.Single(f => f.Name == name).Field);
    }

    /// <summary>
    /// A vehicle's engine's mechanics are its mechanics' too, the same object, and a vehicle given another
    /// engine has that engine's.
    /// </summary>
    [Fact]
    public void A_vehicles_engine_is_its_mechanics_too()
    {
        var car = MachineRegistry.VehicleFor("i4_midsize");
        Assert.Same(car.Engine.Mechanics, car.Mechanics.Engine);
        Assert.Equal(car.Engine.PeakTorqueNm, car.Mechanics.Engine.PeakTorqueNm);
        var other = MachineRegistry.VehicleFor("diesel_truck").Engine;
        var swapped = car with { Engine = other };
        Assert.Same(other.Mechanics, swapped.Mechanics.Engine);
        Assert.Equal(other.RedlineRpm, swapped.Mechanics.Engine.RedlineRpm);
        Assert.Equal(car.MassKg, swapped.Mechanics.MassKg);
        Assert.Same(car.Tyres.Mechanics, swapped.Mechanics.Tyres);
        // The car it was made from keeps its own engine.
        Assert.Same(car.Engine.Mechanics, car.Mechanics.Engine);
        Assert.NotEqual(other.RedlineRpm, car.Mechanics.Engine.RedlineRpm);
        // An engine retuned on the sound's half reaches the vehicle's mechanics with the engine.
        var tuned = car with { Engine = car.Engine with { PeakTorqueNm = 777f } };
        Assert.Equal(777f, tuned.Mechanics.Engine.PeakTorqueNm);
    }
}
