using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// A machine somebody walks behind has a person with it, to the scope and the people tracker (Cody's
/// decision of 2026-11-17, docs/SPECIAL_CASES_AUDIT.md P2).
/// </summary>
public class ScopeOperatorTests
{
    private static readonly Vector3 Eye = new(0f, 1.7f, 0f);

    private static EntitySnapshot Body(int id, string name, string soundId, Vector3 at)
    {
        var def = new EntityDefinition { EntityId = id, Type = EntityType.NPC, Moves = true };
        def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.6f, 1f, 0.9f), IsSolid = true };
        def.Identity = new IdentityComponent { Name = name };
        def.SoundEmitter = new SoundEmitterComponent { SoundId = soundId, IsSynth = true };
        return new EntitySnapshot { Id = id, Definition = def, Transform = new Transform { Position = at, Rotation = Quaternion.Identity } };
    }

    [Fact]
    public void AWalkedMachineIsAPersonAndTheOtherBodiesAreAsBefore()
    {
        // The city's own id for a push mower: somebody walks behind it, so the scope sees a person.
        var push = Body(1, "Mower, garden 2", "machine:mower_push", new Vector3(0f, 0f, 20f));
        Assert.Equal(SightKind.Person, SightKinds.Classify(EntityAcoustics.BodyOf(push)));

        // A riding mower and an air conditioner have nobody walking them: neither is picked out.
        Assert.Null(SightKinds.Classify(EntityAcoustics.BodyOf(Body(2, "Verge mower", "machine:mower_riding", Vector3.Zero))));
        Assert.Null(SightKinds.Classify(EntityAcoustics.BodyOf(Body(3, "Air conditioner", "machine:ac_condenser", Vector3.Zero))));

        // The other bodies answer as they did.
        Assert.Equal(SightKind.Vehicle, SightKinds.Classify(EntityAcoustics.BodyOf(Body(4, "Hatchback", "engine:hatchback", Vector3.Zero))));
        Assert.Equal(SightKind.Aircraft, SightKinds.Classify(EntityAcoustics.BodyOf(Body(5, "Airliner", "aircraft:piston_single", Vector3.Zero))));
        Assert.Equal(SightKind.Train, SightKinds.Classify(EntityAcoustics.BodyOf(Body(6, "Light rail", "rail:light_rail/a/0", Vector3.Zero))));
    }

    [Fact]
    public void TheScopePicksOutAPushMowerInFrontAsAPerson()
    {
        var snap = new WorldSnapshot();
        var mower = Body(1, "Mower, garden 2", "machine:mower_push", new Vector3(0f, 0f, 200f));
        snap.Entities[mower.Id] = mower;
        snap.DynamicEntities.Add(mower);

        // The crosshair on the mower's chest, as a shooter would put it.
        Vector3 chest = mower.Transform.Position + new Vector3(0f, ScopeView.ChestHeight, 0f);
        Vector3 forward = Vector3.Normalize(chest - Eye);
        var seen = ScopeView.InView(snap, new SpatialService(), -1, Eye,
                                    MathF.Atan2(forward.X, forward.Z), -MathF.Asin(forward.Y), 6f, 600f);

        var sight = Assert.Single(seen);
        Assert.Equal(SightKind.Person, sight.Kind);
        Assert.Equal("person", sight.Name);
        Assert.Equal(chest, sight.AimPoint);
        Assert.True(sight.OnCrosshair);
        var scope = new ScopeController();
        scope.Raise("m700", "scope_4_12", numLockOn: true);
        Assert.Equal("person, 200 metres, on the crosshair, standing still.",
                     scope.NextTarget(seen, +1, forward));
    }

    [Fact]
    public void ThePeopleTrackerListsAPushMowerAndTheVehiclesTrackerDoesNot()
    {
        var world = new WorldSnapshot();
        var mower = Body(1, "Mower, garden 2", "machine:mower_push", new Vector3(0f, 0f, 20f));
        world.Entities[mower.Id] = mower;

        var people = MapTracker.Gather(world, Vector3.Zero, TrackCategory.People, -1);
        Assert.Equal(new[] { "person" }, people.Select(p => p.Name));
        Assert.Empty(MapTracker.Gather(world, Vector3.Zero, TrackCategory.Vehicles, -1));
    }
}
