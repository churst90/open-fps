using System.Threading;

namespace OpenFPS.Common;

/// <summary>
/// Counts the times anything has moved a mover (a door leaf) on the server. The triangle world places a
/// leaf by an instance that follows its transform; whoever holds one compares this with the count it last
/// placed its leaves at and places them again before answering, so a leaf swung by DoorSystem is where it
/// is for the very next query, not only after the tick's sync.
/// </summary>
public static class MoverPoses
{
    private static long _version;

    public static long Version => Interlocked.Read(ref _version);

    /// <summary>A mover's pose was written.</summary>
    public static void Moved() => Interlocked.Increment(ref _version);
}
