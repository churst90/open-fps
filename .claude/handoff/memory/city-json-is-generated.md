---
name: city-json-is-generated
description: city.json must be byte-identical to `python3 tools/gen_city.py` output (RoadNetworkTests); edit the generator and regenerate, never hand-edit
metadata:
  type: project
---

`RoadNetworkTests.The_generator_reproduces_the_shipped_city` runs tools/gen_city.py and compares bytes
with OpenFPS.Server/maps/city.json. Hand edits (even with matching content) renumber entities and
fail it — happened 2026-10-02 twice (an openings agent and my Market Square fix). Supersedes the old
note in [[small-machines]] that gen_city.py is behind city.json: they are in step now.

**How to apply:** change gen_city.py, run `python3 tools/gen_city.py`, commit both, restart the
server ([[restart-the-server-always]]). Brief subagents the same way.
