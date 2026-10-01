using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using HarmonyLib;

namespace DWMPHorde.Harmony
{
    /// <summary>
    /// Prefix return false on an IEnumerator target without setting __result yields
    /// StartCoroutine(null) → Unity "Value cannot be null. Parameter name: routine"
    /// (same class as the 0.8.37 HelpMessage / GameEvent.fire suppress NRE).
    /// Always assign <see cref="Empty"/> when skipping a coroutine method.
    /// Also resolves compiler-generated coroutine state machines for MoveNext patches.
    /// </summary>
    internal static class HarmonyCoroutineUtil
    {
        // Reflection results only (types never change in a process); TargetMethod is
        // called from both Prepare and the patcher, so cache to resolve and log once.
        private static readonly Dictionary<string, MethodBase> _moveNextCache =
            new Dictionary<string, MethodBase>(); // process-scoped

        internal static IEnumerator Empty()
        {
            yield break;
        }

        /// <summary>
        /// MoveNext of the iterator state machine behind <paramref name="owner"/>.<paramref name="method"/>.
        /// Uses <see cref="AccessTools.EnumeratorMoveNext"/>; only if that fails falls back to the
        /// first IEnumerator nested type whose name contains the method name. Logs which path won.
        /// </summary>
        internal static MethodBase FindMoveNext(Type owner, string method, Type[] args = null)
        {
            if (owner == null || string.IsNullOrEmpty(method))
                return null;
            string key = owner.FullName + "." + method + "/" + (args == null ? -1 : args.Length);
            if (_moveNextCache.TryGetValue(key, out MethodBase cached))
                return cached;

            MethodBase result = null;
            string via = null;
            try
            {
                MethodInfo outer = AccessTools.Method(owner, method, args);
                if (outer != null)
                {
                    result = AccessTools.EnumeratorMoveNext(outer);
                    if (result != null)
                        via = "EnumeratorMoveNext";
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Core, "Coroutine lookup " + key + " EnumeratorMoveNext failed: " + ex.Message);
            }

            if (result == null)
            {
                result = ScanNestedMoveNext(owner, method);
                if (result != null)
                    via = "nested-type scan (fallback)";
            }

            if (result != null)
                ModLog.Info(LogCat.Core, "Coroutine target " + owner.Name + "." + method
                    + " → " + result.DeclaringType?.Name + ".MoveNext via " + via);
            else
                ModLog.Error(LogCat.Core, "Coroutine target " + owner.Name + "." + method + " MoveNext not found");

            _moveNextCache[key] = result;
            return result;
        }

        private static MethodBase ScanNestedMoveNext(Type owner, string method)
        {
            Type[] nested = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nested.Length; i++)
            {
                Type t = nested[i];
                if (t.Name.IndexOf(method, StringComparison.Ordinal) < 0)
                    continue;
                if (!typeof(IEnumerator).IsAssignableFrom(t))
                    continue;
                MethodInfo m = AccessTools.Method(t, "MoveNext");
                if (m != null)
                    return m;
            }
            return null;
        }

        /// <summary>
        /// The state machine's captured <c>this</c> (<c>&lt;&gt;4__this</c>); falls back to the
        /// first instance field of <paramref name="ownerType"/> if the compiler named it differently.
        /// </summary>
        internal static FieldInfo FindThisField(Type stateMachine, Type ownerType)
        {
            if (stateMachine == null || ownerType == null)
                return null;
            FieldInfo f = AccessTools.Field(stateMachine, "<>4__this");
            if (f != null && ownerType.IsAssignableFrom(f.FieldType))
                return f;
            FieldInfo[] fields = stateMachine.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (ownerType.IsAssignableFrom(fields[i].FieldType))
                    return fields[i];
            }
            return null;
        }
    }
}
