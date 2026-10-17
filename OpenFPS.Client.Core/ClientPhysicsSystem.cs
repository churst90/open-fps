using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Physics;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Client.Core;

/// <summary>
/// Client-side prediction of the player's own movement, through the server's SharedMovementEngine, so
/// a key press moves you before the server answers.
/// </summary>
public class ClientPhysicsSystem
{
    private readonly LocalPlayerState _state;
    private readonly SpatialService _spatial;
    public int OwnEntityId { get; set; } = -1;
    public SpatialService Spatial => _spatial;

    public Vector3 MapMin { get; set; } = new(-50, 0, -50);
    public Vector3 MapMax { get; set; } = new(50, 20, 50);
    public float Gravity { get; set; } = PhysicsConstants.Gravity;

    /// <summary>On the world: which of its tiles are there to walk into (held, and their ground built), and
    /// their size; null elsewhere. The server fences the same edges (SharedMovementEngine).</summary>
    public Func<OpenFPS.Geometry.TileKey, bool>? TileReady { get; set; }
    public float TileMetres { get; set; }

    /// <summary>The last step was stopped at the edge of a tile not built yet.</summary>
    public bool Fenced { get; private set; }

    /// <summary>The snapshot as the physics sees it, and the step's lists: the game thread's alone.</summary>
    private readonly SnapshotPhysicsView _view = new();
    private readonly OnFoot.Scratch _scratch = new();

    public ClientPhysicsSystem(LocalPlayerState state, SpatialService spatial)
    {
        _state = state;
        _spatial = spatial;
    }

    /// <summary>
    /// Applies a look delta with the server MovementSystem's formula, no smoothing. Call once per input,
    /// when it is gathered: rotation is not part of <see cref="Predict"/>, because prediction is
    /// replayed on every correction and a replayed, lerped rotation made the heading depend on how many
    /// packets were in flight.
    /// </summary>
    public void ApplyLook(ClientInputUpdate input, float dt)
    {
        if (input.LookDelta != Vector2.Zero)
        {
            _state.Yaw -= input.LookDelta.X * RotationSpeed * dt;
            _state.Pitch = Math.Clamp(_state.Pitch + (input.LookDelta.Y * RotationSpeed * dt), -1.5f, 1.5f);
        }
        _state.Rotation = Quaternion.CreateFromYawPitchRoll(_state.Yaw, _state.Pitch, 0);
    }

    /// <summary>
    /// Advances the local player one step. Pure in what it reads (position, velocity and yaw in, new
    /// position and velocity out), so replaying the same inputs from the same server state lands in the
    /// same place.
    /// </summary>
    /// <returns>What the body pressed into and was held off this step (see
    /// <see cref="SharedMovementEngine.Contact"/>), with its entity; null when nothing stopped it. Only
    /// the caller knows whether the step was fresh or a replay: see
    /// <see cref="PredictionReconciler.LastContact"/>.</returns>
    public BodyContact? Predict(ClientInputUpdate input, WorldSnapshot snapshot, float dt)
    {
        // The server's question of the client's world: the same ground probe and gather (OnFoot), what moves
        // counted in both, so a body standing on a parked car stands on its roof here too.
        var world = _view.Over(snapshot);
        float groundY = Footing.GroundOn(world, _state.Position, OwnEntityId, null, _scratch.Ground, out string mat);
        _state.CurrentMaterial = mat;

        Vector3 inputDir = Vector3.Zero;
        if (input.MoveDirection != Vector3.Zero)
        {
            inputDir = Vector3.Transform(input.MoveDirection, Quaternion.CreateFromYawPitchRoll(_state.Yaw, 0, 0));
        }

        var ctx = new SharedMovementEngine.MovementContext
        {
            Position = _state.Position,
            Velocity = _state.Velocity,
            InputDirection = inputDir,
            DeltaTime = dt,
            GroundHeight = groundY,
            Gravity = Gravity,
            JumpForce = JumpPower,
            Speed = PhysicsConstants.FootSpeed(input.Sprint, _state.SpeedLimit),
            PlayerRadius = PlayerRadius,
            PlayerHeight = PlayerHeight,
            StepHeight = StepHeight,
            IsJumpRequested = input.Jump,
            MapMin = MapMin,
            MapMax = MapMax,
            // Held, and its ground built into the triangles: a foot never comes down on ground the client
            // cannot stand on yet.
            TileReady = TileReady is { } held ? key => held(key) && snapshot.Geometry is { } g && g.InstanceOfTile(key) >= 0 : null,
            TileMetres = TileMetres,
        };

        var step = OnFoot.Step(world, OwnEntityId, ctx, _scratch);
        Fenced = step.Contact.Fenced;

        _state.Position = step.Position;
        _state.Velocity = step.Velocity;
        _state.IsGrounded = step.Grounded;

        LastContactEvent = OnFoot.ContactOf(OwnEntityId, PhysicsConstants.PersonMaterial, step);
        SecondContact = OnFoot.SecondContactOf(OwnEntityId, PhysicsConstants.PersonMaterial, step) is { } also
            ? new BodyContact(also.Other, also.Normal, also.ClosingSpeed, ctx.Speed, also.Point) : null;
        if (LastContactEvent is not { } contact) return null;
        return new BodyContact(contact.Other, contact.Normal, contact.ClosingSpeed, ctx.Speed, contact.Point);
    }

    /// <summary>The last step's contact as the physics states it (<see cref="ContactEvent"/>), or null when
    /// nothing held the body back. Its phase is always Began: a replayed step cannot tell.</summary>
    public ContactEvent? LastContactEvent { get; private set; }

    /// <summary>Another thing the last step pressed into besides what <see cref="Predict"/> returned: in a
    /// corner, the other wall. Null when there was none.</summary>
    public BodyContact? SecondContact { get; private set; }
}

/// <summary>
/// The body pressed into something and was held off it: the entity, the surface's outward normal, the
/// attempted speed into it, the speed attempted at all, and where the feet ended up.
/// <see cref="Intent"/> is the share aimed at the surface: one walking straight at a wall, near zero
/// brushing along it.
/// </summary>
public readonly record struct BodyContact(int EntityId, Vector3 Normal, float IntoSpeed, float Speed, Vector3 Feet)
{
    public float Intent => Speed > 1e-4f ? Math.Clamp(IntoSpeed / Speed, 0f, 1f) : 0f;
}

/// <summary>What a fresh step's contacts are to the wall bumps.</summary>
public static class BodyContacts
{
    /// <summary>
    /// One fresh movement step's two contacts put to <paramref name="bumps"/>: what the body pressed hardest
    /// into, and the other thing it pressed into (in a corner, the other wall), each already resolved
    /// (<see cref="WallBumps.Resolve"/>). Of two walls pressed equally, which the step names first depends on the
    /// order it met them; asked of both, a corner is two walls whatever that order. True when either is a new
    /// bump, which <paramref name="bumped"/> says.
    /// </summary>
    public static bool Bump(this WallBumps bumps, BodyContact? first, BodyContact? second, Vector3 feet, out BodyContact? bumped)
    {
        bumped = null;
        if (first == null) (first, second) = (second, null);
        if (bumps.Update(first, feet)) { bumped = first; return true; }
        if (second != null && bumps.Update(second, feet)) { bumped = second; return true; }
        return false;
    }
}
