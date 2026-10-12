using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// One rank (docs/SPECIAL_CASES_AUDIT.md, P4): whoever chooses what gets a voice asks how loud it is at the ear
/// (VoiceManager.HeardLevel), with the numbers its voice is played with. There were four laws: cars by distance,
/// standing machines by loudness on numbers of their own, their places nearest first, and the budget by the
/// played gain, which carried the gain that gives a quiet render its headroom back.
/// </summary>
public class OneRankTests
{
    private readonly ITestOutputHelper _o;
    public OneRankTests(ITestOutputHelper o) => _o = o;

    private static readonly Vector3 Ear = new(0f, LocalPlayerState.StandingEyeHeight, 0f);

    private static void AddSource(ClientAudioHarness h, int id, string soundId, Vector3 at, Vector3? size = null)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        if (size is { } s) def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = s, IsSolid = false };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = soundId;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 50f;
        h.World.RegisterDefinition(def);
    }

    /// <summary>
    /// A voice that renders with more headroom than the shared one is played that much up to give it back
    /// (PhysicalVoiceState.HeadroomGain), and is no louder for it. The budget ranked on the played gain: a rain
    /// patch or a fire rendering 45 dB under full scale ranked 29 dB over a machine as loud.
    /// </summary>
    [Fact]
    public void A_renders_headroom_is_not_loudness()
    {
        var machine = new SpatialEmitter
        {
            Volume = 0.05f, MinDistance = 2f, Range = 300f, EarLevelDb = 70f, IsSynth = true, PhysicalKey = "machine:ac_window",
        };
        var fire = machine;
        fire.HeadroomGain = PhysicalVoiceState.HeadroomGain(45f);
        fire.Volume = machine.Volume * fire.HeadroomGain;
        _o.WriteLine($"45 dB of headroom is played {20 * MathF.Log10(fire.HeadroomGain):F1} dB up");
        Assert.InRange(20 * MathF.Log10(fire.HeadroomGain), 28.9f, 29.1f);
        foreach (float distance in new[] { 1f, 8f, 60f, 250f })
        {
            float plain = VoiceManager.Audibility(machine, distance, playing: false);
            float made = VoiceManager.Audibility(fire, distance, playing: false);
            Assert.True(plain > 0f, $"nothing at {distance} m");
            Assert.InRange(made / plain, 0.9999f, 1.0001f);
        }
    }

    public static IEnumerable<object[]> Kinds() => ClientAudioSelectionTests.Kinds().Where(k => !((string)k[0]).StartsWith("rail:", StringComparison.Ordinal));

    /// <summary>
    /// Every kind of standing model is ranked for a voice by the number the budget ranks its voice by: the same
    /// gain, reference distance, range, declared level and spectrum key (a shore's and a fire's carry their
    /// geometry), the headroom's return left out of both (S19: the ranking read the spectrum by another key and
    /// the budget counted the headroom). So nothing is picked on one set of numbers and played at another.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_standing_model_is_chosen_and_budgeted_by_one_number(string soundId)
    {
        var h = new ClientAudioHarness();
        h.Facade.Weather.Wind = WindWeather.Steady(6f, 270f, 0f);
        h.StandAt(Vector3.Zero);
        const int Id = 77;
        // Far enough that a tree or a fire is heard from its middle alone (its places share its gain out).
        AddSource(h, Id, soundId, new Vector3(0f, 1f, 120f), new Vector3(4f, 1f, 4f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(Id), 60), $"{soundId} was never voiced");
        h.Tick(2);
        var e = h.Mixer.Latest[Id];
        e.Occlusion = 0f;
        float chosenBy = h.Audio.HeardInRanking(Id);
        float budgetedBy = VoiceManager.Audibility(e, Vector3.Distance(e.Position, Ear), playing: false);
        _o.WriteLine($"{soundId} ({e.PhysicalKey}): chosen by {20 * MathF.Log10(chosenBy):F2} dB, budgeted by {20 * MathF.Log10(budgetedBy):F2} dB; "
                   + $"headroom's return {(e.HeadroomGain > 0f ? 20 * MathF.Log10(e.HeadroomGain) : 0f):F1} dB");
        Assert.True(chosenBy > 0f && float.IsFinite(chosenBy), $"{soundId} was not ranked");
        // A source still heard from its places from here (a creek's run is long) has shared its gain out among
        // them, which the one gain that keeps them together as loud as the whole puts back: within that.
        if (e.Spread > 0f) Assert.InRange(budgetedBy / chosenBy, 0.7f, 1.4f);
        else Assert.InRange(budgetedBy / chosenBy, 0.999f, 1.001f);
        // The return of its headroom is in its volume, and said, so that whoever ranks it can take it out.
        float headroom = PhysicalVoiceState.HeadroomGain(ModelKindTable.Headroom(soundId));
        if (headroom > 1.0001f) Assert.InRange(e.HeadroomGain / headroom, 0.9999f, 1.0001f);
    }
}
