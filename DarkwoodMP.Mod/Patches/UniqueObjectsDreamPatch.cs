using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// UniqueObjects.addObject keeps the first registrant only. Overworld bunker
    /// registers door_bunker_* before the dream pad clone exists, so leave-door /
    /// setActive GameEvents that resolve via targetUniqueObjects hit the wrong world.
    /// While dreaming, getObject prefers the dream-pad instance.
    /// </summary>
    [HarmonyPatch(typeof(UniqueObjects), nameof(UniqueObjects.getObject))]
    public static class UniqueObjectsDreamGetPatch
    {
        private static void Postfix(UniqueObjects __instance, string type, ref GameObject __result)
        {
            if (string.IsNullOrEmpty(type))
                return;
            if (!DreamSyncManager.IsDreamActive || Dreams.Instance == null || !Dreams.Instance.dreaming)
                return;

            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            if (dreamRoot == null)
                return;

            if (__result != null
                && (__result.transform.IsChildOf(dreamRoot)
                    || Vector3.Distance(__result.transform.position, dreamRoot.position) <= 250f))
                return;

            UniqueObject[] all = Object.FindObjectsOfType<UniqueObject>(true);
            UniqueObject best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                UniqueObject u = all[i];
                if (u == null || !string.Equals(u.type, type, System.StringComparison.Ordinal))
                    continue;
                if (!u.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(u.transform.position, dreamRoot.position) > 250f)
                    continue;
                float d = Vector3.Distance(u.transform.position, dreamRoot.position);
                if (d < bestD)
                {
                    bestD = d;
                    best = u;
                }
            }
            if (best == null)
                return;

            __result = best.gameObject;
            if (__instance.objects != null)
                __instance.objects[type] = best;
        }
    }
}
