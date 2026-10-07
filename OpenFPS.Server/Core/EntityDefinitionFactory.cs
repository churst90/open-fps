using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// The <see cref="EntityDefinition"/> a client is sent for an entity. The broadcast and the map and
/// acoustics tests share it: the client builds its acoustic map from these, so a divergence is heard.
/// </summary>
public static class EntityDefinitionFactory
{
    public static EntityDefinition From(World world, Entity e)
    {
        var def = new EntityDefinition { EntityId = e.Id, Type = EntityType.StaticObject };
        if (world.Has<EntityType>(e)) def.Type = world.Get<EntityType>(e);
        def.Identity = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e) : new IdentityComponent();
        def.Collider = world.Has<ColliderComponent>(e) ? world.Get<ColliderComponent>(e) : new ColliderComponent();
        def.Acoustics = world.Has<AcousticComponent>(e) ? world.Get<AcousticComponent>(e) : new AcousticComponent();
        def.Material = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e) : new MaterialComponent();
        def.SoundEmitter = world.Has<SoundEmitterComponent>(e) ? world.Get<SoundEmitterComponent>(e) : new SoundEmitterComponent();
        def.Physics = world.Has<PhysicsPropertyComponent>(e) ? world.Get<PhysicsPropertyComponent>(e) : new PhysicsPropertyComponent();
        def.Region = world.Has<RegionComponent>(e) ? world.Get<RegionComponent>(e) : new RegionComponent();
        def.Portal = world.Has<PortalComponent>(e) ? world.Get<PortalComponent>(e) : new PortalComponent();
        def.Transform = world.Has<Transform>(e) ? world.Get<Transform>(e) : new Transform();
        def.Moves = world.Has<Velocity>(e);
        // A player is a player beacon, with their team for its tone. Set here, not as an IdentityComponent
        // on the body, so server lookups by identity (scan, take, bumping) do not start finding people.
        // A carried thing is neither a beacon nor announced: as beacons, the things you carry took every
        // item beacon's slot (Cody, 2026-10-04). Taking and putting down re-send it.
        if (world.Has<HeldComponent>(e))
        {
            def.Identity.BeaconCategory = "";
            def.Identity.Announce = false;
        }
        if (world.Has<PlayerComponent>(e))
        {
            var player = world.Get<PlayerComponent>(e);
            def.Identity.BeaconCategory = OpenFPS.Common.Beacons.Player;
            // Their name, in the definition only for the same reason: without it, walking into somebody
            // said "something" (Cody, 2026-10-05).
            if (string.IsNullOrWhiteSpace(def.Identity.Name)) def.Identity.Name = player.Username ?? "";
            def.Team = player.Team ?? "";
            // Dead, they are not a player beacon: their body is the item to find. Re-sent on death and on getting up.
            if (world.Has<DeadComponent>(e)) def.Identity.BeaconCategory = "";
        }
        // Anybody in a seat is carried, not walking (EntityDefinition.RidingEntityId): Alex on the bus too.
        if (world.Has<OccupantComponent>(e)) def.RidingEntityId = world.Get<OccupantComponent>(e).RootEntityId;
        return def;
    }

    /// <summary>Every static entity in a world (no <see cref="PlayerComponent"/>, no <see cref="Velocity"/>):
    /// what a MapDataRequest is answered with.</summary>
    public static List<Entity> StaticEntities(World world)
    {
        var result = new List<Entity>();
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) result.Add(e);
        });
        return result;
    }

    /// <summary>The definitions for every static entity, as the client would receive them.</summary>
    public static List<EntityDefinition> StaticDefinitions(World world)
    {
        var entities = StaticEntities(world);
        var defs = new List<EntityDefinition>(entities.Count);
        foreach (var e in entities) defs.Add(From(world, e));
        return defs;
    }
}
