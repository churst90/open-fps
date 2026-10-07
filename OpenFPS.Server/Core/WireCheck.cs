using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MemoryPack;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// Walks a client message's bytes, field by field, before MemoryPack reads it, and refuses any string
/// whose length does not fit in what arrived.
/// </summary>
// MemoryPack 1.21.4 (and its main branch, 2026-10) reads a UTF-8 string by asking for ~header + 4 bytes; a
// header of int.MinValue..int.MinValue+3 overflows that to a negative size, the bounds check passes, and it
// reads two gigabytes past the buffer: an AccessViolationException, which no catch can stop. Five bytes in
// a LoginRequest took the whole server down before login. The layout here is MemoryPack's for these types:
// a union tag byte, a member count byte (255 null), then each field in declaration order.
public static class WireCheck
{
    private static readonly ConcurrentDictionary<Type, Type[]> Layouts = new();
    private static readonly ConcurrentDictionary<Type, bool> Stringy = new();
    private static readonly Dictionary<byte, Type> ByTag = typeof(IMessage)
        .GetCustomAttributes(typeof(MemoryPackUnionAttribute), false).Cast<MemoryPackUnionAttribute>()
        .Where(a => a.Tag < 250).ToDictionary(a => (byte)a.Tag, a => a.Type);

    /// <summary>Whether MemoryPack may read these bytes without reading past them. False is a malformed message.</summary>
    public static bool IsSafe(byte[] bytes)
    {
        if (bytes.Length == 0 || !ByTag.TryGetValue(bytes[0], out var type)) return false;
        if (!HasStrings(type)) return true;
        int pos = 1;
        return Object(type, bytes, ref pos, depth: 0);
    }

    /// <summary>How many bytes the walk took, or -1 if it refused: for the tests, which check the layout is MemoryPack's.</summary>
    internal static int Walked(byte[] bytes)
    {
        if (bytes.Length == 0 || !ByTag.TryGetValue(bytes[0], out var type)) return -1;
        int pos = 1;
        return Object(type, bytes, ref pos, depth: 0) ? pos : -1;
    }

    /// <summary>The fields MemoryPack writes for a type, in order: its public instance fields, as declared.</summary>
    internal static Type[] Layout(Type type) => Layouts.GetOrAdd(type, t =>
        t.GetFields(BindingFlags.Public | BindingFlags.Instance)
         .Where(f => !f.IsInitOnly && f.GetCustomAttribute<MemoryPackIgnoreAttribute>() == null)
         .OrderBy(f => f.MetadataToken).Select(f => f.FieldType).ToArray());

    private static bool HasStrings(Type type) => Stringy.GetOrAdd(type, t =>
        HasProperties(t) || Layout(t).Any(f => f == typeof(string) || f == typeof(string[]) || (f.IsClass && f != typeof(byte[]))));

    private static readonly ConcurrentDictionary<Type, bool> Propertied = new();

    private static bool HasProperties(Type type) => Propertied.GetOrAdd(type, t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.CanWrite && p.GetCustomAttribute<MemoryPackIgnoreAttribute>() == null));

    private static bool Object(Type type, byte[] b, ref int pos, int depth)
    {
        if (depth > 4 || pos >= b.Length) return false;
        byte count = b[pos++];
        if (count == 255) return true;
        // Properties would interleave with fields in an order reflection cannot see: refuse rather than guess.
        if (HasProperties(type)) return false;
        var fields = Layout(type);
        for (int i = 0; i < Math.Min(count, fields.Length); i++)
            if (!Field(fields[i], b, ref pos, depth)) return false;
        return true;
    }

    private static bool Field(Type t, byte[] b, ref int pos, int depth)
    {
        if (t == typeof(string)) return String(b, ref pos);
        if (t == typeof(string[]))
        {
            if (!Header(b, ref pos, out int n)) return false;
            if (n == -1) return true;
            if (n < 0 || n > b.Length - pos) return false;
            for (int i = 0; i < n; i++) if (!String(b, ref pos)) return false;
            return true;
        }
        if (t.IsArray && t.GetElementType() is { } e && IsUnmanaged(e))
        {
            if (!Header(b, ref pos, out int n)) return false;
            if (n == -1) return true;
            long bytes = (long)n * SizeOf(e);
            if (n < 0 || bytes > b.Length - pos) return false;
            pos += (int)bytes;
            return true;
        }
        if (IsUnmanaged(t)) return Skip(b, ref pos, SizeOf(t));
        if (t.IsClass && t.GetCustomAttribute<MemoryPackableAttribute>() != null) return Object(t, b, ref pos, depth + 1);
        return false;
    }

    private static bool String(byte[] b, ref int pos)
    {
        if (!Header(b, ref pos, out int h)) return false;
        if (h == -1 || h == 0) return true;
        long need = h > 0 ? 2L * h : 4L + ~(long)h;
        if (need > b.Length - pos) return false;
        pos += (int)need;
        return true;
    }

    private static bool Header(byte[] b, ref int pos, out int value)
    {
        value = 0;
        if (b.Length - pos < 4) return false;
        value = BitConverter.ToInt32(b, pos);
        pos += 4;
        return true;
    }

    private static bool Skip(byte[] b, ref int pos, int size)
    {
        if (b.Length - pos < size) return false;
        pos += size;
        return true;
    }

    private static readonly ConcurrentDictionary<Type, bool> Unmanaged = new();

    private static bool IsUnmanaged(Type type) => Unmanaged.GetOrAdd(type, t =>
        t.IsPrimitive || t.IsEnum || (t.IsValueType && !t.IsGenericTypeDefinition
           && (bool)typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod(nameof(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences))!
               .MakeGenericMethod(t).Invoke(null, null)! == false));

    private static readonly ConcurrentDictionary<Type, int> Sizes = new();

    private static int SizeOf(Type t) => Sizes.GetOrAdd(t, x =>
        (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod(nameof(System.Runtime.CompilerServices.Unsafe.SizeOf))!
            .MakeGenericMethod(x).Invoke(null, null)!);
}
