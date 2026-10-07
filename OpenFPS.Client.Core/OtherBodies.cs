using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// Everybody else's feet (and lungs), derived from their interpolated transforms.
///
/// <para>Derived rather than sent: a footstep would be a reliable packet per body per half metre, and
/// would arrive describing a position the listener has already heard the body leave. From the
/// interpolated transform the step is exactly where this client has the body.</para>
///
/// <para>What counts as a stride is <see cref="StrideAccumulator"/>'s, shared with the local player.</para>
/// </summary>
public sealed class OtherBodies
{
    /// <summary>
    /// How fast a body may be moving vertically and still be standing on something, m/s. The server
    /// zeroes a grounded body's vertical velocity, and one tick of free fall is about 0.3 m/s, so the
    /// velocity answers it; probing the floor per body per frame (five points through the grid) costs
    /// real time for an answer that changes twice a second.
    /// </summary>
    public const float GroundedVerticalSpeed = 0.15f;

    /// <summary>Head above feet, metres: where a breath comes from, audible in the elevation.</summary>
    public const float HeadHeight = 1.6f;

    /// <summary>One body's physical state: where its feet are in its gait, and how hard it is working.</summary>
    private sealed class Body
    {
        public readonly StrideAccumulator Stride = new();
        public readonly Breathing Lungs = new();
        public double LastSeenAt = -1;
    }

    private readonly Dictionary<int, Body> _bodies = new();
    private readonly List<int> _departed = new();

    /// <summary>A body put a foot down: where, on what, the variant, up or down, and whose body (the step
    /// is heard from inside that body's own collider, which must not occlude it; see
    /// ClientAudioSystem.OnPlayerFootstep).</summary>
    public event Action<Vector3, string, string, StepSlope, int>? OnStepTriggered;
    public event Action<Vector3, string, string>? OnLandTriggered;

    /// <summary>A body took a breath: who, from where, and what kind. Nothing subscribes: the voice was
    /// rejected by ear (see ClientGameSession).</summary>
    public event Action<int, Vector3, Breath>? OnBreath;

    /// <summary>Bodies being listened to. Diagnostic.</summary>
    public int Count => _bodies.Count;

    public void Clear() => _bodies.Clear();

    public void Update(WorldSnapshot snapshot, int localPlayerId)
    {
        double now = AudioClock.Now;

        foreach (var body in snapshot.DynamicEntities)
        {
            if (body.Id == localPlayerId) continue;

            // Anything that walks: players and NPCs.
            var type = body.Definition.Type;
            if (type != EntityType.Player && type != EntityType.NPC) continue;

            // A thing with an engine has no legs. A car IS an NPC to the server, and without this every
            // car trailed footsteps ("footsteps being drug behind it", sixteen a second at 30 km/h). The
            // same test as ClientAudioSystem's for a vehicle, on the raw "engine:" id the server writes:
            // the resolved spelling "ENGINE/" is never on a snapshot, and matching it let cars walk.
            if (body.Definition.SoundEmitter.SoundId is { } sid &&
                sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) continue;
            // Nor anything else with a physical model (aircraft, bogies, riding mowers), except the push
            // mower: somebody is walking behind it.
            if (body.Definition.SoundEmitter.SoundId is { } pid && body.Definition.SoundEmitter.IsSynth
                && (pid.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase)
                    || pid.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)
                    || (pid.StartsWith("machine:", StringComparison.OrdinalIgnoreCase)
                        && !pid.Equals("machine:mower_push", StringComparison.OrdinalIgnoreCase)))) continue;

            // Nor somebody sitting in one: OccupancySystem gives an occupant the vehicle's velocity, heard
            // (Cody and Sean, 2026-10-05) as running footsteps from the driver's seat. The stride is
            // forgotten, so getting out does not finish a walk that started where they got in.
            if (body.Definition.RidingEntityId >= 0)
            {
                _bodies.Remove(body.Id);
                continue;
            }

            if (!_bodies.TryGetValue(body.Id, out var state))
                _bodies[body.Id] = state = new Body();

            // Lungs run on elapsed time; a body just seen (or back after an absence) gets none.
            float dt = state.LastSeenAt < 0 ? 0f : (float)(now - state.LastSeenAt);
            state.LastSeenAt = now;
            if (dt > 0f && dt < 1f)
            {
                float speed = new Vector2(body.Velocity.X, body.Velocity.Z).Length();
                if (state.Lungs.Update(speed, dt, out var breath))
                    OnBreath?.Invoke(body.Id, body.Transform.Position + new Vector3(0, HeadHeight, 0), breath);
            }

            bool grounded = MathF.Abs(body.Velocity.Y) < GroundedVerticalSpeed;
            var fall = state.Stride.Update(body.Transform.Position, body.Velocity, grounded, body.Transform.Rotation);
            if (!fall.Anything) continue;

            // The floor is asked only when a foot meets it: twice a second at a walk, not every frame.
            PhysicsUtils.GetGroundHeight(snapshot, body.Transform.Position, body.Id, out string material);
            if (string.IsNullOrEmpty(material) || material == "None") material = "Generic";

            if (fall.Landed) OnLandTriggered?.Invoke(body.Transform.Position, material, "0");
            if (fall.Stepped)
            {
                var at = PhysicsUtils.FootOnFloor(snapshot, fall.StepPosition, body.Transform.Position, body.Velocity, body.Id, out var footMaterial);
                if (!string.IsNullOrEmpty(footMaterial) && footMaterial != "None") material = footMaterial;
                OnStepTriggered?.Invoke(at, material, "0", fall.Slope, body.Id);
            }
        }

        // Somebody who has gone loses their half-finished stride: back somewhere else, the distance
        // between is not something they walked.
        if (_bodies.Count == 0) return;
        _departed.Clear();
        foreach (var id in _bodies.Keys)
            if (!snapshot.Entities.ContainsKey(id)) _departed.Add(id);
        foreach (var id in _departed) _bodies.Remove(id);
    }
}
