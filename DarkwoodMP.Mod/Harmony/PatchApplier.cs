using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using HarmonyLib;

namespace DWMPHorde.Harmony
{
    /// <summary>One patch class that failed to apply at startup.</summary>
    public sealed class PatchFailure
    {
        public string TypeName { get; }
        public bool Optional { get; }
        public string Error { get; }

        public PatchFailure(string typeName, bool optional, string error)
        {
            TypeName = typeName;
            Optional = optional;
            Error = error;
        }

        public override string ToString() =>
            (Optional ? "[optional] " : "[critical] ") + TypeName + ": " + Error;
    }

    /// <summary>
    /// Applies every Harmony patch class of an assembly one class at a time so a bad
    /// target (renamed game method, signature drift) cannot leave all later classes
    /// unpatched the way a single PatchAll throw does.
    /// </summary>
    internal static class PatchApplier
    {
        public static List<PatchFailure> ApplyAll(HarmonyLib.Harmony harmony, Assembly assembly, out int applied)
        {
            var failures = new List<PatchFailure>();
            applied = 0;
            // Same walk as Harmony.PatchAll(assembly), one class per try.
            foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (type == null)
                    continue;
                bool patchClass = IsPatchClass(type);
                bool optional = patchClass && type.IsDefined(typeof(OptionalPatchAttribute), false);
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    if (patchClass)
                        applied++;
                }
                catch (Exception ex)
                {
                    Exception root = ex;
                    while (root.InnerException != null)
                        root = root.InnerException;
                    var failure = new PatchFailure(type.FullName, optional, root.GetType().Name + ": " + root.Message);
                    failures.Add(failure);
                    if (optional)
                        ModLog.Warn(LogCat.Core, "Harmony patch failed (optional): " + failure.TypeName + " — " + failure.Error);
                    else
                        ModLog.Error(LogCat.Core, "Harmony patch FAILED (critical): " + failure.TypeName, ex);
                }
            }
            return failures;
        }

        private static bool IsPatchClass(Type type)
        {
            if (!type.IsClass)
                return false;
            try
            {
                return type.IsDefined(typeof(HarmonyAttribute), true);
            }
            catch
            {
                return false;
            }
        }
    }
}
