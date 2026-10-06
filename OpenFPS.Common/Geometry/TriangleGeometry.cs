using System;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Whether the game asks the triangle world (docs/GEOMETRY.md, stage 1) or the old box indexes. On by
/// default; <c>OPENFPS_TRIANGLES=0</c> in the environment turns it off for the whole process, server or
/// client, and every query answers from the boxes as it did before. Both paths stay until the parity
/// harness (AudioLab --geometry-parity) has had its say on every map and every consumer.
/// </summary>
public static class TriangleGeometry
{
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("OPENFPS_TRIANGLES") != "0";
}
