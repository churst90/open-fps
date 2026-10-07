using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// The body the engine is bolted into: its energy goes where a body's does, not a subwoofer's, and the
/// differences between cars fall out of the numbers describing them, not out of tuning.
/// </summary>
public class VehicleBodyTests
{
    private const int SampleRate = 48000;

    /// <summary>
    /// A body colours, it does not boom. The first render put 97 % of a saloon's energy below 200 Hz:
    /// panel spans were the pressings' size, not the free spans between stiffeners, and low modes had the
    /// high ones' radiation efficiency, though a panel small against its wavelength barely radiates.
    /// </summary>
    [Theory]
    [InlineData("saloon")]
    [InlineData("van")]
    [InlineData("supercar")]
    [InlineData("racesaloon")]
    [InlineData("openwheeler")]
    public void ABodyColoursRatherThanBooms(string name)
    {
        var bands = VehicleBody.Bands(Body(name).ImpulseResponse(SampleRate), SampleRate);

        Assert.True(bands.Low < 0.6f, $"{name} put {bands.Low:P0} of its energy below 200 Hz");
        Assert.True(bands.Mid > 0.25f, $"{name} has only {bands.Mid:P0} between 200 Hz and 1.5 kHz");
    }

    /// <summary>
    /// Deadening is audible: a stripped shell and a trimmed saloon differ by four times in loss factor,
    /// a property of a panel with bitumen pads bonded to it, not a choice made for the sound.
    /// </summary>
    [Fact]
    public void AStrippedShellRingsLongerThanADeadenedOne()
    {
        float stripped = VehicleBody.DecayMs(VehicleBody.RaceSaloon.ImpulseResponse(SampleRate, 0.5f), SampleRate);
        float trimmed = VehicleBody.DecayMs(VehicleBody.Saloon.ImpulseResponse(SampleRate, 0.5f), SampleRate);

        Assert.True(stripped > trimmed * 2f,
            $"the race shell rings {stripped:F0} ms against the saloon's {trimmed:F0}");
    }

    /// <summary>Small, thick, stiffened panels ring higher than big flat ones: a supercar is brighter than
    /// a van, and neither number was chosen to make it so.</summary>
    [Fact]
    public void SmallerStifferPanelsSoundHigher()
    {
        var supercar = VehicleBody.Bands(VehicleBody.Supercar.ImpulseResponse(SampleRate), SampleRate);
        var van = VehicleBody.Bands(VehicleBody.Van.ImpulseResponse(SampleRate), SampleRate);

        Assert.True(supercar.High > van.High,
            $"supercar has {supercar.High:P0} above 1.5 kHz against the van's {van.High:P0}");
    }

    /// <summary>
    /// A cabin reaches the street only as far as CabinLeak lets it out, next to nothing at a trimmed
    /// saloon's seals. Guards the mechanism, the leak deciding it, since interior audio will turn it up
    /// (docs/TEST_NOTES.md, "The cabin leak test").
    /// </summary>
    [Fact]
    public void ACabinReachesTheStreetOnlyWhenItIsLetOut()
    {
        var sealedUp = VehicleBody.Bands(VehicleBody.Saloon.ImpulseResponse(SampleRate), SampleRate);
        var wideOpen = VehicleBody.Bands(
            (VehicleBody.Saloon with { CabinLeak = 1f }).ImpulseResponse(SampleRate), SampleRate);

        Assert.True(wideOpen.Low > sealedUp.Low * 3f,
            $"sealed {sealedUp.Low:P1} against open {wideOpen.Low:P1} below 200 Hz");
    }

    /// <summary>A stripped shell with no trim and no glass lets its cabin out and booms where a trimmed
    /// car does not, from the same model.</summary>
    [Fact]
    public void AStrippedShellBoomsWhereATrimmedOneDoesNot()
    {
        var race = VehicleBody.Bands(VehicleBody.RaceSaloon.ImpulseResponse(SampleRate), SampleRate);
        var road = VehicleBody.Bands(VehicleBody.Saloon.ImpulseResponse(SampleRate), SampleRate);

        Assert.True(race.Low > road.Low * 3f,
            $"the race shell has {race.Low:P1} below 200 Hz against the saloon's {road.Low:P1}");
    }

    /// <summary>
    /// A mode is a resonance, so it passes neither DC nor Nyquist. An all-pole resonator's gain far
    /// below its note, on a muffler's firing fundamental at 40-200 Hz, added 5-8 dB below every mode the
    /// can has (measured on the rev bench).
    /// </summary>
    [Fact]
    public void APanelDoesNotRespondBelowItsLowestMode()
    {
        var body = OneMode();
        float hz = body.Modes()[0].Hz;

        float onNote = SteadyStateGain(body, hz);
        float wayBelow = SteadyStateGain(body, hz / 8f);

        Assert.True(wayBelow < onNote * 0.05f,
            $"at an eighth of its note the panel still gave {wayBelow:F4} against {onNote:F4} on it");
    }

