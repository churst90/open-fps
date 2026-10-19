namespace OpenFPS.Client.Core;

/// <summary>What comma and period step through: one kind of thing at a time. It lives here rather than
/// beside MapTracker because the beacon a picked thing makes is a beacon kind (BeaconAids.Ping); the
/// tracker moves onto the scene view last (docs/ENGINE_PACKAGES.md, stage 9).</summary>
public enum TrackCategory { Doors, Entrances, Stairs, Items, People, Vehicles, Places }
