---
name: street-life
description: Horns, hard stops and parking on the city — MapData.StreetLife, VehicleSystem.StreetLife/Parking partials; built 2026-09-23, NOT yet heard
metadata:
  type: project
---

Cody (09-23): honks "every now and again from different vehicles", "not a traffic jam"; tyre squeal
when braking too hard; cars parking, people getting out, doors. Rate is map data:
`city.json "StreetLife": {HornEverySeconds 45, HardBrakeEverySeconds 120, ParkEverySeconds 90}` —
map-wide Poisson events on a random OnStreet vehicle. Absent on the speedway = off.

- **Honk** travels as a WorldAudioEvent with `SynthKey = Honk.Key(horn, rhythm)` ("horn:electric:disc_pair:0.15,0.12,0.2");
  `WorldAudioPlayer.HornReceived` hands it to `ClientAudioSystem.UpdateHorns`, which plays a
  `HornVoiceState` ON the vehicle (id `HornVoiceBase` -1,200,000 - id). Horn per vehicle:
  `VehicleProfile.HornFor` (air systems -> air:truck_dual / air:bus_horn, else electric:disc_pair;
  declared: hatchback disc_single, motorcycles moto_disc, V12/Charger trumpet_pair).
- **ElectricHorn** (Signals/ElectricHorn.cs, --car-horn spike, WAVs inbox/car-horns-2026-09-23/): locked
  buzzer + armature strike + tone disc/trumpet column. Measured on note/level; not yet approved by ear.
- **Trains** blow long-long-short-long 18 s before each crossing (RailSystem.SoundForCrossings). Before
  this the live game NEVER blew a train horn — only the lab did.
- **Hard stop**: 0.92 x grip; squeal falls out of TyreDemand (onset 0.78); 55% honk after.
- **Parking**: spots found from geometry on first update (street-level door 3.5-16 m to the kerb side,
  clear straight walk); engine SynthRunning off/on, CarDoorSounds (shared with OccupancyService), a
  spawned walker NPC, DoorSystem.Set, BroadcastRemoval. Tests: StreetLifeTests.
See [[route-stops]], [[signals-and-air]], [[vehicles-do-not-occlude]].
