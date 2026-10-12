---
name: hardening-2026-10-07
description: 10-07 hardening found a one-packet pre-login server crash (MemoryPack string header overflow) and /saveas path traversal; repo is PUBLIC and the VPS runs the old server - push held for Cody
metadata:
  type: project
---

Hardening agent 10-07 (docs/SERVER_SECURITY.md "Hardening, 2026-10-07"; tests ServerHardeningTests, 87).
Critical: MemoryPack 1.21.4 UTF-8 string header int.MinValue..+3 overflows its bounds check -> AccessViolation,
one LoginRequest killed the process (WireCheck now walks strings first; NOT fixed upstream - Cody may want to
report it to Cysharp); /saveas NAME wrote any .json anywhere; LiteNetLib fragment memory (FragmentGuard).
High: sound ids could point visitors' clients at any path, incl. \\host\share (Windows hands its login hash
over); premium items copyable via duplicate/row/group/replace; no rate limits; map data resent per request.
No wire change, Common unchanged by it.

**Why held:** github.com/churst90/open-fps is PUBLIC; the VPS (f1a0421123d3, 10-05) is unpatched; pushing the
fix and its docs publishes the attack. VPS/zip are on hold by Cody, so: merged LOCALLY only, not pushed, and
nothing after it pushed either, until Cody chooses (patch VPS first, or accept the risk).
Pending Cody: rate limits (chat 6 then 1/2 s; commands 20 then 5/s; edits 20 then 4/s, a row of 50 costs 6;
/join 3 then 1/10 s), line lengths 512/1,024, model_versions no cap yet - all recommended to keep.
