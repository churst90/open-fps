using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Rail;

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

    /// <summary>What the train is sounding (TrainSignal): the horn's rhythm and the bell's length, from a
    /// sample of the synth's own timeline. Swapped whole by the game thread; read by whichever worker
    /// renders the train.</summary>
    private sealed record Signalling(float[] Warning, float BellSeconds, long StartSample);
    private Signalling? _signal;
    private readonly float _rate;

    public TrainVoiceState(string key, TrainProfile p, float sampleRate, int seed)
    {
        Key = key;
        Profile = p;
        _rate = sampleRate;
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

    /// <summary>
    /// The train sounds its horn (or whistle) in this rhythm and rings its bell for this long, begun
    /// <paramref name="secondsAgo"/> before now. Played by the train's own outlets, where they are on
    /// it (TrainSynth's horn, whistle and bell sources), from the newest sample the synth has made: a
    /// voice renders a few hundred milliseconds ahead, and that is all the rhythm can be late by.
    /// </summary>
    public void Signal(float[] warning, float bellSeconds, double secondsAgo)
        => Volatile.Write(ref _signal, new Signalling(warning, MathF.Max(0f, bellSeconds),
                                                      Newest - (long)(Math.Max(0.0, secondsAgo) * _rate)));

    /// <summary>Whether the horn, whistle or bell is being sounded at the newest rendered sample.</summary>
    public bool IsSignalling => Volatile.Read(ref _signal) != null;

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
        // The hand on the horn valve and the bell, every 32 samples (two thirds of a millisecond).
        if ((_rendered & 31) == 0 && Volatile.Read(ref _signal) is { } sig)
        {
            float t = (_rendered - sig.StartSample) / _rate;
            bool warn = Honk.BlowingAt(sig.Warning, t);
            Train.HornBlowing = warn;
            Train.WhistleBlowing = warn;
            Train.BellRinging = t >= 0f && t < sig.BellSeconds;
            // Done: let it go, unless the game thread has already put a new one in its place.
            if (t > TrainSignal.Duration(sig.Warning, sig.BellSeconds)) Interlocked.CompareExchange(ref _signal, null, sig);
        }
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
        : base(shared.Layout[source].LevelDb, sampleRate, shared.Layout[source].HeadroomDb)
    {
        Shared = shared;
        Source = source;
        Entry = shared.Layout[source];
        _dt = 1f / sampleRate;
        _cursor = shared.Newest;
        Interlocked.Increment(ref shared.Taps);
    }

    /// <summary>Only the horn's own tap aims the horn: its frame is the listener seen from the horn (x
    /// right, y up, z along the track), which the train's frame has as (z, y, x). Every tap pushing its
    /// own frame left the horn aimed from whichever bogie rendered last.</summary>
    protected override void PushListener(Vector3 frame)
    {
        if (Entry.Kind == TrainLayout.Kind.Horn) Shared.Train.SetListener(new Vector3(frame.Z, frame.Y, frame.X));
    }
    protected override void Control(float seconds, float dt) => Shared.Running = Running;
    protected override float StepSynth() => Shared.Sample(Source, _cursor++, _dt);
}
