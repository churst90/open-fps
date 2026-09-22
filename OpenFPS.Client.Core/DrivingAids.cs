using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// The painted road, made audible to whoever is driving.
///
/// Two cues, both from the lines on the carriageway (<see cref="LaneGuide"/>):
///
///   * TICKS — one for every dash of the broken line that goes past, from the side it is on. The
///     rate is your speed, the side is which lane line you are nearest, and the loudness is how
///     near. It is the dashed line a sighted driver watches out of the corner of an eye.
///   * EDGE TONES — a low one for the kerb and a higher one for the centre line, each rising as the
///     side of the car closes on it. Two different notes because the two edges mean different
///     things: one is a kerb, and the other has oncoming traffic on the far side of it.
///
/// Both are placed ON the line, beside the driver's head, so the direction they come from is the
/// direction of the line. Only the driver hears them; a passenger has no lane to keep.
/// </summary>
public sealed class DrivingAids
{
    private readonly AudioEngineFacade _audio;

    // Voice ids. Negative, and a block of their own well clear of every other pool.
    private const int LeftToneId = -960001, RightToneId = -960002;
    private const int TickBaseId = -961000, TickPool = 8;
    private int _tickIndex;

    private const string TickSound = "SYNTH/lane_tick";
    private bool _tickRegistered;

    /// <summary>How close to a hazard line, metres from the side of the car, before its tone starts.</summary>
    private const float ToneRangeMetres = 1.0f;

    /// <summary>How far away a dashed line still ticks, metres from the middle of the car.</summary>
    private const float TickRangeMetres = 5.0f;

    private const float ToneVolume = 0.18f, TickVolume = 0.3f;

    private int _lastRoadKey = int.MinValue;
    private float _lastAlong;

    public DrivingAids(AudioEngineFacade audio) => _audio = audio;

    public void Update(WorldSnapshot world, LocalPlayerState state)
    {
        if (!state.IsRiding || !state.RidingControls
            || !world.Entities.TryGetValue(state.RidingEntityId, out var car))
        {
            Silence();
            return;
        }

        var at = car.Transform.Position;
        var rotation = car.Transform.Rotation;
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        var right = Vector3.Transform(Vector3.UnitX, rotation);

        if (!TryFindRoad(world, at, forward, out var road, out int roadKey)
            || !LaneGuide.Locate(road, at, forward, out var p))
        {
            Silence();
            return;
        }

        float halfWidth = 0.95f;
        if (car.Definition.SoundEmitter.SoundId is { } sid && sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
            && MachineRegistry.Knows(sid[7..]))
            halfWidth = MachineRegistry.VehicleFor(sid[7..]).WidthMetres * 0.5f;

        var (left, rightSide) = LaneGuide.Sides(p, halfWidth);
        Tone(LeftToneId, left, right);
        Tone(RightToneId, rightSide, right);

        // The dashes. Counted against the SAME road only: stepping from one carriageway box onto the
        // next restarts the count rather than reading the jump between two unrelated frames as a
        // hundred dashes gone by.
        if (roadKey == _lastRoadKey)
        {
            int passed = Math.Min(2, LaneGuide.DashesPassed(_lastAlong, p.Along));
            if (passed > 0 && NearestDashedLine(p, out float offset))
                for (int i = 0; i < passed; i++) Tick(right * offset, offset);
        }
        _lastRoadKey = roadKey;
        _lastAlong = p.Along;
    }

    /// <summary>
    /// The broken line nearest the car, as an offset to the driver's right. A lane divider if the
    /// road has one; on a road with one lane each way, the centre line, which on a two-lane road is
    /// the broken one you may overtake across.
    /// </summary>
    private static bool NearestDashedLine(LaneGuide.Position p, out float offset)
    {
        offset = 0f;
        float best = float.MaxValue;
        bool anyDivider = false;
        foreach (var (lineAt, kind) in LaneGuide.Lines(p)) anyDivider |= kind == LaneGuide.Line.Lane;
        foreach (var (lineAt, kind) in LaneGuide.Lines(p))
        {
            bool dashed = kind == LaneGuide.Line.Lane || (!anyDivider && kind == LaneGuide.Line.Centre);
            if (!dashed) continue;
            float x = (lineAt - p.Across) * p.Facing;
            if (MathF.Abs(x) < best) { best = MathF.Abs(x); offset = x; }
        }
        return best <= TickRangeMetres;
    }

