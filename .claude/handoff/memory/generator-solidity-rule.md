---
name: generator-solidity-rule
description: gen_city.py must read a prefab with a collider as solid unless IsSolid is false (server rule); walkers were inside the tunnel walls
metadata:
  type: project
---

09-27 Cody, outside the tunnel: "I think I can hear someone walking inside the tunnel". Cause: the
walks at x=±7.05/±8.45 ran THROUGH the tunnel's concrete side fill (and a Wharf Ave concrete wall),
because gen_city.py `_solid_boxes` read a missing IsSolid as false while PrefabRepository uses
`IsSolid ?? true`; 24 of 42 prefabs omit it (concrete_wall among them). Fixed 50f3c79;
`PedestrianSpeechTests.NoPedestrianWalksThroughAnythingSolid` fails on the old map.
**How to apply:** any python tool that reads prefabs must use the server's defaults; when a sound
comes from somewhere impossible, check where the SOURCE is before the acoustics.
Also: gen_city.py reproduced city.json byte-for-byte on 09-27 (no longer behind).
