using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Records the original prefab path on dynamically spawned objects (host entity
    /// broadcast + dream NPC scaling read it). Applied from <see cref="DWMPHorde.Patches.CoreAddPrefabStringPatch"/>.
    /// </summary>
    public static class AddPrefabRecordPathPatch
    {
        internal static void OnAddPrefab(GameObject __result, string prefab)
        {
            if (__result == null || string.IsNullOrEmpty(prefab))
                return;
            // Single-player never reads the path. Chapter resume re-hosts after the new
            // scene spawned its characters, so keep recording while that is pending.
            var net = ModRuntime.Network;
            if ((net == null || net.Role == NetworkRole.Offline) && !ChapterSessionResume.IsPending)
                return;
            var comp = __result.GetComponent<PrefabPathComponent>();
            if (comp == null)
                comp = __result.AddComponent<PrefabPathComponent>();
            comp.Path = prefab;
        }
    }
}
