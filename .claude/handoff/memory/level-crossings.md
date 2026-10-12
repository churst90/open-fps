---
name: level-crossings
description: Built 2026-09-22 — a crossing declares a POINT and derives everything else; the road reads the crossing, the crossing reads the trains, the trains read nothing
metadata:
  type: project
---

`LevelCrossingData` + `CrossingSystem` + `BellVoiceState`, on the city's new `southgate` loop which
crosses the rail's southern leg twice (at X −160 and X −40, Z −180, humping over the 0.85 m
embankment).

**The direction of knowledge is the design.** A train does not know a crossing is there, does not
slow and does not signal — it simply comes. The ROAD is what changes. So everything reads the
trains and writes to the road, and `RailSystem` is untouched.

**A crossing declares a point and nothing else.** Which rail line runs through it, which roads do,
and how far round each it sits are derived from the tracks the map already has — a declared offset
is a number that rots silently the first time a waypoint moves, and a crossing whose offset has
rotted rings for nothing and stops nobody. The warning distance is derived too: real crossings are
TIMED (a fixed warning time, ~20 s), so the distance follows the line speed — 250 m here.

**`SynthRunning` on SoundEmitterComponent** (appended — see [[component-wire-format]]) is the first
thing in this world a client cannot work out for itself. An engine's revs, a siren's mode, an
aeroplane's power are all derivable from what the client observes; a crossing bell rings because of
where a train is on a line the listener may be a kilometre from. Defaults true, so every other
emitter means what it always meant.

**Two bugs found by tracing, both worth remembering:**
1. `DistanceToNextStop` walked a STORED INDEX forward and only advanced on arrival. That works for
   one stop and fails for two: a van locked onto a crossing 300 m ahead drove over the one under
   its wheels at 13 m/s with the bells going. It SCANS every tick now for the nearest stop that is
   actually a stop — and an OPEN crossing is skipped there, which is what makes traffic flow over
   it and queue at it with no special case further down.
2. A crossing's dwell was counted DOWN like a bus stop's, so the van pulled away on a 3-second
   timer with the train 87 m out. While the crossing is closed the dwell is topped up; the instant
   it opens it is dropped. Verified: brakes 9.6 → 0 m/s, holds 18 s through the pass, away on the
   tick after it opens.

**Two more, from "I hear the train stop in front of me, no crossing bell":**
3. **`LookUpPhysicalLevel` knew `machine:`, `rail:` and `aircraft:` and not `bell:`.** It returns
   null for an id it does not recognise, `PhysicalLevel` says false, and the emitter is dropped by
   BOTH the ranking and the submit path. The bell was never ranked, never voiced, never heard — it
   rang perfectly on the server and did not exist on the client. **Any new `kind:` prefix needs an
   entry here or it is silently invisible.**
4. **A definition is sent ONCE per entity per client.** `SynthRunning` lives in the definition, and
   `Transform.IsDirty` re-sends a STATE message (position, velocity) — not the definition. So the
   flag flipped every time and the client kept the definition it was handed at map load, in which
   the bell was silent. `Program.SyncAudioComponent(id)` is what asks for another; the crossing
   system is handed it as a callback. Nothing failed — the news never left the building.

**And a fifth (2026-09-22, "I still do not hear the train bell"):**
5. **`ClientAudioSystem` had a SECOND prefix list** — the branch that builds the emitter tested
   `machine:`/`aircraft:`/`rail:` and not `bell:`. So the bell was RANKED (fix 3), then fell through
   as a nameless synth with no `PhysicalKey`: log showed `Voice 5898 was placed…` every few seconds
   and never `Physical voice started … bell:`. Both lists now ask `PhysicalLevel(sid)` — the lookup
   IS the list. **Symptom to grep for:** a voice being placed with no "Physical voice started" line.

**And a map clash:** the rail platforms had been placed at arbitrary fractions of the lap (0.18,
0.66) BEFORE the crossings existed, and 0.66 landed 20 m from the east crossing. A 55 m consist
stopping there straddles the road — which is what Cody heard as "the train stops in front of me".
Platforms are now chosen from the crossing offsets, at least 200 m clear of both.

Traces: `OPENFPS_TRACE_CROSSING=<name substring>` and `OPENFPS_TRACE_SHUTTLE=<vehicle>`.

**Still to do:** no rail-joint thump under tyres crossing the rails, and no gates (the bell is the
only signal). Kind "give_way" exists for junctions and is unconditional; only "crossing" is
conditional.

See [[route-stops]], [[a-fence-is-not-a-wall]], [[nothing-on-the-city-ever-stops]].
