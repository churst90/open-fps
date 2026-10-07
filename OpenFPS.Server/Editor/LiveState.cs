using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Editor;

/// <summary>
/// What a placed thing is doing, as against what it is: kept across <see cref="WorldEditor.Remake"/>, so a
/// new version of its prefab or model does not shut an open door, light a fire again or start a machine
/// that was off. Only running state is carried; everything the model says comes from the new one.
/// </summary>
internal readonly record struct LiveState(DoorComponent? Door, bool Parented, SoundEmitterComponent? Emitter, HealthComponent? Health)
{
    public static LiveState Of(World world, Entity e) => new(
        world.Has<DoorComponent>(e) ? world.Get<DoorComponent>(e) : null,
        world.Has<ParentComponent>(e),
        world.Has<SoundEmitterComponent>(e) ? world.Get<SoundEmitterComponent>(e) : null,
        world.Has<HealthComponent>(e) ? world.Get<HealthComponent>(e) : null);

    /// <summary>Where the thing is made again: an open door's leaf has swung away from its doorway, so
    /// it is made shut where its doorway is and swung open again after.</summary>
    public Pose Rest(Pose now)
        => Door is { Captured: true } d && !Parented
            ? now with { Position = d.ShutPosition, Rotation = Quaternion.CreateFromYawPitchRoll(d.ShutYaw, 0f, 0f) }
            : now;

    /// <summary>Puts the running state on the thing made again, where it has the same part.</summary>
    public void OnTo(World world, Entity made)
    {
        if (Door is { } d && world.Has<DoorComponent>(made))
        {
            ref var nd = ref world.Get<DoorComponent>(made);
            nd.Openness = d.Openness;
            nd.Target = d.Target;
            nd.ClearSeconds = d.ClearSeconds;
            nd.SelfClosing = d.SelfClosing;
            nd.Travel = d.Travel;
            nd.KeyTurned = d.KeyTurned;
            nd.OpenedFrom = d.OpenedFrom;
            nd.HandId = d.HandId;
            nd.KeySeconds = d.KeySeconds;
            if (!Parented) DoorSystem.Settle(world, made);
        }
        if (Emitter is { } em && world.Has<SoundEmitterComponent>(made))
        {
            ref var ne = ref world.Get<SoundEmitterComponent>(made);
            ne.SynthRunning = em.SynthRunning;
            ne.ServingStop = em.ServingStop;
            ne.WindowsOpen = em.WindowsOpen;
            ne.SoundId = WithLitTime(em.SoundId, ne.SoundId);
        }
        if (Health is { } h && world.Has<HealthComponent>(made))
        {
            ref var nh = ref world.Get<HealthComponent>(made);
            nh.Current = nh.Max > 0 ? Math.Min(h.Current, nh.Max) : h.Current;
        }
    }

    /// <summary>A fire's sound key made again keeps the moment the fire was lit, and so how far it has
    /// grown: "fire:campfire" after "fire:fire_pit/lit=1234.5" is "fire:campfire/lit=1234.5".</summary>
    internal static string WithLitTime(string was, string now)
    {
        if (!ModelKinds.TryModelOfSound(was, out var kind, out _) || kind != ModelLibrary.Kinds.Fire
            || !ModelKinds.TryModelOfSound(now, out var newKind, out _) || newKind != ModelLibrary.Kinds.Fire)
            return now;
        FireSpec.ParseKey(was, out _, out double? lit);
        FireSpec.ParseKey(now, out string preset, out double? litNow);
        return lit is double t && litNow == null ? FireSpec.KeyFor(preset, t) : now;
    }
}
