using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>Tests for extracting Steam Audio scene geometry from a WorldSnapshot (Phase 4b).</summary>
public class SteamAudioSceneTests
{
    private static void Add(WorldSnapshot w, int id, Vector3 pos, Vector3 size, bool solid,
        ColliderShape shape = ColliderShape.Box, string material = "Concrete", string soundId = "")
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Collider = new ColliderComponent { Shape = shape, Size = size, IsSolid = solid },
            Material = new MaterialComponent { Material = material },
            SoundEmitter = new SoundEmitterComponent { SoundId = soundId },
        };
        w.Entities[id] = new EntitySnapshot
        {
            Id = id,
            Definition = def,
            Transform = new Transform { Position = pos, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Velocity = Vector3.Zero,
        };
    }

    [Fact]
    public void BoxesFromWorld_IncludesOnlySolidBoxesWithPositiveSize()
    {
        var w = new WorldSnapshot();
        Add(w, 1, new Vector3(1, 2, 3), new Vector3(4, 4, 4), solid: true);                       // included
        Add(w, 2, new Vector3(0, 0, 0), new Vector3(4, 4, 4), solid: false);                      // excluded: not solid
        Add(w, 3, new Vector3(0, 0, 0), new Vector3(4, 4, 4), solid: true, shape: ColliderShape.Sphere); // excluded: not a box
        Add(w, 4, new Vector3(0, 0, 0), new Vector3(0, 4, 4), solid: true);                       // excluded: zero extent

        var boxes = SteamAudioScene.BoxesFromWorld(w);

        Assert.Single(boxes);
        Assert.Equal(new Vector3(1, 2, 3), boxes[0].Center);
        Assert.Equal(new Vector3(4, 4, 4), boxes[0].Size);
        Assert.Equal("Concrete", boxes[0].Material);
    }

    [Fact]
    public void BoxesFromWorld_CarriesMaterialAndTransform()
    {
        var w = new WorldSnapshot();
        Add(w, 7, new Vector3(5, 0, -5), new Vector3(2, 8, 2), solid: true, material: "Wood");
        var boxes = SteamAudioScene.BoxesFromWorld(w);
        Assert.Single(boxes);
        Assert.Equal("Wood", boxes[0].Material);
        Assert.Equal(new Vector3(5, 0, -5), boxes[0].Center);
    }

    [Fact]
    public void BoxesFromWorld_EmptyWorld_ReturnsEmpty()
    {
        Assert.Empty(SteamAudioScene.BoxesFromWorld(new WorldSnapshot()));
    }

    [Fact]
    public void BoxesFromWorld_ExcludesSoundEmitters_SoNoSelfOcclusion()
    {
        // A solid beacon that also emits sound must NOT become occluding geometry — otherwise its own
        // collider sits at its emission point and occludes itself (the megaphone-goes-silent bug).
        var w = new WorldSnapshot();
        Add(w, 1, new Vector3(0, 2, 0), new Vector3(4, 4, 4), solid: true);                         // a wall -> included
        Add(w, 2, new Vector3(7, 1.5f, 15), new Vector3(0.5f, 0.5f, 1f), solid: true, soundId: "BEACONS/megaphone"); // emitter -> excluded

        var boxes = SteamAudioScene.BoxesFromWorld(w);

        Assert.Single(boxes);
        Assert.Equal(new Vector3(0, 2, 0), boxes[0].Center); // only the wall, not the beacon
    }
}
