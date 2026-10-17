namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The lab's levers on one audio world: A/Bs against what the game did before, and probes that put a click
/// where a delay can be measured. Nothing in the game sets one; a new one is the game. The mixer holds it
/// (FmodAudioProvider.Lab) and hands it to what it makes (its traced places, its stages, its voices), and an
/// acoustic worker has one (AsyncAcousticWorker.Lab), so an instrument that turns a lever on its own world
/// reaches no other. An instrument that makes a synth itself (an engine, running water) hands it its own.
/// </summary>
public sealed class LabLevers
{
    /// <summary>What the traced places' simulator produces (TracedReverb), as Steam Audio's reflection effect
    /// type: null is the game's, HYBRID (the convolution IR and the reverb times); CONVOLUTION the IR alone.</summary>
    public int? TracedSimulation { get; init; }

    /// <summary>Play each trace's own samples, as before SmoothTail (TracedReverb).</summary>
    public bool RawTail { get; set; }

    /// <summary>The late part as one channel through the velvet branches, as before DiffuseLate (TracedReverb).</summary>
    public bool OneChannelLate { get; set; }

    /// <summary>The latency probe: past zero, every trace publishes instead one click this many seconds into the
    /// response, from one direction, and nothing else (TracedReverb). Where it lands in a capture against the
    /// dry sound is what the bus path adds.</summary>
    public float ProbeSeconds { get; set; }

    /// <summary>The traced tail from 50 ms, as before the placed copies (SmoothTail.FromFiftyMs).</summary>
    public bool TailFromFiftyMs { get; set; }

    /// <summary>No wait at the traced stage's input, as before 2026-10-03 (TracedReverbDsp.StagePreDelay).</summary>
    public bool NoPreDelay { get; set; }

    /// <summary>The ears' velvet on the late field, as before (DiffuseTail).</summary>
    public bool LateEarVelvet { get; set; }

    /// <summary>A click in an engine's voice and its negative in the exhaust's tap, to line the two up
    /// (EngineVoiceState.LabAlignProbe).</summary>
    public bool EngineAlignProbe { get; set; }

    /// <summary>Called with a hob's synth as its voice is made (StoveVoiceState), to drive it by hand.</summary>
    public Action<Stove.GasHobSynth>? StoveCreated { get; set; }

    /// <summary>Record where each simulated path came from (AsyncAcousticWorker.Provenance).</summary>
    public bool TraceProvenance { get; set; }

    /// <summary>Keep a route's answers after its graph is replaced, as before (RouteAnswers).</summary>
    public bool RetainSupersededRoutes { get; set; }

    /// <summary>An engine's diesel combustion without the ignition delay and the chamber modes, for an A/B against
    /// the full model (EngineSynth; `legacydiesel` on any vehicle render command).</summary>
    public bool LegacyDiesel { get; set; }

    /// <summary>Every valve of an engine shut for the pipes, so the network can be tested on its own (EngineSynth).</summary>
    public bool RigidValves { get; set; }

    /// <summary>One tailpipe heard on its own (its branch's index), scaled up by the branch count so the level is
    /// comparable (EngineSynth's exhaust; `pipe=`); -1 is every pipe.</summary>
    public int SoloTailpipe { get; set; } = -1;

    /// <summary>Every place of every running water source plays white noise instead of its water, 0.1 Pa rms at a
    /// metre, so what the game's path does to a source can be told from what the water does (RunningWaterSynth).</summary>
    public bool WaterAsNoise { get; set; }

    /// <summary>How much replaced geometry a tile scene pair may hold, as a share of the triangles it traces, before
    /// it is made afresh (TileSceneSet); 0 makes it afresh whenever it holds anything replaced.</summary>
    public double TileRecycleShare { get; set; } = 1.0;

    /// <summary>The rain on the roof of the vehicle you ride in left in the world rather than riding with your head,
    /// to tell what riding with it does (RainField).</summary>
    public bool RainRoofInTheWorld { get; set; }
}
