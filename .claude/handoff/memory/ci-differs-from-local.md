---
name: ci-differs-from-local
description: 10-11 - three CI-only failures in one day; stored tracks/streams must not depend on this machine (core count, libopus, libm rounding, ±pi)
metadata:
  type: feedback
---

2026-10-11 every push failed CI on stored test data that passed locally: ConcurrentDictionary order varies with
ProcessorCount (ranking ties), Concentus uses native libopus here but managed on the runner (0.02 dB), heading pi vs
-pi, and 2 mm libm drift after 57 s of traffic. Also one flaky worker test.

**Why:** GitHub's runner is 4-core, no libopus, different glibc; Cody's machine is 24-core AVX-512.
**How to apply:** any stored replay/track needs a maths fingerprint (exact on match, justified tolerance otherwise),
angles compared round the circle, ties broken by id, no native-library-dependent values; pre-push runs the replay
classes with `-e DOTNET_PROCESSOR_COUNT=4` too. Push the tested commit only; on a CI failure push the fix alone
on top. Related: [[tests-on-github]].
