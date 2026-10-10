using HarmonyLib;
using PathologicalGames;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A chapter scene load tears the old scene down in no fixed order. When the FX/UI SpawnPools
    /// go first they leave <c>PoolManager.Pools</c>, and every Character destroyed after them still
    /// carrying an attached message or effect hits <c>Core.RemovePooledPrefab</c>, which indexes the
    /// missing pool (null) and throws in <c>Character.OnDestroy</c>, skipping the rest of its cleanup.
    /// With a pool gone the object can no longer be despawned into it, so it is destroyed instead,
    /// the same thing vanilla does with an object no pool owns.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.RemovePooledPrefab), new[] { typeof(Transform) })]
    public static class PooledPrefabTeardownPatch
    {
        private static bool Prefix(Transform prefab)
        {
            if (PoolManager.Pools["FX"] != null && PoolManager.Pools["UI"] != null)
                return true;
            if (prefab != null)
                Object.Destroy(prefab.gameObject);
            return false;
        }
    }

    /// <summary>
    /// The same teardown for a banshee: vanilla <c>Character.OnDestroy</c> runs
    /// <c>onBansheeOutOfSightOfPlayer</c>, which parents a new banshee overlay under the UI. With
    /// the UI (or the player) already destroyed, <c>UI.initBansheeOverlay</c> throws. Nothing it
    /// does (walk to the player, fade the overlay) matters while the scene goes.
    /// </summary>
    [HarmonyPatch(typeof(Character), "onBansheeOutOfSightOfPlayer")]
    public static class BansheeTeardownOverlayPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix() => Singleton<UI>.Instance != null && Player.Instance != null;
    }
}