    private void Tone(int id, (float Gap, LaneGuide.Line Kind, float Offset) side, Vector3 right)
    {
        if (side.Kind == LaneGuide.Line.Lane || side.Gap > ToneRangeMetres || side.Gap == float.MaxValue)
        {
            if (_audio.IsPlaying(id)) _audio.StopSound(id);
            return;
        }
        float closeness = Math.Clamp(1f - side.Gap / ToneRangeMetres, 0f, 1f);
        bool kerb = side.Kind == LaneGuide.Line.Kerb;
        var e = new SpatialEmitter
        {
            EntityId = id,
            SoundId = "SYNTH",
            IsSynth = true,
            SynthWave = kerb ? SynthWaveType.Triangle : SynthWaveType.Sine,
            SynthFrequency = kerb ? 196f : 523f,
            // Over the line, the note wobbles: you are not approaching it any more, you are on it.
            SynthLfoRate = side.Gap < 0f ? 6f : 0f,
            SynthLfoDepth = side.Gap < 0f ? 0.3f : 0f,
            SynthFilterCutoff = 1f,
            FollowsListener = true,
            ListenerOffset = right * side.Offset,
            Volume = ToneVolume * closeness * closeness,
            MinDistance = 5f,
            Range = 20f,
            Essential = true,
            Type = EmitterType.WorldLocked,
            EnableReverb = false,
        };
        if (_audio.IsPlaying(id)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
    }

    private void Tick(Vector3 offsetInWorld, float offset)
    {
        if (!_tickRegistered)
            _tickRegistered = _audio.RegisterSynthesisedSound(TickSound, TransientSynth.ToPcm16(RenderTick()), TransientSynth.SampleRate);
        if (!_tickRegistered) return;
        float near = Math.Clamp(1.2f - MathF.Abs(offset) / TickRangeMetres, 0.25f, 1f);
        _audio.Submit(new SpatialEmitter
        {
            EntityId = TickBaseId - (_tickIndex++ % TickPool),
            SoundId = TickSound,
            Mode = OpenFPS.Common.Components.PlaybackMode.Single,
            FollowsListener = true,
            ListenerOffset = offsetInWorld,
            Volume = TickVolume * near,
            MinDistance = 5f,
            Range = 20f,
            Essential = true,
            IsEvent = true,
            Type = EmitterType.WorldLocked,
            EnableReverb = false,
        });
    }

    /// <summary>
    /// A tyre crossing a raised line of paint: a short, bright knock. Twelve milliseconds of noise
    /// through a two-kilohertz resonance with a fast decay — the thermoplastic of a road marking is a
    /// few millimetres proud of the asphalt, and that is the whole sound.
    /// </summary>
    private static float[] RenderTick()
    {
        int rate = TransientSynth.SampleRate, n = rate * 25 / 1000;
        var buf = new float[n];
        var rng = new Random(1234);
        float w = 2f * MathF.PI * 2000f / rate, r = 0.985f;
        float a1 = 2f * r * MathF.Cos(w), a2 = -r * r, y1 = 0f, y2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float env = i < rate * 12 / 1000 ? MathF.Exp(-i / (rate * 0.003f)) : 0f;
            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
            float y = x + a1 * y1 + a2 * y2;
            y2 = y1; y1 = y;
            buf[i] = y * 0.25f;
        }
        float peak = 1e-6f;
        foreach (float v in buf) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < n; i++) buf[i] *= 0.8f / peak;
        return buf;
    }

    /// <summary>
    /// The asphalt the car is standing on. Where two carriageways cross, the one running the way the
    /// car is pointing: at a junction you are on the road you are driving along, not the one across it.
    /// </summary>
    private static bool TryFindRoad(WorldSnapshot world, Vector3 at, Vector3 forward, out LaneGuide.Road road, out int key)
    {
        road = default; key = int.MinValue;
        if (world.StaticGrid == null) return false;
        float bestAlign = -1f;
        foreach (int id in world.StaticGrid.GetItemsInRadius(at, 30f))
        {
            if (!world.Entities.TryGetValue(id, out var e)) continue;
            var def = e.Definition;
            if (!string.Equals(def.Material.Material, "Asphalt", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = new LaneGuide.Road(e.Transform.Position, def.Collider.Size, e.Transform.Rotation);
            if (!LaneGuide.Locate(candidate, at, forward, out _)) continue;
            float align = MathF.Abs(Vector3.Dot(candidate.Axes().Along, Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z))));
            if (align > bestAlign) { bestAlign = align; road = candidate; key = id; }
        }
        return key != int.MinValue;
    }

    private void Silence()
    {
        if (_audio.IsPlaying(LeftToneId)) _audio.StopSound(LeftToneId);
        if (_audio.IsPlaying(RightToneId)) _audio.StopSound(RightToneId);
        _lastRoadKey = int.MinValue;
    }
}
