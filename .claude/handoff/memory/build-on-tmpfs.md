---
name: build-on-tmpfs
description: HISTORICAL - repo was on ntfs3; since 2026-10 it is on btrfs on the NVMe, so the ntfs hang is gone; still build via tools/build-local.sh (CPU pinning)
metadata:
  type: project
---

The open-fps repo lives on an ntfs3 volume whose driver stalls MSBuild's output phase. Build and test only with
`DOTNET_CLI_USE_MSBUILD_SERVER=0 ~/.dotnet/dotnet build <proj> --artifacts-path /tmp/openfps-<name> -nodeReuse:false -p:UseSharedCompilation=false`
(lab: /tmp/openfps-lab, server: /tmp/openfps-srv, gtk: /tmp/openfps-gtk, tests: /tmp/openfps-build).
The AudioLab must be RUN from its tmpfs bin dir (assets resolve from AppContext.BaseDirectory). `run-server.sh` / `run-gtk-client.sh` do this.

**Why:** a plain `dotnet run` hangs the shell for minutes.
**How to apply:** never `dotnet run` in this repo; use the artifacts-path form, and check a port-33288 server may already be running before smoke-testing the server.


**2026-10-11, Cody: "I'm no longer using the ntfs disk".** Checked: the repo is on /home, btrfs on the NVMe
(luks), not ntfs3, so a plain build would not hang now. Keep using tools/build-local.sh and test-local.sh anyway:
they pin the CPU and take the test lock ([[one-test-run-at-a-time]]). /tmp is still an 8 GB RAM tmpfs (not the
NVMe): with five agents' artifact dirs it reached 5.3 GB, so new briefs put artifacts on the NVMe
(~/.cache/openfps-build/<name>) and keep /tmp for small things. [[tmp-is-eight-gigabytes-of-ram]]
