using System.Runtime.InteropServices;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.Platform;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The threads that render ahead of the mixer treat subnormal numbers as zero (FlushToZero), and nothing
/// else does. A resonator ringing down to silence sat on them, a hundred times slower on an Intel processor
/// (docs/AUDIO_QUALITY_2026-10-06.md, row 14).
/// </summary>
public class FlushToZeroTests
{
    private readonly ITestOutputHelper _o;
    public FlushToZeroTests(ITestOutputHelper o) => _o = o;

    // Read from fields so the JIT cannot work the products out ahead.
    private static volatile float _tiny = 1e-30f, _small = 1e-10f, _scale = 16777216f;
    private static volatile float _subnormal = BitConverter.Int32BitsToSingle(1);   // 1.4e-45, the smallest
    private static double _dTiny = 1e-300, _dSmall = 1e-10;

    /// <summary>A float product under the smallest normal float (flush-to-zero's case), a subnormal input
    /// scaled up into the normal range (denormals-are-zero's case), and a double product under the smallest
    /// normal double.</summary>
    private readonly record struct Products(float Result, float Input, double Double)
    {
        public static Products Here() => new(_tiny * _small, _subnormal * _scale, Volatile.Read(ref _dTiny) * Volatile.Read(ref _dSmall));
        public bool AllZero => Result == 0f && Input == 0f && Double == 0.0;
        public bool NoneZero => Result != 0f && Input != 0f && Double != 0.0;
        public override string ToString() => $"1e-30f x 1e-10f = {Result:G3}, 1.4e-45f x 2^24 = {Input:G3}, 1e-300 x 1e-10 = {Double:G3}";
    }

    private sealed class Probe : IRenderedVoice
    {
        public Products? Seen;
        public readonly ManualResetEventSlim Done = new();
        public void Produce()
        {
            if (Done.IsSet) return;
            Seen = Products.Here();
            Done.Set();
        }
    }

    private static bool Supported =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    [Fact]
    public void A_subnormal_product_is_zero_on_a_render_pool_thread_and_not_on_another()
    {
        if (!Supported) { _o.WriteLine($"Nothing to flush with on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}."); return; }
        var probe = new Probe();
        using (new EngineRenderPool(() => new List<IRenderedVoice> { probe }))
            Assert.True(probe.Done.Wait(TimeSpan.FromSeconds(10)), "the render pool never asked the probe to produce");
        var pool = probe.Seen!.Value;
        var here = Products.Here();
        _o.WriteLine($"on a render pool thread: {pool}");
        _o.WriteLine($"on the test's thread:    {here}");
        Assert.True(pool.AllZero, $"a render pool thread kept a subnormal number: {pool}");
        Assert.True(here.NoneZero, $"the test's own thread flushed a subnormal number: {here}");
    }

    [Fact]
    public void A_borrowed_thread_flushes_while_it_renders_and_is_given_back_as_it_was()
    {
        if (!Supported) { _o.WriteLine($"Nothing to flush with on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}."); return; }
        Products before = default, during = default, after = default;
        bool onDuring = false, onAfter = true;
        var t = new Thread(() =>
        {
            before = Products.Here();
            during = FlushToZero.Run("the test", () => { onDuring = FlushToZero.IsOn; return Products.Here(); });
            after = Products.Here();
            onAfter = FlushToZero.IsOn;
        });
        t.Start();
        t.Join();
        _o.WriteLine($"before: {before}\nduring: {during}\nafter:  {after}");
        Assert.True(before.NoneZero, $"a new thread flushed before it was asked: {before}");
        Assert.True(during.AllZero && onDuring, $"a render on a borrowed thread kept a subnormal number: {during}");
        Assert.True(after.NoneZero && !onAfter, $"the borrowed thread was handed back flushed: {after}");
    }
}
