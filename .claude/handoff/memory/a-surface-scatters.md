---
name: a-surface-scatters
description: "Scattering splits a reflection into a mirror image and a wash radiated from the surface; the grandstand is an Audience material (2026-09-16)"
metadata:
  type: project
---

Cody: "the clapping reflections are crisp and they shouldn't be... a lot of the noise will be taken by
the open space so reflections shouldn't be so directional, more diffused than an exact copy like a
mirror."

`ReflectingSurface` carries `Scattering`. `ImageSource.FirstOrder` gives the MIRROR IMAGE only `(1-s)`
of what the surface reflects; the rest radiates FROM THE SURFACE, sampled at `diffuseTaps` points across
its width (2 for a transient). Each tap's level is the Lambert fraction
`A·cosθs·cosθl·direct² / (π·rs²·rl²)` — that is "taken by the open space" written down — and the taps
arrive over the spread of path lengths the face covers, which is what makes a wash instead of a copy.
The diffuse share is NOT gated by the specular bounce landing on the face: that test is what makes a
mirror a mirror, and a rough surface answers from wherever you stand.

Diffuse taps are OFF by default and only transients ask for them: a continuous source's scattered energy
is already in the ray-traced RT60 that drives the outdoor wet level; nothing accounted for a one-shot's.

`Audience` is a material (absorption 0.72, scattering 0.8) — an occupied seating area is the most
absorbent and most scattering thing in ordinary acoustics. The speedway's deck and upper back wall use
the `grandstand_seating` prefab; the retaining walls stay concrete and still slap. Measured at the back
straight: the crowd's return went from one specular arrival at gain **0.93** to two diffuse arrivals at
**0.105**, 88 and 92 ms out. A bike's slapback off the concrete wall is unchanged at 0.41.

**Also found by ear that evening:** when the Steam Audio voice pool is empty a sound plays without HRTF,
and it then used FMOD's LINEAR rolloff — over a 3 km range a source 200 m away comes out at 93% of full
scale, heard as an occasional sound "beamed right into your ears". The fallback uses INVERSE rolloff now
(the same law the HRTF path applies by hand), and the mixer load line reports `N without HRTF`.

See [[a-source-has-a-size]], [[region-is-not-a-room]], [[no-special-cases]].
