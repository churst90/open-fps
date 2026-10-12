---
name: restart-the-server-always
description: Cody's standing instruction 2026-09-21 — ALWAYS restart the server after any change. A day-old server served yesterday's map and cost a whole session; run-server.sh now refuses to start a second one.
metadata:
  type: feedback
---

**Cody: "yes please always remember to restart the server after you've made changes."**

**Why:** the CLIENT script rebuilds on every launch, so client-side work always takes effect. The
SERVER is long-running and re-reads nothing. A server left up overnight (pid found at **1 day 2
hours** uptime) went on serving the map, the vehicle presets and the levels it read when it
started. Everything from a full session — a road police interceptor replacing a 132 dB pace car,
a Charger, two Harleys, bus stops, rail platforms — was in files that process had no reason to
open again. Cody's report was "nothing seems to be different on the city map", and it was exactly
true.

**And the failure is silent.** A second server binds nothing, logs `Address already in use` in the
middle of a startup that otherwise reads perfectly — maps load, entity counts are right, every
vehicle logs its lap — and then serves nobody. I read that output as two passing smoke tests and
reported the work as verified. It was not: it proved the map PARSES, not that the game serves it.

**How to apply:**
- After any change to maps, prefabs, presets, components or server code: restart. Not "if it looks
  like it matters" — always.
- `./run-server.sh` now REFUSES to start when UDP 33288 is taken, naming the pid and when it
  started. Do not work around it with a second port unless that is genuinely what is wanted.
- Never read `Address already in use` as noise. It was dismissed as IPv6 dual-bind chatter twice.
- Proof that a change is live is a line from the RUNNING server's own log, after a restart.

See [[component-wire-format]] (which already said this for components — it is true for everything),
[[build-on-tmpfs]], [[why-did-the-client-stop]].
