namespace OpenFPS.Sound;

/// <summary>How an emitter plays its sound: stored and sent by number in SoundEmitterComponent, so append only.</summary>
public enum PlaybackMode { Single, LoopOne, LoopFolder, Sequential, StateMachine }
