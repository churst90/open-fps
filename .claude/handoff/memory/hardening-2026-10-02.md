---
name: hardening-2026-10-02
description: 10-02 merged to main (ab81545a) — mutation-triage bug fixes, traffic rule scenes, login hardening, admin commands, old code out; Common changed so Windows zip + VPS server need updating
metadata:
  type: project
---

Merged to main 2026-10-02 (ab81545a), full suite 1,687 passed / 2 skipped.
- Fixed: /draw entity 0, disconnect drops items, "good day" at night, phone goodbyes, crossing tail,
  rail height/one-stop, NaN input, give-way speed, deadlock breaker, three crosswalk faults.
- TrafficRuleTests pins each road rule by a fixed scene; StaffGateTests every gated command.
- Logins: docs/SERVER_SECURITY.md. One session per account — a MUD walkthrough on the live server
  with Cody's account CLOSES his session; use a separate account. See [[mud-walkthrough]].
- OpenFPS.Common changed (messages/components removed): new Windows zip for the friend AND update the
  VPS server, or logins are refused on build hash. See [[windows-client]].
- Cody's 2026-10-02 list is todo.md "Now 0"; answers in docs/RESEARCH_2026-10-02.md. .357 demo
  unheard: inbox/gunfire-357-2026-10-02/demo/. Mutation on hold: [[mutation-campaigns-2026-10-01]].
