using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Sound;
using OpenFPS.Physics;
using OpenFPS.Models;

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
    /// <summary>The clock other bodies' steps and breath are timed on: the audio world's, given by the session.
    /// One of its own until then.</summary>
    public IAudioClock Clock { get; set; } = new StopwatchAudioClock();

    // Timing only: the parts of one update, copied out for the city-load instrument (--city-load), which
    // asks which of them a first pass spends its hundreds of milliseconds in.
    internal static readonly string[] LabPartNames = { "classify", "breath", "stride", "ground", "foot", "land", "step", "depart" };
    internal const int LabClassify = 0, LabBreath = 1, LabStride = 2, LabGround = 3, LabFoot = 4, LabLand = 5, LabStep = 6, LabDepart = 7;
    internal const int LabPartCount = 8;
    internal readonly double[] LabPartLastMs = new double[LabPartCount];
    internal double LabLastMs;
    internal int LabLandCount, LabStepCount;
    internal double LabWorstLandMs, LabWorstStepMs;
    private readonly double[] _partMs = new double[LabPartCount];

    private static double Ms(long fromTicks)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - fromTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// How fast a body no step stands (a walker placed on its path) may be moving vertically and still be
    /// standing on something, m/s: one tick of free fall is about 0.3 m/s. A player's body says whether it
    /// stands (EntityState.Grounding, physics stage 3); before that every body was read this way, and a jump's
    /// top, where the vertical speed passes zero, read as standing.
    /// </summary>
    public const float GroundedVerticalSpeed = 0.15f;

    /// <summary>Whether a body stands: as the server's step says, or from its vertical speed where no step
    /// stands it.</summary>
    public static bool Grounded(in EntitySnapshot body) => body.Grounding switch
    {
        Grounding.Grounded => true,
        Grounding.Airborne => false,
        _ => MathF.Abs(body.Velocity.Y) < GroundedVerticalSpeed,
    };

    /// <summary>Head above feet, metres: where a breath comes from, audible in the elevation.</summary>
    public const float HeadHeight = OpenFPS.Models.Speech.MouthHeight;

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
        long updateAt = System.Diagnostics.Stopwatch.GetTimestamp();
        double now = Clock.Now;
        LabLandCount = LabStepCount = 0;
        LabWorstLandMs = LabWorstStepMs = 0;

        foreach (var body in snapshot.DynamicEntities)
        {
            long partAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (body.Id == localPlayerId)
            {
                _partMs[LabClassify] += Ms(partAt);
                continue;
            }

            // Anything that walks: players and NPCs.
            var type = body.Definition.Type;
            if (type != EntityType.Player && type != EntityType.NPC)
            {
                _partMs[LabClassify] += Ms(partAt);
                continue;
            }

            // Only a body that walks has legs. A car IS an NPC to the server, and without this every car
            // trailed footsteps ("footsteps being drug behind it", sixteen a second at 30 km/h); then the
            // resolved spelling "ENGINE/", never on a snapshot, let cars walk again. The model table
            // reads the raw id the server writes: an aircraft, a bogie, a riding mower has no legs, a
            // push mower has its walker's (SmallMachineSpec.WalkedBehind).
            if (ModelKindTable.BodyOf(body.Definition.SoundEmitter.SoundId) is { } kind && kind != ModelBody.Walks)
            {
                _partMs[LabClassify] += Ms(partAt);
                continue;
            }

            // Nor somebody sitting in one: OccupancySystem gives an occupant the vehicle's velocity, heard
            // (Cody and Sean, 2026-10-05) as running footsteps from the driver's seat. The stride is
            // forgotten, so getting out does not finish a walk that started where they got in.
            if (body.Definition.RidingEntityId >= 0)
            {
                _bodies.Remove(body.Id);
                _partMs[LabClassify] += Ms(partAt);
                continue;
            }

            if (!_bodies.TryGetValue(body.Id, out var state))
                _bodies[body.Id] = state = new Body();
            _partMs[LabClassify] += Ms(partAt);

            // Lungs run on elapsed time; a body just seen (or back after an absence) gets none.
            partAt = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = state.LastSeenAt < 0 ? 0f : (float)(now - state.LastSeenAt);
            state.LastSeenAt = now;
            if (dt > 0f && dt < 1f)
            {
                float speed = new Vector2(body.Velocity.X, body.Velocity.Z).Length();
                if (state.Lungs.Update(speed, dt, out var breath))
                    OnBreath?.Invoke(body.Id, body.Transform.Position + new Vector3(0, HeadHeight, 0), breath);
            }
            _partMs[LabBreath] += Ms(partAt);

            partAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool grounded = Grounded(body);
            var fall = state.Stride.Update(body.Transform.Position, body.Velocity, grounded, body.Transform.Rotation);
            _partMs[LabStride] += Ms(partAt);
            if (!fall.Anything) continue;

            // The floor is asked only when a foot meets it: twice a second at a walk, not every frame.
            partAt = System.Diagnostics.Stopwatch.GetTimestamp();
            PhysicsUtils.GetGroundHeight(snapshot, body.Transform.Position, body.Id, out string material);
            _partMs[LabGround] += Ms(partAt);
            if (string.IsNullOrEmpty(material) || material == "None") material = "Generic";

            if (fall.Landed)
            {
                partAt = System.Diagnostics.Stopwatch.GetTimestamp();
                OnLandTriggered?.Invoke(body.Transform.Position, material, "0");
                double landMs = Ms(partAt);
                _partMs[LabLand] += landMs;
                LabLandCount++;
                if (landMs > LabWorstLandMs) LabWorstLandMs = landMs;
            }
            if (fall.Stepped)
            {
                partAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var at = PhysicsUtils.FootOnFloor(snapshot, fall.StepPosition, body.Transform.Position, body.Velocity, body.Id, out var footMaterial);
                _partMs[LabFoot] += Ms(partAt);
                if (!string.IsNullOrEmpty(footMaterial) && footMaterial != "None") material = footMaterial;
                partAt = System.Diagnostics.Stopwatch.GetTimestamp();
                OnStepTriggered?.Invoke(at, material, "0", fall.Slope, body.Id);
                double stepMs = Ms(partAt);
                _partMs[LabStep] += stepMs;
                LabStepCount++;
                if (stepMs > LabWorstStepMs) LabWorstStepMs = stepMs;
            }
        }

        // Somebody who has gone loses their half-finished stride: back somewhere else, the distance
        // between is not something they walked.
        if (_bodies.Count == 0)
        {
            LabLastMs = Ms(updateAt);
            Array.Clear(_partMs);
            Array.Clear(LabPartLastMs);
            return;
        }
        long departAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _departed.Clear();
        foreach (var id in _bodies.Keys)
            if (!snapshot.Entities.ContainsKey(id)) _departed.Add(id);
        foreach (var id in _departed) _bodies.Remove(id);
        _partMs[LabDepart] += Ms(departAt);

        LabLastMs = Ms(updateAt);
        Array.Copy(_partMs, LabPartLastMs, _partMs.Length);
        Array.Clear(_partMs);
    }
}
