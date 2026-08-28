using System.Collections.Generic;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// Builds the wire-format <see cref="EntityDefinition"/> the server streams to clients on map load.
/// Extracted from <c>GameServer</c> so the live broadcast path and the map/acoustics tests build
/// definitions from exactly the same code — the client generates its acoustic map from these, so a
/// divergence here would not show up until it was audible.
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
        return def;
    }

    /// <summary>
    /// Every static entity in a world — the set the server streams in response to a MapDataRequest.
    /// "Static" means no <see cref="PlayerComponent"/> and no <see cref="Velocity"/>.
    /// </summary>
    public static List<Entity> StaticEntities(World world)
    {
        var result = new List<Entity>();
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) result.Add(e);
        });
        return result;
    }

    /// <summary>Convenience: the definitions for every static entity, as the client would receive them.</summary>
    public static List<EntityDefinition> StaticDefinitions(World world)
    {
        var entities = StaticEntities(world);
        var defs = new List<EntityDefinition>(entities.Count);
        foreach (var e in entities) defs.Add(From(world, e));
        return defs;
    }
}
