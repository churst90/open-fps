using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Acoustics;
using OpenFPS.Common.Components;
using OpenFPS.Materials;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Items that declare a shape and ring as what they are (docs/PHYSICS_BOUNDARY.md 18.12): the beam's mode ratios,
/// a tube's note above a bar of its own length and mass, steel ringing longer than wood, a free ring longer than a
/// mounted one, the Hertz contact being shorter for steel than for wood, the shape crossing the wire, and the old
/// `Between` and the old Ring and Knock renders staying byte for byte as they were.
/// </summary>
public class ItemShapeTests
{
    public ItemShapeTests() => AcousticRegistry.Initialize();

    private static MaterialProperties Of(string name) => AcousticRegistry.GetProperties(name);

    private static readonly ItemShape SteelBar = new(ItemShapeKind.Bar, new Vector3(1f, 0.025f, 0.025f));
    private static readonly ItemShape WoodBar = new(ItemShapeKind.Bar, new Vector3(1f, 0.1f, 0.025f));
    private static readonly ItemShape SteelTube = new(ItemShapeKind.Tube, new Vector3(1f, 0.04f, 0.04f));

    // ── The laws ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A free-free beam's first four roots give ratios 1, 2.756, 5.404, 8.933 (Rayleigh I section 170).</summary>
    [Fact]
    public void AFreeBeamRingsAtTheFreeFreeRatios()
    {
        var modes = ItemModes.Free(Of("Metal"), SteelBar, 4.9f);
        Assert.Equal(4, modes.Count);
        float[] expected = { 1f, 2.7559f, 5.4039f, 8.9329f };
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], modes[i].Hz / modes[0].Hz, 0.002f);
    }

    /// <summary>Steel's loss is 0.0002 against wood's 0.03: the same shape rings longer.</summary>
    [Fact]
    public void SteelRingsLongerThanWoodOfTheSameShape()
    {
        var steel = ItemModes.Free(Of("Metal"), SteelBar, 4.9f);
        var wood = ItemModes.Free(Of("Wood"), SteelBar, 4.9f);
        Assert.NotEmpty(steel);
        Assert.NotEmpty(wood);
        Assert.True(steel[0].Seconds > wood[0].Seconds * 2f,
            $"steel {steel[0].Seconds:F3} s at {steel[0].Hz:F0} Hz against wood {wood[0].Seconds:F3} s at {wood[0].Hz:F0} Hz");
    }

    /// <summary>Nothing holds a dropped thing: its ring is the material's loss alone, not a mounting's.</summary>
    [Fact]
    public void AFreeRingIsLongerThanAMountedOne()
    {
        float free = PanelAcoustics.RingSeconds(Of("Metal"), 500f, mountingLoss: 0f);
        float mounted = PanelAcoustics.RingSeconds(Of("Metal"), 500f, PanelAcoustics.MountedLoss);
        Assert.True(free > mounted, $"{free:F3} s free against {mounted:F3} s mounted");
    }

    /// <summary>A tube of the same length and mass has its material farther from the axis than a solid bar, so its
    /// radius of gyration is larger and its bending note higher: I/A is what a tube spends its mass on.</summary>
    [Fact]
    public void ATubeRingsHigherThanABarOfTheSameLengthAndMass()
    {
        var bar = ItemModes.Free(Of("Metal"), SteelBar, 4.9f);
        var tube = ItemModes.Free(Of("Metal"), SteelTube, 4.9f);
        Assert.NotEmpty(bar);
        Assert.NotEmpty(tube);
        Assert.True(tube[0].Hz > bar[0].Hz, $"tube {tube[0].Hz:F0} Hz against bar {bar[0].Hz:F0} Hz");
    }

    /// <summary>Hertz: 1/E* is the pair's, so steel on concrete is a shorter contact than wood on concrete.</summary>
    [Fact]
    public void SteelOnConcreteIsAShorterContactThanWoodOnConcrete()
    {
        float steel = ItemModes.ContactSeconds(Of("Metal"), Of("Concrete"), 4.9f, 1e6f, 0.0125f, 3.1f);
        float wood = ItemModes.ContactSeconds(Of("Wood"), Of("Concrete"), 1.63f, 1e6f, 0.0125f, 3.1f);
        Assert.True(steel < wood, $"steel {steel * 1e6:F0} us against wood {wood * 1e6:F0} us");
        Assert.True(ItemModes.ContactHz(steel) > ItemModes.ContactHz(wood));
    }

    /// <summary>The drop's blow is brighter for steel on concrete than wood on concrete, and the dropped thing
    /// carries its own modes beside the struck floor's ring.</summary>
    [Fact]
    public void ADropTakesItsBrightnessFromBothMaterialsAndRingsItsOwnShape()
    {
        var steel = ImpactAcoustics.Dropped(Of("Metal"), Of("Concrete"), SteelBar, Vector3.Zero, 4.4f, 4.9f, 1e6f,
                                            2f, 2f, 0.2f, struckIsFixed: true);
        var wood = ImpactAcoustics.Dropped(Of("Wood"), Of("Concrete"), WoodBar, Vector3.Zero, 4.4f, 1.63f, 1e6f,
                                           2f, 2f, 0.2f, struckIsFixed: true);
        var steelKnock = steel.First(s => s.Character == SoundCharacter.Knock);
        var woodKnock = wood.First(s => s.Character == SoundCharacter.Knock);
        Assert.True(steelKnock.Hz > woodKnock.Hz, $"steel {steelKnock.Hz:F0} Hz against wood {woodKnock.Hz:F0} Hz");

        var own = steel.First(s => s.Character == SoundCharacter.Ring && s.ModeRatios is { Length: > 0 });
        Assert.Equal(4, own.ModeRatios!.Length);
        Assert.Equal(own.ModeDecaySeconds!.Length, own.ModeRatios.Length);
        Assert.Equal(ItemModes.Free(Of("Metal"), SteelBar, 4.9f)[0].Hz, own.Hz, own.Hz * 0.01f);
    }

    // ── The data ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A declared shape survives the wire exactly, extents and wall included.</summary>
    [Fact]
    public void ShapeDataRoundTrips()
    {
        var original = new ItemComponent
        {
            MassKg = 2.74f,
            Hands = 1,
            WeaponId = "",
            ShapeKind = ItemShapeKind.Tube,
            ShapeSize = new Vector3(1f, 0.04f, 0.04f),
            ShapeWallMetres = 0.003f,
        };
        var bytes = MemoryPackSerializer.Serialize(original);
        var back = MemoryPackSerializer.Deserialize<ItemComponent>(bytes);
        Assert.Equal(original.ShapeKind, back.ShapeKind);
        Assert.Equal(original.ShapeSize, back.ShapeSize);
        Assert.Equal(original.ShapeWallMetres, back.ShapeWallMetres);
        Assert.Equal(original.MassKg, back.MassKg);
    }

    /// <summary>Every shipped item's prefab declares a shape and extents, and the item that reaches the world carries
    /// them; an item that declares nothing still falls as the old cube.</summary>
    [Fact]
    public void EveryShippedItemDeclaresAShapeAndTheCubeRemainsTheFallback()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        var repo = new PrefabRepository(dir);
        foreach (var (id, template) in repo.Prefabs)
        {
            if (!template.IsItem) continue;
            Assert.True(template.ItemShape.HasValue, $"{id} declares no ItemShape");
            Assert.True(template.ItemSize.HasValue, $"{id} declares no ItemSize");
        }

        using var world = World.Create();
        var spawned = repo.Spawn(world, "crowbar", Vector3.Zero);
        Assert.True(world.Has<ItemComponent>(spawned));
        var carried = world.Get<ItemComponent>(spawned);
        Assert.Equal(ItemShapeKind.Bar, carried.ShapeKind);
        Assert.Equal(new Vector3(1f, 0.025f, 0.025f), carried.ShapeSize);

        var crowbar = world.Create(
            new ItemComponent { MassKg = 5f, Hands = 1, WeaponId = "", ShapeKind = ItemShapeKind.Bar,
                                ShapeSize = new Vector3(1f, 0.025f, 0.025f) },
            new MaterialComponent { Material = "Metal" });
        Assert.True(DroppedThings.TryBody(world, crowbar, out ItemShape shape, out float mass, out string material));
        Assert.Equal(ItemShapeKind.Bar, shape.Kind);
        Assert.Equal(new Vector3(1f, 0.025f, 0.025f), shape.Size);
        Assert.Equal(5f, mass);
        Assert.Equal("Metal", material);

        var lump = world.Create(new ItemComponent { MassKg = 5f, Hands = 1, WeaponId = "" },
                                new MaterialComponent { Material = "Metal" });
        Assert.True(DroppedThings.TryBody(world, lump, out ItemShape cube, out _, out _));
        Assert.Equal(ItemShapeKind.Block, cube.Kind);
        Assert.Equal(MathF.Cbrt(5f / 7850f), cube.Size.X, 1e-4f);
    }

    // ── Nothing approved moves ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every non-drop caller of `Between` (WallBumps, DrivingSystem's crashes, CombatService's falls, FireSynth's
    /// table and the lab's old bump) still gets the old law's answer: this is that law, copied, and the public one
    /// must match it field for field over the materials, masses, sizes and speeds they use.
    /// </summary>
    [Fact]
    public void EveryNonDropCallerOfBetweenReturnsWhatItDidBefore()
    {
        string[] materials = { "Metal", "Wood", "Glass", "Concrete", "Carpet", "Plastic", "Generic" };
        float[] masses = { 0.02f, 4f, 75f, 1500f, 14000f };
        float[] sizes = { 0.05f, 0.3f, 1f, 2f, 4f };
        foreach (string hitter in materials)
            foreach (string struck in materials)
                foreach (float mass in masses)
                    foreach (float size in sizes)
                        foreach (float speed in new[] { 0.1f, 1.2f, 5f, 12f })
                            foreach (bool fixedThing in new[] { true, false })
                            {
                                var before = BetweenBeforeShapes(Of(hitter), Of(struck), new Vector3(1, 2, 3), speed,
                                                                 mass, mass * 500f, size, size, size / 10f, fixedThing);
                                var now = ImpactAcoustics.Between(Of(hitter), Of(struck), new Vector3(1, 2, 3), speed,
                                                                  mass, mass * 500f, size, size, size / 10f, fixedThing);
                                Assert.Equal(before.Count, now.Count);
                                for (int i = 0; i < before.Count; i++)
                                {
                                    Assert.Equal(before[i].Character, now[i].Character);
                                    Assert.Equal(before[i].DelaySeconds, now[i].DelaySeconds);
                                    Assert.Equal(before[i].Position, now[i].Position);
                                    Assert.Equal(before[i].LevelDb, now[i].LevelDb);
                                    Assert.Equal(before[i].Hz, now[i].Hz);
                                    Assert.Equal(before[i].DecaySeconds, now[i].DecaySeconds);
                                    Assert.Equal(before[i].Noisiness, now[i].Noisiness);
                                    Assert.Null(now[i].ModeRatios);
                                }
                            }
    }

    /// <summary>The old Ring and Knock render byte for byte: a copy of the old synthesiser alongside the shipped one.</summary>
    [Fact]
    public void ExistingRingAndKnockRenderAsBefore()
    {
        var sounds = new[]
        {
            new TransientSound { Character = SoundCharacter.Ring, Hz = 1400f, DecaySeconds = 1.2f, Noisiness = 0.2f },
            new TransientSound { Character = SoundCharacter.Ring, Hz = 120f, DecaySeconds = 2.5f, Noisiness = 0.12f },
            new TransientSound { Character = SoundCharacter.Knock, Hz = 220f, DecaySeconds = 0.4f, Noisiness = 0.6f },
            new TransientSound { Character = SoundCharacter.Knock, Hz = 3500f, DecaySeconds = 0.02f, Noisiness = 0.9f },
        };
        int seed = 1;
        foreach (var sound in sounds)
        {
            var now = TransientSynth.Render(sound, seed);
            var before = RenderBeforeShapes(sound, seed++);
            Assert.Equal(before.Length, now.Length);
            for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], now[i]);
        }
    }

    private static List<TransientSound> BetweenBeforeShapes(MaterialProperties hitter, MaterialProperties struck, Vector3 where,
                                                            float closingSpeed, float hitterMassKg, float struckMassKg,
                                                            float struckWidth, float struckHeight, float struckThickness,
                                                            bool struckIsFixed)
    {
        var sounds = new List<TransientSound>(2);
        if (closingSpeed < ImpactAcoustics.MinimumSpeed) return sounds;
        float joules = PanelAcoustics.ImpactJoules(hitterMassKg, struckMassKg, closingSpeed);
        float db = PanelAcoustics.ImpactDb(joules);
        var softer = hitter.YoungsModulusGPa <= struck.YoungsModulusGPa ? hitter : struck;
        float softness = 1f / (1f + MathF.Max(0f, softer.YoungsModulusGPa));
        float reduced = PanelAcoustics.ImpactJoules(hitterMassKg, struckMassKg, 1f) * 2f;
        float sizeScale = 1f / MathF.Pow(MathF.Max(0.02f, reduced), 0.28f);
        float lengthScale = MathF.Pow(MathF.Max(0.02f, reduced), 0.18f);
        sounds.Add(new TransientSound
        {
            Character = SoundCharacter.Knock, DelaySeconds = 0f, Position = where, LevelDb = db,
            Hz = Math.Clamp((1400f * (1f - softness) + 70f) * sizeScale, 45f, 3000f),
            DecaySeconds = Math.Clamp((0.02f + softness * 0.25f) * lengthScale, 0.02f, 0.6f),
            Noisiness = 0.85f,
        });
        float hz = PanelAcoustics.RingHz(struck, struckWidth, struckHeight, struckThickness);
        if (hz > 0f)
        {
            float mounting = struckIsFixed ? PanelAcoustics.MountedLoss : 0f;
            float seconds = PanelAcoustics.RingSeconds(struck, hz, mounting);
            if (PanelAcoustics.RingsAudibly(struck, hz, mounting))
                sounds.Add(new TransientSound
                {
                    Character = SoundCharacter.Ring, DelaySeconds = 0.003f, Position = where, LevelDb = db - 5f,
                    Hz = hz, DecaySeconds = seconds, Noisiness = 0.12f,
                });
        }
        return sounds;
    }

    // ── The old synthesiser, copied for the byte-for-byte guard ───────────────────────────────────

    private static float[] RenderBeforeShapes(TransientSound sound, int seed)
    {
        float seconds = Math.Clamp(sound.DecaySeconds, 0.005f, 3f);
        int samples = Math.Max(16, (int)(seconds * TransientSynth.SampleRate));
        var buffer = new float[samples];
        var rng = new Random(seed);
        float hz = MathF.Max(20f, sound.Hz * (0.97f + 0.06f * (float)rng.NextDouble()));
        if (sound.Character == SoundCharacter.Ring) RingBefore(buffer, hz, seconds, sound.Noisiness, rng);
        else KnockBefore(buffer, hz, seconds, sound.Noisiness, rng);
        float peak = 0f;
        foreach (float v in buffer) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak >= 1e-6f)
        {
            float gain = 0.98f / peak;
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= gain;
        }
        return buffer;
    }

    private static void KnockBefore(float[] buffer, float hz, float seconds, float noisiness, Random rng)
    {
        Span<float> ratios = stackalloc float[] { 1f, 1.71f, 2.63f, 4.07f, 6.2f };
        Span<float> gains = stackalloc float[] { 1f, 0.72f, 0.5f, 0.31f, 0.17f };
        float noise = Math.Clamp(noisiness, 0f, 1f);
        float q = 2.5f + 24f * (1f - noise);
        Span<ResonatorBefore> modes = stackalloc ResonatorBefore[5];
        int used = 0;
        for (int m = 0; m < 5; m++)
        {
            float f = hz * ratios[m];
            if (f > TransientSynth.SampleRate * 0.45f) break;
            modes[m] = new ResonatorBefore(f, q);
            used++;
        }
        if (used == 0) { modes[0] = new ResonatorBefore(hz, q); used = 1; }
        float k = 6.9f / (seconds * TransientSynth.SampleRate);
        float contact = MathF.Max(1f, 0.0018f * TransientSynth.SampleRate);
        float bright = 1f - MathF.Exp(-2f * MathF.PI * MathF.Min(hz * 6f, TransientSynth.SampleRate * 0.45f) / TransientSynth.SampleRate);
        float lp = 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            float raw = i == 0 ? 1f : (float)(rng.NextDouble() * 2.0 - 1.0) * MathF.Exp(-i / contact);
            lp += bright * (raw - lp);
            float v = 0f;
            for (int m = 0; m < used; m++)
                v += modes[m].Process(raw) * gains[m] * MathF.Exp(-k * i * (1f + m * 0.55f));
            buffer[i] = v + lp * noise * 0.6f * MathF.Exp(-k * i * 3f);
        }
    }

    private static void RingBefore(float[] buffer, float hz, float seconds, float noisiness, Random rng)
    {
        Span<float> partials = stackalloc float[] { 1f, 2.39f, 4.11f };
        Span<float> gains = stackalloc float[] { 1f, 0.42f, 0.18f };
        Span<float> phases = stackalloc float[3];
        for (int p = 0; p < 3; p++) phases[p] = (float)(rng.NextDouble() * Math.Tau);
        float k = 6.9f / (seconds * TransientSynth.SampleRate);
        float twoPiOverSr = MathF.Tau / TransientSynth.SampleRate;
        for (int i = 0; i < buffer.Length; i++)
        {
            float v = 0f;
            for (int p = 0; p < 3; p++)
            {
                v += gains[p] * MathF.Sin(phases[p]) * MathF.Exp(-k * i * (1f + p * 0.8f));
                phases[p] += twoPiOverSr * hz * partials[p];
            }
            if (noisiness > 0f) v += (float)(rng.NextDouble() * 2.0 - 1.0) * noisiness * MathF.Exp(-k * i * 8f);
            buffer[i] = v;
        }
    }

    private struct ResonatorBefore
    {
        private readonly float _a1, _a2, _gain;
        private float _z1, _z2;
        public ResonatorBefore(float hz, float q)
        {
            float r = MathF.Exp(-MathF.PI * MathF.Max(1f, hz / MathF.Max(0.2f, q)) / TransientSynth.SampleRate);
            float theta = MathF.Tau * Math.Clamp(hz, 20f, TransientSynth.SampleRate * 0.45f) / TransientSynth.SampleRate;
            _a1 = 2f * r * MathF.Cos(theta);
            _a2 = -r * r;
            _gain = (1f - r) * MathF.Sqrt(1f - 2f * r * MathF.Cos(2f * theta) + r * r);
            _z1 = _z2 = 0f;
        }
        public float Process(float x)
        {
            float y = _gain * x + _a1 * _z1 + _a2 * _z2;
            _z2 = _z1;
            _z1 = y;
            return y;
        }
    }
}
