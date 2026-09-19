using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// Everybody else's feet.
///
/// Until this existed the only body in the world that made any noise walking was your own. Another
/// player could run past you, round you, into you, and the map stayed silent — in a game whose whole
/// proposition is knowing where things are by ear, the things that matter most were the quietest in
/// it. Nothing was missing to fix it: the positions, the velocities and the floor were all already on
/// the client, being used every frame for other purposes.
///
/// It is derived here rather than sent, for two reasons. A footstep would be a reliable packet per
/// body per half metre, which is a lot of traffic for something the receiver can work out; and the
/// step would arrive describing a position the listener has already heard the body leave. Deriving it
/// from the interpolated transform puts the sound exactly where the body is as this client
/// understands it, which is the only place it can be without contradicting everything else the
/// listener is being told.
///
/// The rules for what counts as a stride are <see cref="StrideAccumulator"/>'s, shared with the local
/// player, so there is one answer to "was that a step" and not two that can drift apart.
/// </summary>
public sealed class OtherBodies
{
    /// <summary>
    /// How fast a body may be moving vertically and still be standing on something, m/s.
    ///
    /// The server's movement step clamps a grounded body's vertical velocity to zero and lets gravity
    /// have it otherwise, so "is it on the ground" is a fact this client is already being told — one
    /// tick of free fall is a third of a metre a second, comfortably clear of this. Reading it off the
    /// velocity rather than probing the floor every frame for every body matters: the probe walks the
    /// static grid and tests five points, and at render rate across a field of bodies that is real
    /// money for an answer that changes twice a second.
    /// </summary>
    public const float GroundedVerticalSpeed = 0.15f;

    /// <summary>How far above a body's feet its head is, metres. A breath comes from up there and a
    /// footstep does not, and over a few metres that difference is audible in the elevation.</summary>
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

    public event Action<Vector3, string, string>? OnStepTriggered; // Position, Material, Variant
    public event Action<Vector3, string, string>? OnLandTriggered;

    /// <summary>A body took a breath: who, from where, and what kind.</summary>
    public event Action<int, Vector3, Breath>? OnBreath;

    /// <summary>How many bodies are currently being listened to. Diagnostic only.</summary>
    public int Count => _bodies.Count;

    public void Clear() => _bodies.Clear();

    public void Update(WorldSnapshot snapshot, int localPlayerId)
    {
        double now = AudioClock.Now;

        foreach (var body in snapshot.DynamicEntities)
        {
            if (body.Id == localPlayerId) continue;

            // Anything that walks. A player and an NPC are the same animal to a listener, and neither
            // is a crate, a door or a car — those make their own noises, by their own mechanisms.
            var type = body.Definition.Type;
            if (type != EntityType.Player && type != EntityType.NPC) continue;

            // ── A thing with an engine does not have legs ────────────────────────────────────────
            //
            // The line above was meant to keep cars out and did not, because a car IS an NPC: the
            // server drives it with the same AI type as anything else that moves under its own
            // direction. So every vehicle on the map got a stride accumulator, and a stride is half a
            // metre — reported from the rooms map as "the car driving by sounds like footsteps are
            // being drug behind it", which at 30 km/h is sixteen footfalls a second trailing the car.
            // Nothing protected against it: a car's own velocity is genuinely its own, which is the
            // test that keeps passengers and server corrections quiet, and at render rate it moves a
            // few centimetres an update, which is well inside what a stride explains.
            //
            // What actually distinguishes them is not the AI type but the machinery: an entity whose
            // emitter is an engine is a machine, and a machine is heard through its engine, its tyres
            // and its body — all of which it already has. This is the same test ClientAudioSystem uses
            // to decide something is a vehicle, so the two cannot disagree about what a car is.
            // The prefix is "engine:", which is the form the SERVER writes (VehicleSystem) and the form
            // ClientAudioSystem tests before it resolves anything. Checked against the raw id for that
            // reason: a first attempt at this filter matched the RESOLVED spelling, "ENGINE/", which
            // nothing on a snapshot ever holds, so it matched nothing and the cars kept walking.
            if (body.Definition.SoundEmitter.SoundId is { } sid &&
                sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!_bodies.TryGetValue(body.Id, out var state))
                _bodies[body.Id] = state = new Body();

            // Lungs run on elapsed time rather than on frames, and a body seen for the first time (or
            // after an absence) gets no elapsed time at all — it has not been running, it has just
            // arrived.
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

            // The floor is only asked about at the moment a foot actually meets it. A body is asked
            // twice a second at a walk, not once a frame, which is what makes this affordable.
            PhysicsUtils.GetGroundHeight(snapshot, body.Transform.Position, body.Id, out string material);
            if (string.IsNullOrEmpty(material) || material == "None") material = "Generic";

            if (fall.Landed) OnLandTriggered?.Invoke(body.Transform.Position, material, "0");
            if (fall.Stepped) OnStepTriggered?.Invoke(fall.StepPosition, material, "0");
        }

        // Somebody who has gone — disconnected, died, or simply walked out of the area of interest —
        // must not keep their half-finished stride. They may be back, and when they are they will be
        // somewhere else entirely; the distance between here and there is not something they walked.
        if (_bodies.Count == 0) return;
        _departed.Clear();
        foreach (var id in _bodies.Keys)
            if (!snapshot.Entities.ContainsKey(id)) _departed.Add(id);
        foreach (var id in _departed) _bodies.Remove(id);
    }
}
