# net-trim handoff (2026-10-05)

Branch net-trim off main 4da80029. Wire change: client AND server must both be rebuilt.

## Done (committed)
- Resting states: OpenFPS.Server/Core/RestingStates.cs. A dynamic state goes when it changed, on the 2 ticks
  after its last change, on a 1 Hz keep-alive ((tick+id)%30==0), always for a fresh definition and for the
  client's own body. Wheels omitted while unchanged (client keeps the last). Per-session record:
  UserSession.SentStates, cleared/removed alongside KnownEntities.
- Client holds what it is not sent: ClientWorldState.HoldThrough folds every played snapshot into _held;
  an entity in 'to' but not 'from' interpolates from its held state (at t0 if resting, from its own tick if
  it was moving = packet loss); an entity in 'from' but not 'to' is put exactly at its held state.
- Packing: OpenFPS.Common/StatePacking.cs (varint id, flags, 3x int32 mm, smallest-three 48-bit rotation
  with a sign bit, velocity none / int16 mm/s below 32.767 m/s / float, tyre byte if nonzero, wheels if sent).
  ServerStateUpdate.Packed appended; [MemoryPackOnDeserialized] unpacks into States.
- NetworkService.Pieces: greedy fill to the packet limit instead of halving.
- StatsUpdate only when its content changes (GameServer.SameStats, session.LastStats).
- Composite parts: NOT the reliable flood the audit thought. CompositeService.MakeDynamic gives parts a
  Velocity, so they were already unreliable; resting parts (parked cars) are now omitted like anything else.

## Measurements so far (city map)
- Same recorded stream (base1.bin, 1 walking bot, 45 s) through old vs new send path:
  481 -> 178 states/tick, 5.76 -> 1.35 Mbit/s, 578 -> 133 packets/s; packing alone 3.05 Mbit/s.
  Client playback of both through ClientWorldState, 482 entities x 2701 frames: worst 0.72 mm, 0.68 mm/s,
  0.005 deg. Send-path CPU per player per tick: old 2.26 ms, new 2.45 ms (debug build, loaded box).
- Live, 1 walking bot: baseline 5.48 Mbit/s 582 datagrams/s; new 1.51 Mbit/s 151 datagrams/s.
  StatsUpdate 28.5/s -> 0.1/s. Server tick times unusable: box at load 70-130 from another session.
- Still to do: 3-bot live A/B.

## Tools (not in repo)
- ~/.cache/openfps-agent-net/run.sh VARIANT BOTS WARMUP MEASURE [REC] [walk]; ab.sh BOTS VARIANT...
  variants baseline/ and new/ each hold server/ and netbots/ builds. Port 34391.
- Bot and replay sources: session scratchpad netbots/ and netreplay/ (netreplay simulate FILE).

## Tests
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet test OpenFPS.Tests --artifacts-path ~/.cache/openfps-agent-net -nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName~NetworkTrimTests|FullyQualifiedName~StateSplitTests"
(14 pass). Full suite not yet run.
