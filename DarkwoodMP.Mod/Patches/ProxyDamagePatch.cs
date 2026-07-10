using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

/// <summary>
/// Forwards damage received by the remote proxy (RemotePlayerProxy CharBase.getHit)
/// to the actual player on the other peer. Host sends DamagePlayerMessage,
/// client sends FriendlyFireMessage.
/// </summary>
namespace DWMPHorde.Patches
{
    /// <summary>Forwards damage from the remote proxy (on either host or client) to the other peer via DamagePlayer/FriendlyFire message.</summary>
    [HarmonyPatch(typeof(CharBase), "getHit",
        typeof(float), typeof(Transform),
        typeof(bool), typeof(bool), typeof(bool),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
    public static class ProxyDamagePatch
    {
        private static bool Prefix(CharBase __instance, object[] __args)
        {
            float damage = (float)__args[0];
            Transform attackerTransform = (Transform)__args[1];
            bool CanCutInHalf = (bool)__args[2];
            bool byPlayer = (bool)__args[3];

            RemotePlayerProxy proxy = __instance.GetComponent<RemotePlayerProxy>();
            if (proxy == null) return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline) return true;

            // Friendly fire toggle:
            // - Melee: byPlayer=true when a player attacks (set by MeleeSensor.OnTriggerEnter)
            // - Projectile: byPlayer=false always (Bullet.onCollide hardcodes false).
            //   Detect player-fired projectiles by null attackerTransform:
            //   Player.spawnBullet never sets objectThatSpawnedMe (unlike enemy code).
            bool isPlayerProjectile = !byPlayer && attackerTransform == null;
            if ((byPlayer || isPlayerProjectile) && !Config.ModConfig.FriendlyFireEnabled.Value)
            {
                ModRuntime.LegacyInfo("[ProxyDmg] friendly fire disabled, blocking " + damage + " dmg from player");
                return false;
            }

            Vector3 pos = proxy.transform.position;
            // Preserve fractional→int the same way melee does (already scaled by sensor/bullet).
            int dmg = Mathf.Max(1, Mathf.RoundToInt(damage));

            if (net.Role == NetworkRole.Host)
            {
                net.SendToPlayer(proxy.PlayerId, NetMessageType.DamagePlayer, w =>
                {
                    new DamagePlayerMessage
                    {
                        Damage = dmg,
                        AttackerPosX = pos.x,
                        AttackerPosY = pos.y,
                        AttackerPosZ = pos.z,
                        CanCutInHalf = CanCutInHalf,
                        ShowRedScreen = true
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                ModRuntime.LegacyInfo("[ProxyDmg] host proxy took " + dmg + " damage — sent to client");
            }
            else
            {
                // AttackerPlayerId required so host can attribute 3+ FF correctly
                // (fallback to receive peer id alone is wrong if message is ever relayed).
                int localId = net.LocalPlayerId;
                net.Send(NetMessageType.FriendlyFire, w =>
                {
                    new FriendlyFireMessage
                    {
                        Damage = dmg,
                        AttackerPosX = pos.x,
                        AttackerPosY = pos.y,
                        AttackerPosZ = pos.z,
                        CanCutInHalf = CanCutInHalf,
                        AttackerPlayerId = localId,
                        VictimPlayerId = proxy.PlayerId
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                ModRuntime.LegacyInfo("[ProxyDmg] client proxy took " + dmg + " damage — sent to host (victim=" + proxy.PlayerId + ")");
            }

            return false;
        }
    }
}
