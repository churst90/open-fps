using System;
using System.Linq;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The car the engine is bolted into.
///
/// Two cars with the same engine do not sound the same, and most of the difference is a couple of
/// square metres of thin steel and a box of air. These hold the model to the two things that make it
/// worth having: that it puts its energy where a body does rather than where a subwoofer does, and
/// that the differences between one car and the next fall out of the numbers describing them rather
/// than being dialled in afterwards.
/// </summary>
public class VehicleBodyTests
{
    private const int SampleRate = 48000;

    /// <summary>
    /// A body colours, it does not boom — the regression test for the first thing measuring found.
    ///
    /// The first render put 97 per cent of a saloon's energy below 200 Hz, which is not a car. Two
    /// causes, both physical and both absent: the panel spans were the size of the pressings rather
    /// than the free spans between stiffeners, and the low modes were given the radiation efficiency
    /// of the high ones when a panel small against its wavelength barely radiates at all.
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
    /// Deadening is what a manufacturer pays for, and it is audible.
    ///
    /// A stripped shell and a trimmed saloon differ here by a factor of four in loss factor, and
    /// that is the whole of why one sounds expensive. Nothing about this was chosen for the sound:
    /// the loss factor is a property of a panel with bitumen pads bonded to it.
    /// </summary>
    [Fact]
    public void AStrippedShellRingsLongerThanADeadenedOne()
    {
        float stripped = VehicleBody.DecayMs(VehicleBody.RaceSaloon.ImpulseResponse(SampleRate, 0.5f), SampleRate);
        float trimmed = VehicleBody.DecayMs(VehicleBody.Saloon.ImpulseResponse(SampleRate, 0.5f), SampleRate);

        Assert.True(stripped > trimmed * 2f,
            $"the race shell rings {stripped:F0} ms against the saloon's {trimmed:F0}");
    }

    /// <summary>Small, thick, heavily stiffened panels ring higher than big undeadened flat ones.
    /// A supercar is brighter than a van, and neither number was chosen to make that true.</summary>
    [Fact]
    public void SmallerStifferPanelsSoundHigher()
    {
        var supercar = VehicleBody.Bands(VehicleBody.Supercar.ImpulseResponse(SampleRate), SampleRate);
        var van = VehicleBody.Bands(VehicleBody.Van.ImpulseResponse(SampleRate), SampleRate);

        Assert.True(supercar.High > van.High,
            $"supercar has {supercar.High:P0} above 1.5 kHz against the van's {van.High:P0}");
    }

    /// <summary>
    /// A cabin reaches the street only to the extent it is let out, and at a trimmed saloon's seals
    /// that is next to nothing — which is the claim CabinLeak exists to make.
    ///
    /// This test used to say an open-wheeler has less low end than a saloon, and it stopped being
    /// true the moment the modes acquired their zeros at DC: with the leak at 0.15 a saloon's cabin
    /// contributes about as much to the street as the open-wheeler's absent one does, which is the
    /// model saying what it was built to say. The thing worth guarding is the MECHANISM — that the
    /// leak is what decides it — because that is the number interior audio will turn up.
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

    /// <summary>A stripped shell with no trim and no glass lets its cabin out, and booms where a
    /// trimmed car does not. Same model, different car, nothing special-cased.</summary>
    [Fact]
    public void AStrippedShellBoomsWhereATrimmedOneDoesNot()
    {
        var race = VehicleBody.Bands(VehicleBody.RaceSaloon.ImpulseResponse(SampleRate), SampleRate);
        var road = VehicleBody.Bands(VehicleBody.Saloon.ImpulseResponse(SampleRate), SampleRate);

        Assert.True(race.Low > road.Low * 3f,
            $"the race shell has {race.Low:P1} below 200 Hz against the saloon's {road.Low:P1}");
    }

    /// <summary>
    /// A mode is a RESONANCE, so it passes neither DC nor Nyquist.
    ///
    /// All-pole resonators have real gain a long way below their own note, which does not matter for
    /// a broadband drive and matters enormously for an exhaust: the pressure inside a muffler is
    /// dominated by the firing fundamental at 40-200 Hz, well under the lowest panel mode, and a
    /// small off-resonance gain on an enormous drive is a lot of output. Measured on the rev bench,
    /// the can was adding 5-8 dB BELOW every mode it has.
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
    /// Each resonator passes its own note at the gain its weight claims, which is what makes a
    /// weight mean anything. Without the normalisation a mode's real gain would be whatever its pole
    /// placement happened to give it — and that varies by tens of decibels across the range.
    /// </summary>
    [Fact]
    public void AModeIsNormalisedToUnityAtItsOwnNote()
    {
        var body = OneMode();
        float hz = body.Modes()[0].Hz;
        Assert.InRange(SteadyStateGain(body, hz), 0.7f, 1.4f);
    }

    /// <summary>
    /// The weights are normalised against the STRONGEST mode, not against their sum — and a body
    /// with more resonances is not therefore a quieter one at each of them.
    ///
    /// Dividing by the sum was the first thing written here and it made the whole layer inaudible:
    /// sixteen modes summing to one leaves the loudest around a tenth, and a tenth of a coupling of
    /// 0.25 is thirty decibels down. It measured as working and was heard as nothing.
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

    /// <summary>...and rejects everything else. A resonator that passed everything would be a gain
    /// stage, not a body.</summary>
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

    /// <summary>The response is finite, and it dies. A resonator bank with a pole outside the unit
    /// circle is an oscillator, and it would not announce itself as one — it would arrive as a car
    /// that got steadily louder until the mixer clipped.</summary>
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

    /// <summary>The mode budget is a real budget and it is honoured, because it is paid per sample
    /// per voice on a track with thirty cars on it.</summary>
    [Fact]
    public void TheModeBudgetIsHonoured()
    {
        var body = VehicleBody.Saloon with { MaxModes = 5 };
        Assert.Equal(5, body.Modes().Count);
        Assert.Equal(5, new BodyResonator(body, SampleRate).ModeCount);
    }

    /// <summary>
    /// Half a plate's modes are not driven at all by a pressure spread over its face: their two
    /// halves move oppositely and the net force on them is zero. That is a selection rule rather
    /// than a taste curve, and it is why the mode list is far shorter than the mode count.
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

    /// <summary>
    /// A body with exactly ONE resonance, for testing the bank itself: one panel, one mode kept.
    ///
    /// Which note it lands on is the plate law's business, not the test's, so the test asks the body
    /// what it built rather than asserting a frequency it would have had to work out by hand.
    /// </summary>
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
