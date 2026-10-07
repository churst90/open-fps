namespace OpenFPS.Tests;

/// <summary>
/// A bound on how long a test waits for work it handed to another thread, such as a login's password
/// check. An unbounded wait hung a CI shard for 30 minutes with nothing said (2026-10-07: the check sat
/// in the thread pool's queue behind door renders); this fails the test instead and says how busy the
/// pool was.
/// </summary>
internal static class TestDeadline
{
    /// <summary>A login or registration: bcrypt at work factor 4 and a few SQLite rows, milliseconds.</summary>
    public static readonly TimeSpan Login = TimeSpan.FromSeconds(60);

    public static async Task Within(this Task task, TimeSpan limit, string what)
    {
        try { await task.WaitAsync(limit); }
        catch (TimeoutException) { throw Late(limit, what); }
    }

    private static TimeoutException Late(TimeSpan limit, string what) => new(
        $"{what} did not finish in {limit.TotalSeconds:F0} s. Thread pool: {ThreadPool.ThreadCount} threads, "
        + $"{ThreadPool.PendingWorkItemCount} work items waiting.");
}
