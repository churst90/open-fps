---
name: client-ui-and-chat
description: Chat channels, MOTD, four chat rings, UI sounds, Linux menus/settings/saved servers, F-key list menus and /join map travel — built 2026-09-24
metadata:
  type: project
---

Cody's decisions (2026-09-24): plain typing = your MAP, /all = everyone; Escape closes the command
box; Linux first but shared code in Client.Core (Mac will be a head too; he asked about a web
version); Connect goes straight to the preferred saved server.

- ChatMessage now carries Channel (Map/All/Private/Server), FromStaff, To (appended). No "System"
  label anywhere; command replies have no prefix; only server-to-everyone is "Server".
  /all, /announce, /motd, /setmotd; motd.txt in the server's cwd (OpenFPS.Server/), sent on FIRST
  spawn only.
- ChatManager rings: All, Map, Private, Server ([ ] read, Shift+[ ] change ring).
- UiSounds (Client.Core) + IAudioProvider.PlayUiSound (cached 2D buffers; old PlayUiBeep released
  its sound immediately = inaudible). Cues: menu move/select/back/edge, EnterWorld chord, per-chat.
- ClientSettings (Client.Core, ~/.config/openfps/client.json, 0600): UI sounds, devices, saved
  servers with Preferred and optional remembered password. GTK: Menus.cs (Saved Servers, Settings).
- MenuStack/ListMenu (Client.Core): F5 players, F6 maps, F8 friends; person submenu = PM (console
  pre-filled), Where is, View profile, Add/Remove friend. Map item -> /join.
- Server: GameServer.MoveToMap; second MapManifest mid-game = travel (client forgets old voices).
  friends.json (not the DB: EnsureCreated never migrates). PlayerListResponse.Usernames,
  FriendListResponse.Online appended.
- Air horns were thin because ChimeHorn's reed was open 46% of a cycle (fits a 5-bell chime only);
  ChimeHornSpec.ReedOpenFraction 0.30 for truck/bus. Car horns got a relay envelope (~20 ms in,
  ~50 ms out). WAVs inbox/car-horns-2026-09-24 — not yet heard.
Not built yet: Windows head adopting ClientSettings/Menus; microphone on Linux (input device is
stored only).

2026-09-24: shift-[ / shift-] did nothing on Linux — GTK reports shifted punctuation as a DIFFERENT
keyval (braceleft/braceright), and GtkKeyMap only mapped the unshifted names. Any new shift+punctuation
binding must map the shifted keyval too (letters are folded to lower case already). Fixed e426c24.
