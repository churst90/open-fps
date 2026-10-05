using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Where a door's sound comes from (Cody, 2026-10-05: "most of the time I just couldn't hear the sound
/// of the doors, like the front doors"; "should the sound not follow the direction of the opening door?").
///
/// A knob door's sound was placed on the leaf's latch edge, which laps the jamb by 50 mm: inside the
/// brick, and inside the leaf. Measured with --path-probe at 58 Alder Street's front door from three
/// metres in plain view, the simulator heard it through both, −30/−46/−83 dB. A patio door's whole run
/// was placed at the middle of its own glass leaf, where it started, and stayed there while the leaf
/// slid a metre.
///
/// Now every door sound is at the handle — the backset in from the free edge, never in the jamb — with
/// the leaf's faces to be heard from, and a slider's run travels with its handle.
/// </summary>
public class DoorSoundPlacementTests : IDisposable
{
    private const float Dt = PhysicsConstants.FixedDeltaTime;
    private readonly ITestOutputHelper _o;
    private readonly World _world = World.Create();
    private readonly DoorSystem _doors = new();
    private readonly PrefabRepository _prefabs = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));
    private readonly List<(string Key, IReadOnlyList<TransientSound> Sounds)> _heard = new();

    public DoorSoundPlacementTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() => World.Destroy(_world);

    private Entity Door(string prefab, float yawRadians = 0f)
    {
        var e = _prefabs.Spawn(_world, prefab, new Vector3(3f, 1.05f, -2f),
                               Quaternion.CreateFromYawPitchRoll(yawRadians, 0f, 0f), Vector3.One);
        Tick(1);
        return e;
    }

    private void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++)
            _doors.Update(_world, Dt, _ => { }, (_, key, sounds) => _heard.Add((key, sounds)));
    }

    private void TickSeconds(float s) => Tick((int)MathF.Ceiling(s / Dt));

    /// <summary>The point in the leaf's shut frame: x across the doorway, z through it.</summary>
    private static Vector3 Local(Vector3 p, Vector3 centre, Quaternion rotation)
        => Vector3.Transform(p - centre, Quaternion.Inverse(rotation));

    /// <summary>
    /// Every sound a door makes, of every kind, opening and shutting, is within the leaf's width less the
    /// handle's backset — so never in the wall the leaf laps — and carries the leaf's faces.
    /// </summary>
    [Theory]
    [InlineData("door")]
    [InlineData("steel_door")]
    [InlineData("glass_front_door")]
    [InlineData("glass_pull_door")]
    [InlineData("patio_door")]
    [InlineData("auto_sliding_door")]
    public void EveryDoorSoundIsInTheDoorwayAndHasFaces(string prefab)
    {
        var e = Door(prefab, yawRadians: 0.6f);
        var shut = _world.Get<Transform>(e);
        var d = _world.Get<DoorComponent>(e);
        float half = _world.Get<ColliderComponent>(e).Size.X * 0.5f;
        DoorSystem.Set(_world, e, open: true, by: shut.Position + new Vector3(0f, 0f, 1.5f));
        TickSeconds(d.SwingSeconds + 0.5f);
        DoorSystem.Set(_world, e, open: false);
        TickSeconds(MathF.Max(d.SwingSeconds, d.CloseSeconds) + 1.5f);

        var sounds = _heard.SelectMany(h => h.Sounds).ToList();
        Assert.NotEmpty(sounds);
        foreach (var s in sounds)
        {
            Assert.True(s.FaceNormal.Length() > DoorSystem.FaceStandoffMetres, $"{prefab}: a sound with no faces");
            if (d.Slides)
            {
                // A slider does not turn: everything is in its plane, within the doorway it crosses.
                foreach (var p in new[] { s.Position, s.MoveSeconds > 0f ? s.MovesTo : s.Position })
                {
                    var l = Local(p, shut.Position, shut.Rotation);
                    Assert.InRange(l.Z, -1e-3f, 1e-3f);
                    // Shut, the handle is a backset in from the leading edge; open, a backset in from the far jamb.
                    float x = l.X * d.HingeSide;
                    Assert.InRange(x, -half + DoorSystem.HandleBacksetMetres - 1e-3f, half - DoorSystem.HandleBacksetMetres + 1e-3f);
                }
            }
            else
            {
                // A hinged leaf's sounds happen with it shut: inside the leaf's width by the backset.
                var l = Local(s.Position, shut.Position, shut.Rotation);
                Assert.InRange(MathF.Abs(l.X), 0f, half - DoorSystem.HandleBacksetMetres + 1e-3f);
                Assert.InRange(l.Z, -1e-3f, 1e-3f);
            }
        }
        _o.WriteLine($"{prefab}: {sounds.Count} sound(s), all in the doorway");
    }

    /// <summary>
    /// A patio door's run comes from its handle and goes with it: opening, from a backset in from one
    /// jamb across to a backset in from the other, over the time the leaf takes; shutting, back. Never
    /// further: open, the handle itself is behind the far jamb, and the sound stays in the doorway.
    /// </summary>
    [Fact]
    public void ASlidingDoorsRunTravelsWithItsHandle()
    {
        var e = Door("patio_door");
        var shut = _world.Get<Transform>(e);
        var d = _world.Get<DoorComponent>(e);
        float half = _world.Get<ColliderComponent>(e).Size.X * 0.5f;
        float reach = half - DoorSystem.HandleBacksetMetres;

        DoorSystem.Set(_world, e, open: true);
        TickSeconds(d.SwingSeconds + 0.5f);
        var opening = _heard.SelectMany(h => h.Sounds).Single(s => s.MoveSeconds > 0f);
        float from = Local(opening.Position, shut.Position, shut.Rotation).X * d.HingeSide;
        float to = Local(opening.MovesTo, shut.Position, shut.Rotation).X * d.HingeSide;
        _o.WriteLine($"opening: handle from {from:F2} to {to:F2} m across, over {opening.MoveSeconds:F2} s");
        // From where the leaf is when the run is announced, a tick into its travel.
        Assert.InRange(from, -reach - 1e-3f, -reach + 0.03f);
        Assert.Equal(reach, to, 2);
        Assert.InRange(opening.MoveSeconds, d.SwingSeconds * 0.9f, d.SwingSeconds * 1.05f);

        _heard.Clear();
        DoorSystem.Set(_world, e, open: false);
        TickSeconds(d.SwingSeconds + 1f);
        var closing = _heard.SelectMany(h => h.Sounds).Single(s => s.MoveSeconds > 0f);
        Assert.InRange(Local(closing.Position, shut.Position, shut.Rotation).X * d.HingeSide, reach - 0.03f, reach + 1e-3f);
        Assert.Equal(-reach, Local(closing.MovesTo, shut.Position, shut.Rotation).X * d.HingeSide, 2);
    }

    /// <summary>Each listener hears a door from the face on their own side, the standoff off the leaf.</summary>
    [Fact]
    public void ADoorIsHeardFromTheFaceOnYourSide()
    {
        var onLeaf = new Vector3(1f, 1f, 0f);
        var face = new Vector3(0f, 0f, 0.28f);
        Assert.Equal(new Vector3(1f, 1f, 0.28f), TransientSound.FacingListener(onLeaf, face, new Vector3(0f, 1.6f, 3f)));
        Assert.Equal(new Vector3(1f, 1f, -0.28f), TransientSound.FacingListener(onLeaf, face, new Vector3(2f, 1.6f, -3f)));
        Assert.Equal(onLeaf, TransientSound.FacingListener(onLeaf, Vector3.Zero, new Vector3(0f, 1.6f, 3f)));
        Assert.Equal(new Vector3(0.5f, 0f, 0f), TransientSound.Along(Vector3.Zero, Vector3.UnitX, 2f, 1f));
        Assert.Equal(Vector3.UnitX, TransientSound.Along(Vector3.Zero, Vector3.UnitX, 2f, 5f));
    }

    /// <summary>The fields a door's sound carries survive the wire.</summary>
    [Fact]
    public void TheMotionAndTheFacesGoOverTheWire()
    {
        var sent = new OpenFPS.Common.Networking.WorldAudioEvent
        {
            SourceEntityId = 7, Label = "door",
            Sounds = new List<TransientSound>
            {
                new TransientSound
                {
                    Position = new Vector3(1, 2, 3), MovesTo = new Vector3(4, 2, 3), MoveSeconds = 1.4f,
                    FaceNormal = new Vector3(0, 0, 0.26f), SynthKey = "door:test",
                },
            },
        };
        var bytes = MemoryPack.MemoryPackSerializer.Serialize(sent);
        var got = MemoryPack.MemoryPackSerializer.Deserialize<OpenFPS.Common.Networking.WorldAudioEvent>(bytes)!;
        var s = Assert.Single(got.Sounds);
        Assert.Equal(new Vector3(4, 2, 3), s.MovesTo);
        Assert.Equal(1.4f, s.MoveSeconds);
        Assert.Equal(new Vector3(0, 0, 0.26f), s.FaceNormal);
    }
}
