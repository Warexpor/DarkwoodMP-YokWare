using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Hooks vanilla outside-location pad placement so every peer spawns a location at the
    /// host-assigned slot (see <see cref="OutsidePadSlots"/>).
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), "spawnLocation")]
    public static class OutsidePadSlotSpawnPatch
    {
        // spawnLocation is an iterator: the original enumerator has not run yet when the
        // postfix sees it, so it can be wrapped without changing what vanilla does.
        private static void Postfix(OutsideLocations __instance, string locationName,
            ref IEnumerator __result)
        {
            if (__instance == null || __result == null)
                return;
            __result = OutsidePadSlots.WrapSpawn(__instance, locationName, __result);
        }
    }

    /// <summary>
    /// <c>LocationMarker.spawnLocation</c> rotates the pad by <c>Core.getRandomHalfRotation()</c>,
    /// which differs per machine. Route the value through the host-assigned yaw.
    /// </summary>
    [HarmonyPatch]
    public static class OutsidePadSlotYawPatch
    {
        private static bool Prepare() => TargetMethod() != null;

        private static MethodBase TargetMethod()
        {
            MethodInfo outer = AccessTools.Method(typeof(LocationMarker), "spawnLocation");
            return outer != null ? AccessTools.EnumeratorMoveNext(outer) : null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var randomYaw = AccessTools.Method(typeof(Core), "getRandomHalfRotation");
            var thisField = AccessTools.Field(original.DeclaringType, "<>4__this");
            var adjust = AccessTools.Method(typeof(OutsidePadSlots), nameof(OutsidePadSlots.AdjustMarkerYaw));

            var list = new List<CodeInstruction>(instructions);
            if (randomYaw == null || thisField == null || adjust == null)
            {
                ModLog.Error(LogCat.World,
                    "[PadSlot] LocationMarker.spawnLocation yaw hook not applied (missing member)");
                return list;
            }

            int patched = 0;
            var result = new List<CodeInstruction>(list.Count + 4);
            foreach (var ins in list)
            {
                result.Add(ins);
                if (ins.Calls(randomYaw))
                {
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Ldfld, thisField));
                    result.Add(new CodeInstruction(OpCodes.Call, adjust));
                    patched++;
                }
            }
            if (patched != 1)
            {
                ModLog.Error(LogCat.World,
                    "[PadSlot] LocationMarker.spawnLocation yaw hook expected 1 call site, found " + patched);
            }
            return result;
        }
    }
}
