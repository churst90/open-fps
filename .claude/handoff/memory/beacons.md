---
name: beacons
description: Beacon categories (door/exit/stairs/item/vehicle/waypoint), map BeaconPolicy, player /beacons switches — built 2026-09-23, unheard
metadata:
  type: project
---

`OpenFPS.Common/Beacons.cs` (policy table), `IdentityComponent.BeaconCategory` (appended; set by
what a thing IS: door prefab, Item, drivable composite, or a Beacon's own `BeaconCategory`),
`MapData.BeaconPolicy` -> `MapManifest.BeaconPolicy` ("cat=policy"), client `BeaconAids` (nearest
3 doors/12 m, 3 items/10 m, 2 cars/25 m, staggered 1.6 s, world-placed blips at 66 dB) and
`BeaconPreferences` (~/.config/openfps/beacons.json). `/beacons` is answered CLIENT-side in
`ClientGameSession.HandleCommandEntered`. City: 470 door beacons, 4 vehicle beacons. Authored
beacons (Type Beacon) keep their own emitter and are muted when their category is off.
**How to apply:** a new beacon-worthy kind of thing gets its category where it is SPAWNED, not on
the map. Voice ids -967000..-967023.
