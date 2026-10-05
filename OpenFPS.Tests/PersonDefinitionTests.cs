using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What a client is told about another player: who they are and what they are made of. Walking into
/// Sean said "something" and knocked like stone (Cody, 2026-10-05), because the definition carried no
/// name and a player's material was "Generic", later the floor under them.
/// </summary>
public class PersonDefinitionTests
{
    [Fact]
    public void APlayersDefinitionNamesThemAndIsABody()
    {
        var world = World.Create();
        try
        {
            var sean = world.Create(
                new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new Velocity(),
                new PlayerComponent { ConnectionId = 7, Username = "seanterry01" },
                EntityType.Player,
                new MaterialComponent { Material = PhysicsConstants.PersonMaterial },
                new ColliderComponent { Shape = ColliderShape.Cylinder, Size = PhysicsConstants.PlayerSize, IsSolid = true });
            var def = EntityDefinitionFactory.From(world, sean);
            Assert.Equal("seanterry01", def.Identity.Name);
            Assert.Equal(PhysicsConstants.PersonMaterial, def.Material.Material);
            Assert.Equal(Beacons.Player, def.Identity.BeaconCategory);
            Assert.False(def.Identity.Announce);
            // The name is in what is sent, not on the body: nothing on the server that looks things up
            // by identity starts finding people.
            Assert.False(world.Has<IdentityComponent>(sean));
        }
        finally { World.Destroy(world); }
    }
}
