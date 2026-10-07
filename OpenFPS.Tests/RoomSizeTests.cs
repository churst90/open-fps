using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// <see cref="Enclosure"/> measures a room's surface area from inside, ∮(d²/cosθ)dω over its sphere of
/// rays, for a cube, a slab and a tube, from the middle and against a wall. The cube form it replaced
/// (S ≈ 13.5·MFP²) gave a 21 x 28 x 2.5 m car park 246 m² for 1,390 (docs/THE_CITY_BLOCK.md).
/// </summary>
public class RoomSizeTests
{
    public RoomSizeTests() => AcousticRegistry.Initialize();

    /// <summary>A hollow box of a given size, as six slabs of concrete a quarter of a metre thick.</summary>
    private static List<Enclosure.Solid> Box(float x, float y, float z, string material = "Concrete")
    {
        const float t = 0.25f;
        float hx = x / 2, hy = y / 2, hz = z / 2;
        return new List<Enclosure.Solid>
        {
            new(new Vector3(0, -hy - t / 2, 0), new Vector3(x + 2 * t, t, z + 2 * t), Quaternion.Identity, material),
            new(new Vector3(0, +hy + t / 2, 0), new Vector3(x + 2 * t, t, z + 2 * t), Quaternion.Identity, material),
            new(new Vector3(0, 0, -hz - t / 2), new Vector3(x + 2 * t, y, t), Quaternion.Identity, material),
            new(new Vector3(0, 0, +hz + t / 2), new Vector3(x + 2 * t, y, t), Quaternion.Identity, material),
            new(new Vector3(-hx - t / 2, 0, 0), new Vector3(t, y, z), Quaternion.Identity, material),
            new(new Vector3(+hx + t / 2, 0, 0), new Vector3(t, y, z), Quaternion.Identity, material),
        };
    }

    private static float TrueSurface(float x, float y, float z) => 2f * (x * y + y * z + z * x);

    [Theory]
    // shape                              x     y     z    where the listener stands
    [InlineData("a cube",                8f,   8f,   8f,  0f, 0f, 0f)]
    [InlineData("a cube, against a wall", 8f,  8f,   8f,  3.4f, 0f, 3.4f)]
    [InlineData("a car park (slab)",     21f,  2.5f, 28f, 0f, -0.15f, 0f)]
    [InlineData("a car park, at a wall", 21f,  2.5f, 28f, 9f, -0.15f, 12f)]
    [InlineData("a corridor (tube)",      2.2f, 2.7f, 39f, 0f, 0f, 0f)]
    [InlineData("a tunnel",              12f,  5.5f, 30f, 0f, -1f, 0f)]
    [InlineData("a hall",                30f, 12f,  30f, 0f, -4f, 0f)]
    public void TheSurfaceIsMeasuredNotAssumed(string shape, float x, float y, float z,
                                               float px, float py, float pz)
    {
        var survey = Enclosure.Look(new Vector3(px, py, pz), Box(x, y, z));

        float truth = TrueSurface(x, y, z);
        float measured = survey.SurfaceAreaSquareMetres;
        float cube = 13.5f * survey.MeanFreePathMetres * survey.MeanFreePathMetres;

        // The measure's stated limit: a long tube reads about a third low (192 rays cannot resolve a
        // corridor's far ends), 1.6 dB too wet, against the cube form's 7.6 dB in a car park.
        Assert.True(measured > truth * 0.6f && measured < truth * 1.45f,
            $"{shape}: measured {measured:F0} m^2 against {truth:F0} true");

        // For anything not a cube, the cube form was far off.
        if (MathF.Abs(x - y) > 1f || MathF.Abs(y - z) > 1f)
            Assert.True(cube < truth * 0.6f,
                $"{shape}: the cube form gives {cube:F0} m^2 against {truth:F0} true — it was supposed to be wrong here");
    }

    /// <summary>
    /// Your own footsteps in a bare concrete car park: the room equation gives the field at 1.6 m as
    /// (r/r_c)², r_c = sqrt(S·ā/(1−ā)/16π), about +5 dB (amplitude near 1.7); the cube form's 3.0
    /// clipped the master.
    /// </summary>
    [Fact]
    public void ACarParkAnswersAboutAsLoudlyAsItsOwnSurfaceAllows()
    {
        var survey = Enclosure.Look(new Vector3(0, -0.15f, 0), Box(21f, 2.5f, 28f));

        float measured = MathF.Sqrt(Enclosure.ReverberantToDirectPower(
            survey.Enclosure, survey.MeanFreePathMetres, survey.SurfaceAreaSquareMetres, 1.6f));
        float assumed = MathF.Sqrt(Enclosure.ReverberantToDirectPower(
            survey.Enclosure, survey.MeanFreePathMetres, 1.6f));

        Assert.InRange(measured, 0.6f, 2.2f);
        Assert.True(assumed > measured * 2f,
            $"the cube form gave {assumed:P0} where the measured surface gives {measured:P0}");
    }

    /// <summary>Open ground raises no reverberant field.</summary>
    [Fact]
    public void OpenGroundStaysDry()
    {
        var ground = new List<Enclosure.Solid>
        {
            new(new Vector3(0, -0.2f, 0), new Vector3(200f, 0.2f, 200f), Quaternion.Identity, "Asphalt"),
        };
        var survey = Enclosure.Look(new Vector3(0, 1.6f, 0), ground);

        float send = MathF.Sqrt(Enclosure.ReverberantToDirectPower(
            survey.Enclosure, survey.MeanFreePathMetres, survey.SurfaceAreaSquareMetres, 1.6f));
        Assert.True(send < 0.25f, $"a field sent {send:P0} of every sound to a reverb bus");
    }

    /// <summary>Two places in the same car park measure the same room; the old mean-free-path measure read
    /// 4.2 m in the middle and 3.0 m by a wall, 3 dB of reverberation from walking.</summary>
    [Fact]
    public void TheRoomIsTheSameSizeWhereverYouStandInIt()
    {
        var box = Box(21f, 2.5f, 28f);
        float middle = Enclosure.Look(new Vector3(0, -0.15f, 0), box).SurfaceAreaSquareMetres;
        float corner = Enclosure.Look(new Vector3(9f, -0.15f, 12f), box).SurfaceAreaSquareMetres;

        float db = 20f * MathF.Log10(middle / corner);
        Assert.True(MathF.Abs(db) < 4f,
            $"the same car park measured {middle:F0} m^2 from the middle and {corner:F0} from a corner ({db:+0.0;-0.0} dB of reverb)");
    }
}
