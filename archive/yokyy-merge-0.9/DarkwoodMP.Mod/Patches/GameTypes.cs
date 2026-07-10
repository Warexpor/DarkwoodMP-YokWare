using System;
using System.Collections.Generic;
using System.Reflection;

namespace DarkwoodMP.Patches;

/// <summary>
/// Utility for resolving game types at runtime via reflection.
/// </summary>
public static class GameTypes
{
    private static readonly Dictionary<string, Type> _typeCache = new();

    public static Type? GetType(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached))
            return cached;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null)
            {
                _typeCache[fullName] = t;
                return t;
            }
        }
        return null;
    }
}
