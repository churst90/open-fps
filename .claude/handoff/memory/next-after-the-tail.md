---
name: next-after-the-tail
description: Session 16 closed the reverb arc; session 17 cleared most of the list — what is left
metadata:
  type: project
---

**2026-09-19, end of session 16.** Cody's verdict: *"that issue is now officially solved and
everything sounds great. there's no popping either."* The arc that ran from session 9 — every room
sounding identical — is **closed**. Four faults in series, see
[[the-reverb-unit-was-rendering-no-tail]] and [[your-own-footsteps-reverberated-outdoors]].

What it was blocking, in order — **session 17 cleared 1, 2, 3 and most of 6.** Full detail in
`docs/THE_CITY.md`; the original list is in `docs/NEXT_AFTER_THE_TAIL.md`.

1. ~~**The corridor comb**~~ — **CLOSED.** Cody's ear said the map was good, so it was deleted from
   the plan rather than pre-emptively fixed.
2. ~~**Stationary machines have no voice path**~~ — **BUILT.** [[physical-voice-path]]; 121 of them on
   the city map, and aircraft on the same path.
3. ~~**Footsteps: model or bank**~~ — **DECIDED by measurement.** [[footsteps-are-the-bank]]. The
   files were in `inbox/foot steps sounds/`, not `inbox/footsteps`.
4. **Small enclosure inside a big one** — still OPEN, but the gate test now exists and the fix is
   specified. See [[a-small-room-inside-a-big-one]].
5. **Enclosure vs measured absorption** in the room equation — still OPEN and now the most visible
   thing on the map: an open-deck garage measures 5.9 s where a real one is 2-4, and a 100 m tunnel
   4.8 s. The room equation uses `e/(1−e)` rather than `(1−ᾱ)/ᾱ`.
6. **Traffic, rail, aircraft** — traffic and aircraft are ON the map. Rail is the one thing left:
   [[rail-needs-a-multi-tap-voice]]. Aggregation (many distant sources into one) is untouched and is
   what the 121 machines are the case for.

**Why:** these were all queued behind an audible-rooms fix that took a session longer than it should
have.
**How to apply:** NONE OF SESSION 17 HAS BEEN HEARD. Every number in `docs/THE_CITY.md` is measured
and nothing is approved. Use `--tailcheck` before re-deriving any acoustic model — it meters the
OUTPUT, not the parameter.
