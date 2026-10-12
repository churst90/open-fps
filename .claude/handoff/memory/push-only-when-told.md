---
name: push-only-when-told
description: Cody 2026-10-09 - never push to GitHub (or deploy) until he explicitly says to; merge locally only
metadata:
  type: feedback
---

Cody 2026-10-09, twice: "do not push yet" and "do not push, only do so when I say to do so".

**Why:** the repo is public and pushes go out with the VPS/zip build-hash coupling; he decides when a batch ships.
**How to apply:** merge agent branches into local main, commit, but never `git push` (main or any branch, tags
included) and never deploy to the VPS or build a release zip until he says so in that moment. Earlier
permission to push does not carry over. Put "do not push" in every agent brief. Related: [[hardening-2026-10-07]].
