using System.Numerics;
using System.Reflection;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// The audio world's sources as the client fills them (docs/SOUND_LIBRARY_BOUNDARY.md, stage 7, decision 10): the
/// library's <see cref="SourceSpec"/> is the emitter component field by field, without its wire format, so a field
/// appended to the component and not to the spec, or not copied across, is a source the audio world hears wrong.
/// </summary>
public class AudioSourcesTests
{
    private static PropertyInfo[] EmitterFields()
        => typeof(SoundEmitterComponent).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void EveryEmitterFieldHasACounterpart()
    {
        var missing = new List<string>();
        foreach (var field in EmitterFields())
        {
            var twin = typeof(SourceSpec).GetProperty(field.Name, BindingFlags.Public | BindingFlags.Instance);
            if (twin == null || twin.PropertyType != field.PropertyType) missing.Add($"{field.PropertyType.Name} {field.Name}");
        }
        Assert.True(missing.Count == 0, "SourceSpec has no counterpart for: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheClientCopiesEveryField()
    {
        // Every field a value of its own, none its default, so a field left behind reads as its default and differs.
        var emitter = new SoundEmitterComponent();
        object boxed = emitter;
        int n = 1;
        foreach (var field in EmitterFields())
        {
            if (!field.CanWrite) continue;
            object value = field.PropertyType switch
            {
                var t when t == typeof(float) => n + 0.25f,
                var t when t == typeof(int) => n,
                var t when t == typeof(bool) => !(bool)field.GetValue(boxed)!,
                var t when t == typeof(string) => "s" + n,
                var t when t == typeof(Vector3) => new Vector3(n, n + 1, n + 2),
                var t when t == typeof(Vector3[]) => new[] { new Vector3(n, 0f, 0f), new Vector3(n + 1, 0f, n + 2) },
                var t when t == typeof(PlaybackMode) => PlaybackMode.Sequential,
                var t => throw new InvalidOperationException($"no test value for {t.Name} {field.Name}"),
            };
            field.SetValue(boxed, value);
            n++;
        }
        emitter = (SoundEmitterComponent)boxed;
        var def = new EntityDefinition
        {
            EntityId = 5,
            SoundEmitter = emitter,
            Collider = new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(2f, 3f, 4f) },
        };

        var spec = SnapshotSources.SpecOf(def);
        foreach (var field in EmitterFields())
        {
            var twin = typeof(SourceSpec).GetProperty(field.Name)!;
            object? mine = field.GetValue(emitter), theirs = twin.GetValue(spec);
            // A line is compared point by point: a copy of its points is as good as its own array.
            bool same = mine is Vector3[] points && theirs is Vector3[] copied
                ? points.AsSpan().SequenceEqual(copied)
                : Equals(mine, theirs);
            Assert.True(same,
                        $"{field.Name}: the component has {field.GetValue(emitter)}, the spec {twin.GetValue(spec)}");
        }
        Assert.Equal(ColliderShape.Cylinder, spec.Shape);
        Assert.Equal(new Vector3(2f, 3f, 4f), spec.Size);
    }

    [Fact]
    public void AnUnauthoredVehicleSoundsFromItsExhaust()
    {
        // The offset a kind implies is the host's (EntityEmission.LocalOffset): the spec carries it worked out.
        var def = new EntityDefinition { EntityId = 9 };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "engine:i4_economy";
        var spec = SnapshotSources.SpecOf(def);
        Assert.Equal(EntityEmission.LocalOffset(def), spec.Offset);
        Assert.NotEqual(Vector3.Zero, spec.Offset);

        var car = new AudioSource(9);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        car.Set(spec, new SourceState { Position = new Vector3(10f, 0f, 20f), Rotation = turned });
        var snap = new EntitySnapshot { Id = 9, Definition = def, Transform = new Transform { Position = new Vector3(10f, 0f, 20f), Rotation = turned } };
        Assert.Equal(EntityEmission.PointFor(snap), car.Emission);
        Assert.Equal(EntityEmission.OcclusionRadiusFor(snap), car.ProbeRadius);
    }

    [Fact]
    public void AFrameReadsItsOwnSnapshot()
    {
        // A source asked about twice in a frame is one answer; the next frame's snapshot is a new one.
        var state = new ClientWorldState();
        state.Clear(new Vector3(100f, 50f, 100f));
        var def = new EntityDefinition
        {
            EntityId = 3, Type = EntityType.NPC, Moves = true,
            Transform = new Transform { Position = new Vector3(1f, 0f, 1f), Rotation = Quaternion.Identity },
        };
        def.SoundEmitter.SoundId = "a";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        state.RegisterDefinition(def);
        var sources = new SnapshotSources();
        sources.Refill(state.GetSnapshot());
        Assert.True(sources.TryGet(3, out var first));
        Assert.Equal(new Vector3(1f, 0f, 1f), first.State.Position);
        Assert.Contains(3, sources.Audible);
        Assert.Equal(1, sources.MovingCount);
        Assert.Same(first, sources.MovingAt(0));

        state.SyncState(new[] { new EntityState
        {
            EntityId = 3,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(4f, 0f, 1f), Rotation = Quaternion.Identity }),
        } });
        // Not refilled: still the frame's own.
        Assert.True(sources.TryGet(3, out var again));
        Assert.Equal(new Vector3(1f, 0f, 1f), again.State.Position);
        sources.Refill(state.GetSnapshot());
        Assert.True(sources.TryGet(3, out var next));
        Assert.Equal(4f, next.State.Position.X, 2);
        Assert.False(sources.TryGet(4, out _));
    }
}
