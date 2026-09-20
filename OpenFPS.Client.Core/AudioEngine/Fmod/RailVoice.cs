using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// A train, rendered once, heard from many places.
///
/// Every other physical model is one pressure at one point and has <see cref="PhysicalVoiceState"/>:
/// a synth, a ring, a DSP. A train is a line of bogies, drives and a body drum spread over fifty
/// metres of consist, and the rule that must not be undone (docs/TRAINS.md) is that they share ONE
/// rail: splitting them into independent synths gives each bogie its own track, and the clatter
/// sweep, the level plateau and the far end being duller than the near end all go.
///
/// So the server places one entity per source (<see cref="TrainLayout"/>, sampling the track at
/// <c>head − along</c>, which is right round curves), and the client gives each of them a
/// <see cref="TrainTapState"/> — an ordinary physical voice as far as the provider, the render pool
/// and FMOD are concerned — that reads source <c>i</c> of a single <see cref="TrainSynth"/> held here.
/// Whichever tap asks for a sample the synth has not made yet steps the synth, under a lock, and
/// writes every source's output into that source's ring; taps then read their own ring at their
/// own pace. The rings are long enough that taps rendered on different worker threads, each a few
/// hundred milliseconds ahead of the mixer, stay inside the window.
/// </summary>
public sealed class TrainVoiceState
{
    public readonly TrainProfile Profile;
    public readonly TrainSynth Train;
    public readonly IReadOnlyList<TrainLayout.Entry> Layout;
    public readonly string Key;

    private const int RingBits = 17;                       // about three seconds at 44.1 kHz
    private readonly float[][] _rings;
    private readonly int _mask = (1 << RingBits) - 1;
    private long _rendered;                                // samples the synth has produced so far
    private readonly object _gate = new();

    public volatile float TargetSpeed;
    public volatile float TargetNotch = 3f;
    public volatile bool Running = true;
    private float _speed, _notch = 3f;

    public int Taps;                                       // how many entities currently read it

    public TrainVoiceState(string key, TrainProfile p, float sampleRate, int seed)
    {
        Key = key;
        Profile = p;
        Train = new TrainSynth(p, sampleRate, seed);
        Layout = TrainLayout.Sources(p);
        int n = Train.Sources.Count;
        _rings = new float[n][];
        for (int i = 0; i < n; i++) _rings[i] = new float[1 << RingBits];
        _speed = TargetSpeed = p.TypicalSpeedMps;
    }

    /// <summary>Where a tap should start reading: the newest sample, so a voice that joins late
    /// does not begin three seconds behind the train.</summary>
    public long Newest => Volatile.Read(ref _rendered);

    /// <summary>The sample at position <paramref name="at"/> of source <paramref name="source"/>,
    /// rendering forward as needed. Called from the render pool's worker threads.</summary>
    public float Sample(int source, long at, float dt)
    {
        if (at >= Volatile.Read(ref _rendered))
        {
            lock (_gate)
            {
                long have = _rendered;
                if (at >= have)
                {
                    // Render in a block, not one sample per lock: 512 is what the pool asks for.
                    long upto = at + 512;
                    for (long s = have; s < upto; s++) StepOnce(dt);
                    Volatile.Write(ref _rendered, upto);
                }
            }
        }
        return _rings[source][(int)(at & _mask)];
    }

    private void StepOnce(float dt)
    {
        // Speed and notch slew: a train's speed cannot step, and neither can its effort.
        _speed += (TargetSpeed - _speed) * MathF.Min(1f, dt * 1.5f);
        _notch += (TargetNotch - _notch) * MathF.Min(1f, dt * 0.8f);
        Train.Speed = Running ? MathF.Max(0f, _speed) : 0f;
        Train.Notch = _notch;
        Train.Step();
        var src = Train.Sources;
        int idx = (int)(_rendered & _mask);
        for (int i = 0; i < src.Count; i++) _rings[i][idx] = src[i].Out;
        _rendered++;
    }

    /// <summary>"rail:&lt;preset&gt;/&lt;train&gt;/&lt;source index&gt;" — what the server writes as the
    /// SoundId of each entity of a consist.</summary>
    public static bool ParseKey(string soundId, out string preset, out string train, out int index)
    {
        preset = ""; train = ""; index = -1;
        if (soundId == null || !soundId.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = soundId[5..].Split('/');
        if (parts.Length != 3) return false;
        preset = parts[0]; train = parts[1];
        return int.TryParse(parts[2], out index);
    }
}

/// <summary>One source of a train, as a voice. See <see cref="TrainVoiceState"/>.</summary>
public sealed class TrainTapState : PhysicalVoiceState
{
    public readonly TrainVoiceState Shared;
    public readonly int Source;
    public readonly TrainLayout.Entry Entry;
    private long _cursor;
    private readonly float _dt;

    public TrainTapState(TrainVoiceState shared, int source, float sampleRate)
        : base(shared.Layout[source].LevelDb, sampleRate)
    {
        Shared = shared;
        Source = source;
        Entry = shared.Layout[source];
        _dt = 1f / sampleRate;
        _cursor = shared.Newest;
        Interlocked.Increment(ref shared.Taps);
    }

    protected override void PushListener(Vector3 frame) => Shared.Train.SetListener(frame);
    protected override void Control(float seconds, float dt) => Shared.Running = Running;
    protected override float StepSynth() => Shared.Sample(Source, _cursor++, _dt);
}