    // ── The resonator bank ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each resonator passes its own note at the gain its weight claims; unnormalised, its pole placement
    /// would decide it, tens of dB apart across the range.
    /// </summary>
    [Fact]
    public void AModeIsNormalisedToUnityAtItsOwnNote()
    {
        var body = OneMode();
        float hz = body.Modes()[0].Hz;
        Assert.InRange(SteadyStateGain(body, hz), 0.7f, 1.4f);
    }

    /// <summary>
    /// The weights are normalised against the strongest mode, not their sum, so more resonances are not
    /// quieter ones. By the sum the layer was inaudible (docs/ENGINE_SYNTHESIS.md, end of the body section).
    /// </summary>
    [Fact]
    public void AddingModesDoesNotQuietenTheOnesAlreadyThere()
    {
        var few = VehicleBody.Saloon with { MaxModes = 3, Coupling = 1f };
        var many = VehicleBody.Saloon with { MaxModes = 16, Coupling = 1f };
        float hz = few.Modes()[0].Hz;

        float withFew = SteadyStateGain(few, hz);
        float withMany = SteadyStateGain(many, hz);

        Assert.True(withMany > withFew * 0.6f,
            $"the strongest mode gave {withFew:F3} among three and {withMany:F3} among sixteen");
    }

    /// <summary>A resonator rejects everything off its note; one that passed everything would be a gain stage.</summary>
    [Fact]
    public void AModeRejectsNotesThatAreNotItsOwn()
    {
        var body = OneMode();
        float hz = body.Modes()[0].Hz;
        float onNote = SteadyStateGain(body, hz);
        float offNote = SteadyStateGain(body, hz * 4f);

        Assert.True(onNote > offNote * 8f, $"on-note {onNote:F3} against off-note {offNote:F3}");
    }

    [Fact]
    public void ABodyThatIsNotCoupledIsSilent()
    {
        var bank = new BodyResonator(VehicleBody.None, SampleRate);
        for (int i = 0; i < 100; i++) Assert.Equal(0f, bank.Process(1f));
    }

    /// <summary>The response is finite and dies. A pole outside the unit circle would be heard as a car
    /// growing louder until the mixer clipped.</summary>
    [Theory]
    [InlineData("saloon")]
    [InlineData("van")]
    [InlineData("supercar")]
    [InlineData("racesaloon")]
    [InlineData("openwheeler")]
    public void TheResponseIsFiniteAndDecays(string name)
    {
        float[] ir = Body(name).ImpulseResponse(SampleRate, 1.0f);

        Assert.All(ir, v => Assert.True(float.IsFinite(v), "the response produced a non-finite sample"));

        float first = ir.Take(ir.Length / 2).Sum(v => v * v);
        float second = ir.Skip(ir.Length / 2).Sum(v => v * v);
        Assert.True(second < first * 0.5f, $"{name} had {second:E2} of energy in its second half against {first:E2}");
    }

    /// <summary>The mode budget is honoured: it is paid per sample per voice, thirty cars on a track.</summary>
    [Fact]
    public void TheModeBudgetIsHonoured()
    {
        var body = VehicleBody.Saloon with { MaxModes = 5 };
        Assert.Equal(5, body.Modes().Count);
        Assert.Equal(5, new BodyResonator(body, SampleRate).ModeCount);
    }

    /// <summary>
    /// A pressure spread over a plate drives none of the modes whose halves move oppositely (no net
    /// force): a selection rule, and why the mode list is far shorter than the mode count.
    /// </summary>
    [Fact]
    public void OnlyOddOddPlateModesAreDriven()
    {
        var modes = PanelAcoustics.Modes(AcousticRegistry.GetProperties("Metal"), 0.3f, 0.2f, 0.0008f);

        Assert.NotEmpty(modes);
        Assert.All(modes, m =>
        {
            Assert.True(m.M % 2 == 1, $"mode {m.M},{m.N} has an even m and should not be driven");
            Assert.True(m.N % 2 == 1, $"mode {m.M},{m.N} has an even n and should not be driven");
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static VehicleBody Body(string name) => name switch
    {
        "saloon" => VehicleBody.Saloon,
        "van" => VehicleBody.Van,
        "supercar" => VehicleBody.Supercar,
        "racesaloon" => VehicleBody.RaceSaloon,
        "openwheeler" => VehicleBody.OpenWheeler,
        _ => throw new ArgumentException(name),
    };

    /// <summary>A body with one resonance, one panel and one mode, for testing the bank; tests ask it
    /// which note it built rather than working the plate law out by hand.</summary>
    private static VehicleBody OneMode() => new()
    {
        PanelSpansM = new[] { 0.25f },
        CabinLengthM = 0f, CabinWidthM = 0f, CabinHeightM = 0f,
        MaxModes = 1,
        Coupling = 1f,
    };

    /// <summary>Peak output for a unit sine in, once the bank has settled.</summary>
    private static float SteadyStateGain(VehicleBody body, float hz)
    {
        var bank = new BodyResonator(body, SampleRate);
        float peak = 0f;
        int n = SampleRate / 2;
        for (int i = 0; i < n; i++)
        {
            float y = bank.Process(MathF.Sin(2f * MathF.PI * hz * i / SampleRate));
            if (i > n / 2) peak = MathF.Max(peak, MathF.Abs(y));
        }
        return peak;
    }
}
