---
name: component-wire-format
description: Component structs are [MemoryPackable] and serialised POSITIONALLY — append fields only, and restart the server after any component change
metadata:
  type: project
---

`OpenFPS.Common/Components.cs` structs are `[MemoryPackable]` with no `[MemoryPackOrder]`, so MemoryPack
writes members **positionally, in declaration order, with no names on the wire**. The declaration order
of those structs IS the network protocol.

Inserting a member in the middle does not add a field, it renumbers every field after it — and the
failure is silent. Adding `SoundEmitterComponent.Offset` after `MinDistance` made the client read a
`Vector3` where a `float` had been written, lose twelve bytes of alignment, and come out with
`IsSynth` false on every vehicle: thirty cars became thirty attempts to play a sample file called
`engine:nascar_v8`, and the speedway went completely silent. Nothing threw. `SoundEmitterComponent`
now carries an "APPEND ONLY BELOW THIS LINE" marker.

**Why:** appended members degrade gracefully — an old peer simply does not send one, and a member
nobody sent reads back as its default.

**How to apply:** add new component members at the END of the struct, never in the middle.
And **restart the server after any component change**: `run-gtk-client.sh` rebuilds only the client,
so a long-lived server quietly becomes an old protocol. Check for one with `ss -lunp | grep 33288` —
it is **UDP**, so `ss -lntp` (TCP) will tell you the port is free when it is not.

See [[build-on-tmpfs]], [[audio-ghosts-and-stutters]].

The restart rule is not only for components — see [[restart-the-server-always]]: it is every change,
and `run-server.sh` now refuses to start a second server rather than binding nothing quietly.

**A definition is sent ONCE per entity per client** (`session.KnownEntities`), so anything carried in the DEFINITION rather than the state message is frozen at map load unless something asks for a re-send: `Program.SyncAudioComponent(entityId)`. `Transform.IsDirty` does NOT do it — that re-sends position and velocity. Cost a whole debugging round on the crossing bell; see [[level-crossings]].
