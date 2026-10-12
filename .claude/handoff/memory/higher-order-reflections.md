---
name: higher-order-reflections
description: 2nd/3rd-order echoes are for ONE-OFF sounds only (WorldAudioPlayer); on sustained sources they made stationary ghost washes and put far sirens in front — reverted there
metadata:
  type: project
---

Built 2026-09-23. `EarlyReflections.Find(..., maxOrder, separateFirst)`; `MaxOrder = 3`,
`HigherOrderSurfaces = 8`. AsyncAcousticWorker asks for 3 only when the listener's region is not an
enclosure (`RoomAcoustics.IsEnclosure`) — in a room the copies of copies are DENSE, they are the
tail, and the reverb bus already renders them. Footstep reflections still call with defaults.

Three things that were wrong on the way, each found by a test:
1. The MaxArrivals=4 cap kept the LOUDEST four — ground and near-wall copies inside the 50 ms
   fusion window, which the renderer then drops. The slapback and the flutter never got a slot.
   Renderers that voice only separate events pass `separateFirst: true`.
2. Choosing chain surfaces by size/distance picked the ground and the floor slabs INSIDE the towers
   on Main Street (huge, near, hidden): zero chains survived. The mirrors are now the faces that
   gave a valid FIRST-ORDER arrival — visible to both ends by construction.
3. `ReflectionsInARoomAreNotSeparateEvents` / step-reflection tests broke when the ranking changed
   for everyone — hence the two parameters rather than a global change.

Measured: two facades 20 m apart hand a clap back at 88 (1st), 89/106 (2nd) ms; on the city's Main
Street 2nd/3rd-order echoes at 107-145 ms; search 10 -> ~15 ms mean per source.
**Not yet:** engine echoes (EngineReflections/ImageSource.FirstOrder) are still first order only.
See [[a-reflection-is-the-room-answering]], [[a-small-room-inside-a-big-one]].

**2026-09-23, heard and REVERTED for sustained sources.** Cody: "ghostly washes of white noise off in
the distance that stay in one place and cut in and out", "sirens in front of me when they are super
far away" (runway, 304 -140). The worker (AsyncAcousticWorker) renders reflections of CONTINUOUS
emitters — jets, bus air hiss, sirens, machines — and 3rd order + separateFirst gave each a mirrored
copy standing still at the image, flickering as paths came and went. A sustained sound's echo is not
an event; it is the field (reverb). Worker and SpatialAcoustics are back to first order, old ranking.
Higher orders now live in `WorldAudioPlayer.QueueHigherOrderEchoes` — one-off sounds, outdoors, <=3.
**Also found:** one-off echoes were DOUBLE-delayed (DueAt + extra delay, AND the facade's distance
delay since the FMOD setDelay clock fix) — a facade slapback at 180 ms instead of 90. DueAt is now
item.DueAt for both first and higher order.
**Rule:** discrete echo voices only for impulsive, one-off sounds.

