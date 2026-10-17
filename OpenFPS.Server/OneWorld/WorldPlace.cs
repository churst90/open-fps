namespace OpenFPS.Server.OneWorld;

/// <summary>A place in the world a player can arrive at: what it is called and where it is.</summary>
public sealed record WorldPlace(string Id, string Name, double Lat, double Lon);
