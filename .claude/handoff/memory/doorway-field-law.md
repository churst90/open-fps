---
name: doorway-field-law
description: 10-02 the city washes into the lobby — send tap before the route EQ, FieldAt through every opening, outdoor reverb traced 2 m outside the door; unheard
metadata:
  type: project
---

Built 2026-10-02, MERGED to main b367bab8 (full suite 1687 pass), unheard. Also: route legs end 15 cm inside an opening and 10 cm off its face, or an open leaf hinged on the jamb was charged as steel (CityZoneTests):
- Own-room reverb send comes off a MIXER pass-through at the channel's TAIL (input end), mix x
  LastVolume; cross-send stays at the FADER. Pooled `_sendTapPool`; release = Detach then
  disconnectAll(true,true) before pooling (a pooled tap keeping a send would feed a room from the
  next voice — the [[the-mixer-thread-crash]] class).
- Other places' bus volume = `OpeningRoutes.FieldAt` (S tau/(8 pi r^2) per opening, straight or by
  Route, + transmission-room term E_out S tau / A into the listener's room). Cached 0.25 s/0.5 m.
  Old (aperture/2)/dist rule kept only when both places are the Outside node or no routes.
- Open ground heard from indoors: TracedReverbSet.ForRoom(region, 2 m outside the best opening).
- One speed of sound: AudioPhysics.CurrentSpeedOfSound (set from temperature); wind Doppler gone.
Openings geometry (every open side an opening, composites' open faces) was a parallel agent's job.
See [[portal-penalty-leak]], [[traced-reverb]].
