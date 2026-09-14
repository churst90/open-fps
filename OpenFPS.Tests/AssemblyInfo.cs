using Xunit;

// Test CLASSES run in parallel by default, and this codebase has global static state that several of
// them reach through: PerfProbe is written to by ClientWorldState, ClientAudioSystem and PhysicsUtils,
// and AcousticRegistry is a process-wide singleton. So HotPathTests, which asserts that PerfProbe holds
// NOTHING until it is switched on, was racing any other class that happened to step a client session at
// the same moment — passing alone, passing most of the time, and failing at random in a full run.
//
// Serializing the whole assembly costs about nothing here (244 tests in ~5 s) and removes the entire
// class of failure rather than the one instance of it that showed up.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
