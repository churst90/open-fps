using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The presets a sound model names by key, as the catalogue has them: a map's own, or one changed in the
/// world editor, before the built-in. The catalogue (OpenFPS.Models, docs/ENGINE_PACKAGES.md stage 2) sits
/// above the sound and hands out its presets, so a model that names another (a train its horn, a mower its
/// engine, a tree's crown its places) is handed this by whoever makes it, and never asks the catalogue
/// itself.
/// </summary>
public interface IPresets
{
    /// <summary>An engine by its preset name, as changed in the world editor if it has been.</summary>
    EngineProfile Engine(string key);
    ChimeHornSpec Horn(string id);
    WhistleSpec Whistle(string id);
    StruckBellSpec Bell(string id);
    AirSystemSpec Air(string id);
    SmallMachineSpec SmallMachine(string key);
    WaterFeatureSpec Water(string key);
    FoliageSpec Foliage(string key);
    /// <summary>Running water by preset, or a drainage line of the ground by its key ("ground/...").</summary>
    RunningWaterSpec Flow(string key);
    /// <summary>A shore by preset, or by a map's key with its geometry ("shore:lake_sand/300/90/20").</summary>
    ShoreSpec Shore(string key);
    /// <summary>A fire by preset or by key, burning over the shape the key gives it.</summary>
    FireSpec Fire(string key);
    /// <summary>A hob by preset or by key.</summary>
    GasHobSpec GasHob(string key);
    /// <summary>The places a model's sound id is heard from, unscaled and uncapped; null for one heard from one
    /// point (ExtendedSources.Layout caps and caches them).</summary>
    Vector3[]? Places(string soundId);
}
