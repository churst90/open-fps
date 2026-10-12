---
name: blocked-bearing-flips
description: 09-27 sirens "bounce around" = blocked-source bearing alternating between the verified edge and 23.4 m pathing probes; not yet fixed
metadata:
  type: project
---

Cody 09-27: "Sirens still bounce around" (after the 09-26 double-set fix, which WAS in the build he
heard). Log /tmp/openfps-sa-on.log 05:02-05:15, `[ADBG] ... siren ... dir=`: 69 jumps >30 deg between
updates with listener yaw steady, 68 of them with occ>0.3 at 150-300 m. Cause: AsyncAcousticWorker
bearing for Visibility<0.5 = edge when `edgeVerified` else `_saSim.GetPathing` — city probes are
23.4 m apart, and in a city at 280 m the single-worst-box edge is verified only some ticks, so the
bearing alternates between two methods. Birds stall FIXED 50f3c79 (survey keyed on entity count → scenery signature).
Cody asked if box buildings (no rounded shapes) cause it: partly — the model picks ONE edge of ONE
box; real sound arrives round several edges at once. Proposed: blend routes by energy + slew. Not built. See [[diffracted-bearing]].
