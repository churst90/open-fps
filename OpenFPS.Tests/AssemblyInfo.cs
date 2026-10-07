// The whole assembly runs one test at a time: global state (PerfProbe, written by ClientWorldState,
// ClientAudioSystem and PhysicsUtils; the AcousticRegistry singleton) made HotPathTests fail at random
// whenever another class stepped a client session at the same moment.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
