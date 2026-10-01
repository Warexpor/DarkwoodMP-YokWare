using System.Collections;
using System.Reflection;
using DWMPHorde.Networking;
using Xunit;
using Xunit.Abstractions;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Every wire message with a <c>Serialize(NetWriter)</c> / static <c>Deserialize(NetReader)</c> pair must
/// read exactly what it writes: serialize → deserialize → serialize yields the same bytes, and the
/// reader ends with nothing left over. Covers all message structs by reflection, so a field added to
/// one side only, or a trailer read under a different condition than it is written, fails here.
/// </summary>
public class WireSymmetryTests
{
    private readonly ITestOutputHelper _out;

    public WireSymmetryTests(ITestOutputHelper output) => _out = output;

    private static IEnumerable<Type> MessageTypes()
    {
        Assembly asm = typeof(NetWriter).Assembly;
        foreach (Type t in asm.GetTypes())
        {
            if (t.Namespace == null || !t.Namespace.StartsWith("DWMPHorde", StringComparison.Ordinal))
                continue;
            if (t.IsGenericTypeDefinition || t.IsAbstract && !t.IsSealed)
                continue;
            if (FindSerialize(t) != null && FindDeserialize(t) != null)
                yield return t;
        }
    }

    private static MethodInfo FindSerialize(Type t)
        => t.GetMethod("Serialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(NetWriter) }, null);

    private static MethodInfo FindDeserialize(Type t)
    {
        MethodInfo m = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(NetReader) }, null);
        return m != null && m.ReturnType == t ? m : null;
    }

    public static IEnumerable<object[]> AllMessages()
        => MessageTypes().Select(t => new object[] { t.FullName });

    [Fact]
    public void ReflectionFindsTheMessageCatalog()
    {
        // Guard against the scan silently matching nothing (e.g. a renamed NetReader).
        Assert.True(MessageTypes().Count() >= 100, "found only " + MessageTypes().Count() + " message types");
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void SerializeDeserialize_IsSymmetric(string typeName)
    {
        Type t = typeof(NetWriter).Assembly.GetType(typeName)!;
        int checkedFills = 0;
        foreach (int seed in new[] { 0, 1, 2, 3 })
        {
            object msg = Fill(t, new Random(seed), seed, depth: 0);
            byte[] first;
            try { first = Write(t, msg); }
            catch (TargetInvocationException ex) when (seed > 0)
            {
                // A random fill can violate a sender-side precondition (count vs array length);
                // the zero fill and at least one random fill must still pass.
                _out.WriteLine($"seed {seed}: serialize rejected fill ({ex.InnerException?.GetType().Name})");
                continue;
            }

            var reader = new NetReader(first);
            object back = FindDeserialize(t)!.Invoke(null, new object[] { reader });
            Assert.True(reader.AvailableBytes == 0,
                $"{t.Name} seed {seed}: {reader.AvailableBytes} byte(s) left unread after Deserialize");
            byte[] second = Write(t, back!);
            Assert.True(first.SequenceEqual(second),
                $"{t.Name} seed {seed}: re-serialized bytes differ ({first.Length} vs {second.Length} bytes)");
            checkedFills++;
        }
        Assert.True(checkedFills >= 2, $"{t.Name}: only {checkedFills} fill(s) could be serialized");
    }

    private static byte[] Write(Type t, object msg)
    {
        var w = new NetWriter();
        FindSerialize(t)!.Invoke(msg, new object[] { w });
        return w.CopyData();
    }

    /// <summary>
    /// Deterministic fill: seed 0 leaves everything default (null arrays/strings), other seeds put
    /// small values everywhere and give arrays a length equal to the small integers, so count
    /// fields and array lengths usually agree.
    /// </summary>
    private static object Fill(Type t, Random rng, int seed, int depth)
    {
        object obj = Activator.CreateInstance(t)!;
        if (seed == 0 || depth > 3)
            return obj;
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.IsInitOnly || f.IsLiteral)
                continue;
            object v = ValueFor(f.FieldType, rng, seed, depth);
            if (v != null || !f.FieldType.IsValueType)
                f.SetValue(obj, v);
        }
        return obj;
    }

    private const int SmallCount = 2;

    private static object ValueFor(Type ft, Random rng, int seed, int depth)
    {
        if (ft == typeof(bool)) return seed % 2 == 1;
        if (ft == typeof(byte)) return (byte)SmallCount;
        if (ft == typeof(sbyte)) return (sbyte)SmallCount;
        if (ft == typeof(short)) return (short)SmallCount;
        if (ft == typeof(ushort)) return (ushort)SmallCount;
        if (ft == typeof(int)) return SmallCount;
        if (ft == typeof(uint)) return (uint)SmallCount;
        if (ft == typeof(long)) return (long)(rng.Next(1, 1000));
        if (ft == typeof(ulong)) return (ulong)rng.Next(1, 1000);
        if (ft == typeof(float)) return (float)Math.Round(rng.NextDouble() * 100, 2);
        if (ft == typeof(double)) return Math.Round(rng.NextDouble() * 100, 2);
        if (ft == typeof(string)) return "s" + rng.Next(0, 99);
        if (ft.IsEnum)
        {
            Array values = Enum.GetValues(ft);
            return values.Length > 0 ? values.GetValue(Math.Min(1, values.Length - 1)) : Activator.CreateInstance(ft);
        }
        if (ft.IsArray)
        {
            Type et = ft.GetElementType()!;
            Array arr = Array.CreateInstance(et, SmallCount);
            for (int i = 0; i < SmallCount; i++)
                arr.SetValue(ValueFor(et, rng, seed, depth + 1), i);
            return arr;
        }
        if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(ft)!;
            Type et = ft.GetGenericArguments()[0];
            for (int i = 0; i < SmallCount; i++)
                list.Add(ValueFor(et, rng, seed, depth + 1));
            return list;
        }
        if (ft.IsValueType && !ft.IsPrimitive && ft.Namespace != null && ft.Namespace.StartsWith("DWMPHorde", StringComparison.Ordinal))
            return Fill(ft, rng, seed, depth + 1);
        if (ft.IsClass && ft.Namespace != null && ft.Namespace.StartsWith("DWMPHorde", StringComparison.Ordinal)
            && ft.GetConstructor(Type.EmptyTypes) != null)
            return Fill(ft, rng, seed, depth + 1);
        return ft.IsValueType ? Activator.CreateInstance(ft) : null;
    }
}
