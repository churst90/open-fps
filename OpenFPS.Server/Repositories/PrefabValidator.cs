using System.Numerics;
using System.Reflection;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>The problems found in one prefab file. Errors reject it; warnings are logged and it loads.</summary>
public sealed class PrefabValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks a prefab against what the engine can actually do with it, at load, and says exactly what is wrong.
///
/// The rules here are all the same rule: a prefab must not be able to describe something the engine will
/// then quietly ignore. Deserialization already accepts anything — an unknown key, a mis-cased enum name, a
/// negative range, emitter settings on an entity whose emitter is switched off — and the result is an entity
/// that spawns without the behaviour its author wrote down. In a game played entirely by ear, that is
/// indistinguishable from a bug in the audio engine, which is where the search then goes.
/// </summary>
public static class PrefabValidator
{
    private static readonly HashSet<string> KnownProperties = new(
        typeof(PrefabTemplate).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] FaceNames = { "north", "south", "east", "west", "top", "ceiling", "bottom", "floor" };

    /// <summary>Region face order, as read by the Sabine reverb math.</summary>
    public static readonly string[] RoomFaceOrder = { "Floor", "Ceiling", "North", "South", "East", "West" };

    public static bool IsKnownProperty(string name) => KnownProperties.Contains(name);

    public static PrefabValidationResult Validate(PrefabTemplate t, IEnumerable<string>? jsonPropertyNames = null)
    {
        var r = new PrefabValidationResult();
        string id = string.IsNullOrWhiteSpace(t.Id) ? "<no Id>" : t.Id;

        // --- The file itself ------------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(t.Id))
            r.Errors.Add("Id is missing. A prefab is referenced by Id from a map's PrefabId; without one it can never be spawned.");
        else if (t.Id.Any(char.IsWhiteSpace))
            r.Errors.Add($"Id '{t.Id}' contains whitespace. Use a plain lower-case identifier (letters, digits, underscore).");

        if (string.IsNullOrWhiteSpace(t.Name))
            r.Errors.Add("Name is missing. It is the only handle the player has on this entity — everything is spoken.");

        if (jsonPropertyNames != null)
        {
            var unknown = jsonPropertyNames.Where(n => !IsKnownProperty(n)).ToList();
            if (unknown.Count > 0)
                r.Errors.Add($"Unknown field(s) {string.Join(", ", unknown.Select(u => $"'{u}'"))}. " +
                             "The loader ignores what it does not recognise, so a typo here is a setting that never applies. " +
                             "See prefabs/prefab-schema.json for the full field list.");
        }

        // --- Material -------------------------------------------------------------------------------
        if (!string.IsNullOrEmpty(t.Material) && !AcousticRegistry.IsKnown(t.Material))
            r.Errors.Add($"Material '{t.Material}' is not a known material — it would silently fall back to 'Generic'. " +
                         $"Known: {string.Join(", ", AcousticRegistry.KnownMaterials())}.");

        // --- Collider -------------------------------------------------------------------------------
        bool hasCollider = t.ColliderSize.HasValue;
        var shape = t.Shape ?? ColliderShape.Box;

        if (hasCollider)
        {
            var s = t.ColliderSize!.Value;
            if (s.X <= 0 || s.Y <= 0 || s.Z <= 0)
                r.Errors.Add($"ColliderSize {Fmt(s)} has a non-positive extent. A zero-sized collider occupies nothing, " +
                             "blocks nothing and reflects nothing — omit ColliderSize instead.");
        }
        else if (t.Shape.HasValue)
        {
            r.Errors.Add("Shape is set but ColliderSize is not, so no collider is attached at all and the shape is ignored.");
        }

        if (t.IsSolid == true && !hasCollider)
            r.Errors.Add("IsSolid is true but there is no ColliderSize, so nothing is solid. Give it a ColliderSize or drop IsSolid.");

        bool isSolid = hasCollider && (t.IsSolid ?? true);

        if (isSolid && shape != ColliderShape.Box)
            r.Warnings.Add($"Shape '{shape}' on a SOLID collider: movement collides against the bounding box whatever the shape, " +
                           "and Steam Audio's scene is built from box colliders only — this will be walked around but not heard " +
                           "as an obstruction. Use Box for anything that should occlude sound.");

        // --- Health / item --------------------------------------------------------------------------
        if (t.MaxHealth is <= 0)
            r.Errors.Add($"MaxHealth {t.MaxHealth} must be greater than 0 (omit it for something that cannot be damaged).");

        if (t.IsItem && t.Type != EntityType.Item)
            r.Errors.Add($"IsItem is true but Type is '{t.Type}'. Type must be 'Item' — the two disagree about what this is.");
        if (!t.IsItem && t.Type == EntityType.Item)
            r.Errors.Add("Type is 'Item' but IsItem is false. The two disagree about what this is.");
        if (t.ItemWeight.HasValue && !t.IsItem)
            r.Errors.Add("ItemWeight is set but IsItem is false, so the weight applies to nothing.");
        if (t.ItemWeight is <= 0)
            r.Errors.Add($"ItemWeight {t.ItemWeight} must be greater than 0.");

        // --- Acoustics ------------------------------------------------------------------------------
        Unit(r, "TransmissionLow", t.TransmissionLow);
        Unit(r, "TransmissionMid", t.TransmissionMid);
        Unit(r, "TransmissionHigh", t.TransmissionHigh);
        Unit(r, "Absorption", t.Absorption);
        Unit(r, "Scattering", t.Scattering);
        if (t.ShellThickness is < 0)
            r.Errors.Add($"ShellThickness {t.ShellThickness} cannot be negative.");

        if (t.FaceMask.HasValue && t.MissingFaces != null)
            r.Errors.Add("FaceMask and MissingFaces both set — they describe the same thing and MissingFaces silently wins. Keep one.");
        if (t.FaceMask is < 0 or > 63)
            r.Errors.Add($"FaceMask {t.FaceMask} is outside 0..63 (North 1, South 2, East 4, West 8, Top 16, Bottom 32).");
        if (t.MissingFaces != null)
        {
            foreach (var face in t.MissingFaces)
            {
                if (!FaceNames.Contains(face?.ToLowerInvariant()))
                    r.Errors.Add($"MissingFaces entry '{face}' is not a face name. Use North, South, East, West, Top/Ceiling, Bottom/Floor.");
            }
        }

        // --- Physics --------------------------------------------------------------------------------
        if (t.Mass is < 0) r.Errors.Add($"Mass {t.Mass} cannot be negative.");
        if (t.Friction is < 0) r.Errors.Add($"Friction {t.Friction} cannot be negative.");
        Unit(r, "Restitution", t.Restitution);
        if (t.Drag is < 0) r.Errors.Add($"Drag {t.Drag} cannot be negative.");

        // --- Sound emitter --------------------------------------------------------------------------
        var emitterFields = new List<string>();
        void Emitter(string name, bool set) { if (set) emitterFields.Add(name); }
        Emitter("SoundId", !string.IsNullOrEmpty(t.SoundId));
        Emitter("StartSoundId", !string.IsNullOrEmpty(t.StartSoundId));
        Emitter("StopSoundId", !string.IsNullOrEmpty(t.StopSoundId));
        Emitter("Volume", t.Volume.HasValue);
        Emitter("Range", t.Range.HasValue);
        Emitter("MinDistance", t.MinDistance.HasValue);
        Emitter("Mode", t.Mode.HasValue);
        Emitter("EmitterDirection", t.EmitterDirection.HasValue);
        Emitter("ConeInsideAngle", t.ConeInsideAngle.HasValue);
        Emitter("ConeOutsideAngle", t.ConeOutsideAngle.HasValue);
        Emitter("ConeOutsideVolume", t.ConeOutsideVolume.HasValue);
        Emitter("IsGranular", t.IsGranular == true);
        Emitter("IsSynth", t.IsSynth == true);

        if (!t.HasEmitter && emitterFields.Count > 0)
            r.Errors.Add($"HasEmitter is false but {string.Join(", ", emitterFields)} " +
                         $"{(emitterFields.Count == 1 ? "is" : "are")} set. No SoundEmitterComponent is attached, so this entity " +
                         "is silent and those settings do nothing. Set HasEmitter to true, or remove them.");

        if (t.Type == EntityType.Beacon && !t.HasEmitter)
            r.Errors.Add("Type is 'Beacon' but HasEmitter is false. A beacon is exactly an entity whose sound emitter is " +
                         "switched on — a silent one is a landmark nobody can find.");

        if (t.HasEmitter)
        {
            bool synth = t.IsSynth == true;
            if (string.IsNullOrEmpty(t.SoundId) && !synth)
                r.Errors.Add("HasEmitter is true but SoundId is empty and IsSynth is false — there is nothing to play.");

            if (t.Volume is < 0) r.Errors.Add($"Volume {t.Volume} cannot be negative.");
            else if (t.Volume > 1f) r.Warnings.Add($"Volume {t.Volume} is above unity gain; the master limiter will pull it back.");

            if (t.Range is <= 0)
                r.Errors.Add($"Range {t.Range} must be greater than 0 — at 0 the emitter is never audible.");
            if (t.MinDistance is <= 0)
                r.Errors.Add($"MinDistance {t.MinDistance} must be greater than 0.");
            if (t.MinDistance.HasValue && t.Range.HasValue && t.MinDistance.Value >= t.Range.Value)
                r.Errors.Add($"MinDistance {t.MinDistance} is not less than Range {t.Range}; distance attenuation would be inverted.");

            Angle(r, "ConeInsideAngle", t.ConeInsideAngle);
            Angle(r, "ConeOutsideAngle", t.ConeOutsideAngle);
            Unit(r, "ConeOutsideVolume", t.ConeOutsideVolume);
            float inside = t.ConeInsideAngle ?? 360f, outside = t.ConeOutsideAngle ?? 360f;
            if (inside > outside)
                r.Errors.Add($"ConeInsideAngle {inside} is wider than ConeOutsideAngle {outside}; the cone is inside out.");

            if (t.EmitterDirection.HasValue)
            {
                if (t.EmitterDirection.Value.LengthSquared() <= 0f)
                    r.Errors.Add("EmitterDirection is the zero vector, which points nowhere. Omit it for the default forward (0,0,1).");
                else if (inside >= 360f && outside >= 360f)
                    r.Warnings.Add("EmitterDirection is set on an omnidirectional emitter (both cone angles 360), so it is inaudible. " +
                                   "Narrow ConeInsideAngle/ConeOutsideAngle to make the direction matter.");
            }

            if (t.IsGranular == true)
            {
                if (synth) r.Errors.Add("IsGranular and IsSynth are both true; an emitter is one or the other.");
                if (string.IsNullOrEmpty(t.SoundId)) r.Errors.Add("IsGranular needs a SoundId — grains are cut from a sample.");
                Unit(r, "GranularPosition", t.GranularPosition);
                if (t.GranularGrainSize is <= 0 or > 1000)
                    r.Errors.Add($"GranularGrainSize {t.GranularGrainSize} must be within 1..1000 milliseconds.");
                if (t.GranularDensity is <= 0) r.Errors.Add($"GranularDensity {t.GranularDensity} must be greater than 0 grains/second.");
                if (t.GranularPitch is <= 0) r.Errors.Add($"GranularPitch {t.GranularPitch} must be greater than 0.");
                Unit(r, "GranularPosJitter", t.GranularPosJitter);
                if (t.GranularPitchJitter is < 0) r.Errors.Add($"GranularPitchJitter {t.GranularPitchJitter} cannot be negative.");
            }

            if (synth)
            {
                if (t.SynthWave is < 0 or > 4)
                    r.Errors.Add($"SynthWave {t.SynthWave} is outside 0..4 (0 Sine, 1 Square, 2 Triangle, 3 Saw, 4 Noise).");
                if (t.SynthFreq is <= 0 or > 20000)
                    r.Errors.Add($"SynthFreq {t.SynthFreq} must be within 1..20000 Hz.");
                if (t.SynthLfoRate is < 0) r.Errors.Add($"SynthLfoRate {t.SynthLfoRate} cannot be negative.");
                Unit(r, "SynthLfoDepth", t.SynthLfoDepth);
                if (t.SynthFilterCutoff is <= 0 or > 1)
                    r.Errors.Add($"SynthFilterCutoff {t.SynthFilterCutoff} must be within 0..1 (a fraction of Nyquist; 1 is open).");
                Unit(r, "SynthFilterResonance", t.SynthFilterResonance);
                if (t.SynthPulseWidth is <= 0 or >= 1)
                    r.Errors.Add($"SynthPulseWidth {t.SynthPulseWidth} must be strictly between 0 and 1.");
            }
            else if (t.SynthWave.HasValue || t.SynthFreq.HasValue || t.SynthLfoRate.HasValue || t.SynthLfoDepth.HasValue ||
                     t.SynthFilterCutoff.HasValue || t.SynthFilterResonance.HasValue || t.SynthPulseWidth.HasValue)
            {
                r.Errors.Add("Synth parameters are set but IsSynth is false, so they are ignored and a sample is played instead.");
            }
        }

        // --- Region ---------------------------------------------------------------------------------
        bool declaresRegion = t.IsIndoor.HasValue || t.EnvType.HasValue || t.RoomSize.HasValue ||
                              !string.IsNullOrEmpty(t.AmbienceId) || t.ReverbScale.HasValue || t.RoomMaterials != null;

        if (declaresRegion)
        {
            if (isSolid)
                r.Errors.Add("This is an acoustic region AND a solid collider. A region is a volume the listener stands inside; " +
                             "solid geometry is a volume they cannot. Set IsSolid to false, or move the region fields to a " +
                             "separate 'acoustic_region' entity.");

            if (!t.RoomSize.HasValue || t.RoomSize.Value.X <= 0 || t.RoomSize.Value.Y <= 0 || t.RoomSize.Value.Z <= 0)
                r.Errors.Add($"A region needs a RoomSize with three positive extents (got {(t.RoomSize.HasValue ? Fmt(t.RoomSize.Value) : "none")}). " +
                             "Its volume and surface areas are what the reverb time is computed from.");

            if (t.ReverbScale is <= 0)
                r.Errors.Add($"ReverbScale {t.ReverbScale} must be greater than 0.");

            if (t.RoomMaterials != null)
            {
                if (t.RoomMaterials.Length != RoomFaceOrder.Length)
                    r.Errors.Add($"RoomMaterials has {t.RoomMaterials.Length} entries; it needs exactly {RoomFaceOrder.Length}, " +
                                 $"in the order {string.Join(", ", RoomFaceOrder)}.");
                for (int i = 0; i < t.RoomMaterials.Length; i++)
                {
                    if (!AcousticRegistry.IsKnown(t.RoomMaterials[i]))
                        r.Errors.Add($"RoomMaterials[{i}] ({(i < RoomFaceOrder.Length ? RoomFaceOrder[i] : "?")}) is " +
                                     $"'{t.RoomMaterials[i]}', which is not a known material. " +
                                     $"Known: {string.Join(", ", AcousticRegistry.KnownMaterials())}.");
                }
            }
        }

        // --- Portal ---------------------------------------------------------------------------------
        bool declaresPortal = t.RegionAId.HasValue || t.RegionBId.HasValue;

        if (declaresPortal || t.ApertureSize.HasValue)
        {
            if (isSolid)
                r.Errors.Add("This is a portal AND a solid collider — a doorway that blocks the doorway. Set IsSolid to false.");
            if (declaresRegion)
                r.Errors.Add("The same entity declares both an acoustic REGION and a PORTAL. A portal joins two regions; " +
                             "it cannot be one of them. Split them into two entities.");
            if (t.ApertureSize is < 0)
                r.Errors.Add($"ApertureSize {t.ApertureSize} cannot be negative (0 means 'derive it from the collider at map load').");
            if (t.ApertureSize is 0 or null && !hasCollider)
                r.Warnings.Add("Portal has neither an ApertureSize nor a collider to derive one from; it will fall back to 1 m.");
            if (t.RegionAId.HasValue && t.RegionBId.HasValue &&
                t.RegionAId.Value == t.RegionBId.Value && t.RegionAId.Value >= 0)
                r.Errors.Add($"RegionAId and RegionBId are both {t.RegionAId}. A portal joins two DIFFERENT regions " +
                             "(-1 means the outside); one that links a region to itself is ignored.");
            if (!declaresPortal && t.ApertureSize.HasValue)
                r.Warnings.Add("ApertureSize is set but neither RegionAId nor RegionBId is, so no portal is declared here. " +
                               "That is normal for a reusable 'portal' prefab whose map entity names the pair.");
        }

        return r;
    }

    private static void Unit(PrefabValidationResult r, string name, float? value)
    {
        if (value is < 0 or > 1) r.Errors.Add($"{name} {value} is outside 0..1.");
    }

    private static void Angle(PrefabValidationResult r, string name, float? value)
    {
        if (value is < 0 or > 360) r.Errors.Add($"{name} {value} is outside 0..360 degrees.");
    }

    private static string Fmt(Vector3 v) => $"({v.X}, {v.Y}, {v.Z})";
}
