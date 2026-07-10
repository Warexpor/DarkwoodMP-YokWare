using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Forwards hitscan and projectile weapon impact FX (bullet_hit_1, Shotsplat, blood splatters)
/// from the shooting player to the other peer so both see the same visual feedback.
/// </summary>
namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(Core), "AddPooledPrefab", typeof(string), typeof(string), typeof(Vector3), typeof(Quaternion))]
    public static class HitscanImpactForwardPatch
    {
        private static void Prefix(string pool, string prefab, Vector3 position, Quaternion quaternion)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (pool != "FX") return;
            if (prefab != "bullet_hit_1" && prefab != "Shotsplat1") return;

            // Skip if inside Bullet.onCollide — BulletFXSyncPatch handles projectile weapons
            if (TraverseHack.IsInsidePlayerBulletCollision) return;

            Player player = Player.Instance;
            if (player == null) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;
            if (!player.currentItem.baseClass.isFirearm) return;

            net.SendBulletImpact(new BulletImpactMessage
            {
                PrefabName = prefab,
                PoolName = pool,
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z,
                RotX = quaternion.eulerAngles.x,
                RotY = quaternion.eulerAngles.y,
                RotZ = quaternion.eulerAngles.z
            });
        }
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Core), "AddPrefab", typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool))]
    public static class HitscanBloodPatch
    {
        private static void Prefix(string prefab, Vector3 position, Quaternion quaternion)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (prefab == null) return;
            if (!prefab.StartsWith("FX/Bloodsplats/")) return;

            // Only forward blood FX that's near a player-controlled entity.
            // Blood from environmental damage (save-loaded dog collision, etc.)
            // on phantom characters far from players should NOT be forwarded.
            const float MAX_BLOOD_DISTANCE = 60f;
            bool nearPlayer = false;
            if (Player.Instance != null && Vector3.Distance(position, Player.Instance.transform.position) <= MAX_BLOOD_DISTANCE)
                nearPlayer = true;
            if (!nearPlayer)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (Vector3.Distance(position, proxy.transform.position) <= MAX_BLOOD_DISTANCE)
                    {
                        nearPlayer = true;
                        break;
                    }
                }
            }
            if (!nearPlayer)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[BloodFX] SKIPPED forward (far from players): {prefab} pos={position}");
                return;
            }

            net.SendBulletImpact(new BulletImpactMessage
            {
                PrefabName = prefab,
                PoolName = "",
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z,
                RotX = quaternion.eulerAngles.x,
                RotY = quaternion.eulerAngles.y,
                RotZ = quaternion.eulerAngles.z
            });
        }
    }

    /// <summary>
    /// Forwards projectile weapon impact FX. These bullets have a Bullet component
    /// and a FastProjectile component; on collision, Bullet.onCollide is called.
    /// </summary>
    [HarmonyPatch(typeof(Bullet), "onCollide", typeof(Collider), typeof(Vector3))]
    public static class BulletFXSyncPatch
    {
        private static void Prefix(Bullet __instance, Collider collider, Vector3 hitPoint)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline) return;

            if (__instance.objectThatSpawnedMe != null) return;
            if (collider == null) return;

            int layer = collider.gameObject.layer;

            RemotePlayerProxy proxy = collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy != null)
            {
                float y = __instance.transform.eulerAngles.y;
                Quaternion rot = Quaternion.Euler(90f, y + Random.Range(-40f, 40f), 0f);
                Core.AddPooledPrefab("FX", "Shotsplat1", hitPoint, rot);
                return;
            }

            string prefabName = "";
            string poolName = "";
            Quaternion fxRot = Quaternion.identity;

            bool isWall = layer == 0 || layer == 15;
            bool isChar = layer == 11 || layer == 21;

            if (isWall)
            {
                prefabName = "bullet_hit_1";
                poolName = "FX";
                fxRot = Quaternion.Euler(90f, __instance.transform.eulerAngles.y, 0f);
            }
            else if (isChar)
            {
                prefabName = "Shotsplat1";
                poolName = "FX";
                fxRot = Quaternion.Euler(90f, __instance.transform.eulerAngles.y + Random.Range(-40f, 40f), 0f);
            }
            else
            {
                return;
            }

            net.SendBulletImpact(new BulletImpactMessage
            {
                PrefabName = prefabName,
                PoolName = poolName,
                PosX = hitPoint.x,
                PosY = hitPoint.y,
                PosZ = hitPoint.z,
                RotX = fxRot.eulerAngles.x,
                RotY = fxRot.eulerAngles.y,
                RotZ = fxRot.eulerAngles.z
            });
        }
    }
}
