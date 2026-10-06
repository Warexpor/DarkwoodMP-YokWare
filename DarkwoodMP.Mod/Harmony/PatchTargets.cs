using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using HarmonyLib;

namespace DWMPHorde.Harmony
{
    /// <summary>
    /// Multi-target patch helper. Stacked class-level [HarmonyPatch(typeof(X), "m")] attributes
    /// MERGE into one target (last wins), so a class that must patch several methods uses
    /// [HarmonyPatch] + TargetMethods() built from these helpers instead.
    /// </summary>
    internal static class PatchTargets
    {
        /// <summary>
        /// Resolves <paramref name="name"/> on <paramref name="type"/>. Pass <paramref name="args"/>
        /// whenever the method has overloads. Missing targets log an error and are skipped so one
        /// vanilla drift cannot silently take the rest of the group with it.
        /// </summary>
        internal static MethodBase Find(Type type, string name, Type[] args = null)
        {
            MethodBase m = AccessTools.Method(type, name, args);
            if (m == null)
                ModLog.Error(LogCat.Core, "Patch target missing: " + type.FullName + "." + name);
            return m;
        }

        /// <summary>Adds each resolved target; unresolved ones are skipped (already logged).</summary>
        internal static IEnumerable<MethodBase> Resolve(params MethodBase[] targets)
        {
            var list = new List<MethodBase>(targets.Length);
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] != null)
                    list.Add(targets[i]);
            return list;
        }
    }
}
