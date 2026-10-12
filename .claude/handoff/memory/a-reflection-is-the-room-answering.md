---
name: a-reflection-is-the-room-answering
description: Reflections were feeding the reverb bus as new sources at their IMAGE distance — 507% send per footstep
metadata:
  type: project
---

**A reflection must not send to the reverb bus as though it were a source.** The room equation
(`Enclosure.ReverberantToDirectPower`) says how much reverberant field a SOURCE raises and already
counts every path from it — direct, first bounce, second. A reflection that sends as well is the same
energy twice.

And it was twice at the **wrong distance**: a reflection sits at its IMAGE position, so `sourceDist`
is the mirrored distance — 16 to 22 m inside a 9 m flat — which the room equation quite correctly
reads as "almost entirely reverberant". Measured live: `wettest voice sent 507 % (at 16.2 m)`, on
every footfall, three times over. Heard as *"the whole room is reverby... every time I step, pop pop
pop"*, and it is also what buried the carpet in the flats and the wall slaps in the tunnel.

A reflection now sends only `ReflectionScatteredShare` (0.25) of a mix capped at 1 — the share its
surface scattered rather than mirrored, which has no direction left and belongs in the diffuse field.

`SpatialEmitter.EnableReverb` exists and **the provider never read it**. Still doesn't; the rule is
applied on `IsReflection` instead.

See [[the-room-is-not-a-cube]], [[reverb-needs-enclosure]], [[regions-measure-themselves]].
