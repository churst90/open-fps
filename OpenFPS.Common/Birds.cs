using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>Where a species lives, which is how the map says where the birds are without anybody
/// placing one: foliage is foliage because of its material, a roof because of its height.</summary>
public enum BirdHabitat { Foliage, Roof, Sky }

/// <summary>
/// One kind of bird: what it sounds like, how loud, and the rhythm it calls in.
///
/// The calls are recordings — one bird, one call per file, under ASSETS/SOUNDS/BIRDS/&lt;Folder&gt; —
/// and a group is NEVER a recording of a group. A hedge of sparrows is a dozen sparrows, each in its
/// own place in the hedge, each with its own voice (a fixed pitch of its own) and its own rhythm,
/// which is what makes a hedge you can walk along rather than a loop hung in the air.
///
/// Birds call in BOUTS: a run of calls a second or so apart, then quiet for a while. That is most of
/// what separates a bird from a beeping machine, and the numbers below are the species' own.
/// </summary>
public sealed record BirdSpecies
{
    public required string Name { get; init; }
    /// <summary>Folder under ASSETS/SOUNDS/BIRDS.</summary>
    public required string Folder { get; init; }
    public required BirdHabitat Habitat { get; init; }

    /// <summary>SPL of one call at one metre, dB. Small songbirds are 70-80, a crow 80-90, a goose
    /// on the wing 90-95; a dove's coo is soft for its size.</summary>
    public required float CallDb { get; init; }

    /// <summary>How many live together at one perch; a solitary species is 1..1.</summary>
    public int GroupMin { get; init; } = 1;
    public int GroupMax { get; init; } = 1;

    /// <summary>Calls in one bout, and the seconds between them.</summary>
    public int BoutCallsMin { get; init; } = 1;
    public int BoutCallsMax { get; init; } = 3;
    public float CallGapMin { get; init; } = 0.8f;
    public float CallGapMax { get; init; } = 2f;
    /// <summary>Seconds of quiet between bouts.</summary>
    public float BoutGapMin { get; init; } = 10f;
    public float BoutGapMax { get; init; } = 40f;

    /// <summary>How far one bird's voice sits from the next, as a fraction of pitch either way. Two
    /// sparrows are not the same sparrow, and a playback rate a few percent apart is heard as two
    /// birds rather than as one recording.</summary>
    public float PitchSpread { get; init; } = 0.04f;

    /// <summary>How much a neighbour calling shortens this bird's wait, 0..1 — the contagion that
    /// makes a hedge of sparrows break out together and then go quiet together.</summary>
    public float Contagion { get; init; }

    /// <summary>A sound this loud where the bird is sends it quiet, dB SPL.</summary>
    public float StartleDb { get; init; } = 85f;

    /// <summary>How close a person may come before it stops calling, metres.</summary>
    public float ShyMetres { get; init; } = 3f;

    // ── The birds in the samples Cody collected, 2026-09-23 ──────────────────────────────────

    public static BirdSpecies HouseSparrow => new()
    {
        Name = "house sparrow", Folder = "SPARROW", Habitat = BirdHabitat.Foliage,
        CallDb = 74f, GroupMin = 2, GroupMax = 6,
        BoutCallsMin = 3, BoutCallsMax = 10, CallGapMin = 0.35f, CallGapMax = 1.1f,
        BoutGapMin = 15f, BoutGapMax = 75f, PitchSpread = 0.05f, Contagion = 0.5f,
        StartleDb = 80f, ShyMetres = 2.5f,
    };

    /// <summary>A collared-type dove: not the mourning dove's long "coo-OO-oo-oo", but a dove, and
    /// alone — one you hear every now and then from a tree.</summary>
    public static BirdSpecies Dove => new()
    {
        Name = "dove", Folder = "DOVE", Habitat = BirdHabitat.Foliage,
        CallDb = 70f, BoutCallsMin = 2, BoutCallsMax = 5, CallGapMin = 1.6f, CallGapMax = 3.2f,
        BoutGapMin = 30f, BoutGapMax = 120f, PitchSpread = 0.02f, StartleDb = 82f, ShyMetres = 4f,
    };

    public static BirdSpecies Pigeon => new()
    {
        Name = "pigeon", Folder = "PIGEON", Habitat = BirdHabitat.Roof,
        CallDb = 64f, GroupMin = 3, GroupMax = 12,
        BoutCallsMin = 1, BoutCallsMax = 4, CallGapMin = 1.5f, CallGapMax = 4f,
        BoutGapMin = 8f, BoutGapMax = 40f, PitchSpread = 0.04f, Contagion = 0.2f,
        StartleDb = 88f, ShyMetres = 3f,
    };

    public static BirdSpecies Crow => new()
    {
        Name = "crow", Folder = "CROW", Habitat = BirdHabitat.Roof,
        CallDb = 86f, GroupMin = 1, GroupMax = 2,
        BoutCallsMin = 2, BoutCallsMax = 5, CallGapMin = 0.7f, CallGapMax = 1.3f,
        BoutGapMin = 25f, BoutGapMax = 150f, PitchSpread = 0.05f, Contagion = 0.6f,
        StartleDb = 95f, ShyMetres = 6f,
    };

    /// <summary>Canada geese, going over in a skein. They honk almost continually on the wing.</summary>
    public static BirdSpecies Goose => new()
    {
        Name = "goose", Folder = "GOOSE", Habitat = BirdHabitat.Sky,
        CallDb = 92f, GroupMin = 6, GroupMax = 14,
        BoutCallsMin = 3, BoutCallsMax = 10, CallGapMin = 0.5f, CallGapMax = 1.4f,
        BoutGapMin = 1f, BoutGapMax = 5f, PitchSpread = 0.05f, Contagion = 0.4f,
        StartleDb = 200f, ShyMetres = 0f,
    };

    public static IReadOnlyList<BirdSpecies> All { get; } = new[] { HouseSparrow, Dove, Pigeon, Crow, Goose };
}
