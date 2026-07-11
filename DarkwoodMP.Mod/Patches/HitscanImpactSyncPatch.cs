using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

/// <summary>
/// Forwards hitscan raycast hit FX (bullet_hit_1, Shotsplat) and proxy damage to the
/// other peer. Only runs when the local player fires a weapon, ensuring both clients
/// see blood FX and the proxy receives damage from the remote player at range.
/// </summary>
namespace DWMPHorde.Patches
{
    [HarmonyPatch]
    public static class HitscanImpactSyncPatch
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Physics), "Raycast", new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });
        private static void Postfix(bool __result, RaycastHit hitInfo, int layerMask)
        {
            if (!__result) return;
            if (layerMask != GameplayConstants.HitscanLayerMask) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            Player player = Player.Instance;
            if (player == null) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;

            Collider collider = hitInfo.collider;
            if (collider == null) return;

            Vector3 hitPoint = hitInfo.point;

            RemotePlayerProxy proxy = collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy != null)
            {
                // Physical projectile weapons (shotgun pellets, etc.) also Raycast with this
                // layer mask via FastProjectile. Their damage is Bullet.onCollide → CharBase
                // → ProxyDamagePatch. Only pure hitscan weapons need damage here.
                bool hitscanWeapon = player.currentItem.baseClass.item == null;
                if (!hitscanWeapon)
                    return;

                // Vanilla spawnBullet hitscan only damages Character components.
                // Remote proxies are CharBase-only (no Character), so getHit never runs
                // and ProxyDamagePatch never fires. Send FF damage here instead.
                if (!Config.ModConfig.FriendlyFireEnabled.Value)
                    return;

                CharBase proxyCB = proxy.GetComponent<CharBase>();
                if (proxyCB != null && !proxyCB.alive)
                    return;
                if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    return;

                // Match vanilla spawnBullet → getHit(baseClass.damage) plus upgrade mods
                // (melee uses getModdedDamage; firearms hitscan did not, but upgrades still apply).
                int baseDmg = player.currentItem.baseClass.damage;
                int dmg = Mathf.Max(1, player.currentItem.getModdedDamage(baseDmg));
                Vector3 atkPos = player.transform.position;
                bool inWater = proxyCB != null && proxyCB.inWater;

                if (net.Role == NetworkRole.Host)
                {
                    net.SendToPlayer(proxy.PlayerId, NetMessageType.DamagePlayer, w =>
                        new DamagePlayerMessage
                        {
                            Damage = dmg,
                            AttackerPosX = atkPos.x,
                            AttackerPosY = atkPos.y,
                            AttackerPosZ = atkPos.z,
                            ShowRedScreen = true
                        }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo($"[HitscanFF] host hit proxy {proxy.PlayerId} dmg={dmg}");
                }
                else
                {
                    net.Send(NetMessageType.FriendlyFire, w =>
                        new FriendlyFireMessage
                        {
                            Damage = dmg,
                            AttackerPosX = atkPos.x,
                            AttackerPosY = atkPos.y,
                            AttackerPosZ = atkPos.z,
                            AttackerPlayerId = net.LocalPlayerId,
                            VictimPlayerId = proxy.PlayerId
                        }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo($"[HitscanFF] client hit proxy {proxy.PlayerId} dmg={dmg}");
                }

                float yRot = player.transform.eulerAngles.y;
                string bloodPrefab = inWater ? "FX/Bloodsplats/Shotsplat" : "FX/Bloodsplats/Shotsplat_stay";
                TraverseHack.ApplyingFromNetwork = true;
                try
                {
                    Core.AddPrefab(bloodPrefab, hitPoint,
                        Quaternion.Euler(90f, yRot + Random.Range(-20f, 20f), 0f), null);
                }
                finally { TraverseHack.ApplyingFromNetwork = false; }

                // Play spatialized bullet impact sound at the hit point so the
                // shooter hears the direction the proxy was hit from.
                AudioController.Play("bullet_hit_1", hitPoint);

                // Send BulletImpact so the other peer sees the same blood
                net.Send(NetMessageType.BulletImpact, w => new BulletImpactMessage
                {
                    PrefabName = bloodPrefab,
                    PoolName = "",
                    PosX = hitPoint.x,
                    PosY = hitPoint.y,
                    PosZ = hitPoint.z,
                    RotX = 90f,
                    RotY = yRot + Random.Range(-40f, 40f),
                    RotZ = 0f
                }.Serialize(w), DeliveryMethod.ReliableOrdered);

                return;
            }

            // Wall/entity FX is handled by HitscanImpactForwardPatch (Prefix on Core.AddPooledPrefab).
            // This Postfix only handles proxy hits.
        }
    }
}
