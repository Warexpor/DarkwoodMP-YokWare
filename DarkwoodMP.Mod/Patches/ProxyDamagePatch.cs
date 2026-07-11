using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
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

            // Night-dead peer: no further damage (proxy may still exist for corpse pose).
            if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                return false;
            CharBase proxyCb = proxy.GetComponent<CharBase>();
            if (proxyCb != null && !proxyCb.alive)
                return false;

            // Player-sourced vs AI/env:
            // - Melee: byPlayer=true (MeleeSensor)
            // - Player projectile: attackerTransform null + player bullet flag
            //   (Bullet.onCollide hardcodes byPlayer=false; player bullets leave objectThatSpawnedMe null)
            // - AI melee: byPlayer=false, attackerTransform = enemy (NOT player FF)
            // Do NOT treat all null-attacker hits as FF — that blocked env damage when FF off.
            bool isPlayerProjectile = !byPlayer && attackerTransform == null
                && TraverseHack.IsInsidePlayerBulletCollision;
            bool isPlayerRoot = attackerTransform != null && Player.Instance != null
                && (attackerTransform == Player.Instance.transform
                    || attackerTransform.IsChildOf(Player.Instance.transform));
            bool isProxyAttacker = attackerTransform != null
                && attackerTransform.GetComponentInParent<RemotePlayerProxy>() != null;
            bool isPlayerSourced = byPlayer || isPlayerProjectile || isPlayerRoot || isProxyAttacker;

            if (isPlayerSourced && !Config.ModConfig.FriendlyFireEnabled.Value)
            {
                ModRuntime.LegacyInfo("[ProxyDmg] friendly fire disabled, blocking " + damage + " dmg from player");
                return false;
            }

            Vector3 pos = proxy.transform.position;
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

                ModRuntime.LegacyInfo("[ProxyDmg] host proxy took " + dmg + " damage — sent to client p"
                    + proxy.PlayerId + " playerSourced=" + isPlayerSourced);
            }
            else
            {
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
