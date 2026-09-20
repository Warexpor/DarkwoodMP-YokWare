using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>Records the original prefab path on dynamically spawned objects.</summary>
    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class AddPrefabRecordPathPatch
    {
        private static void Postfix(GameObject __result, object[] __args)
        {
            string prefab = (string)__args[0];

            if (__result == null || string.IsNullOrEmpty(prefab))
                return;
            var comp = __result.GetComponent<PrefabPathComponent>();
            if (comp == null)
                comp = __result.AddComponent<PrefabPathComponent>();
            comp.Path = prefab;
        }
    }
}
