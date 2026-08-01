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
            bool canInterrupt = __args.Length > 4 && (bool)__args[4];
            bool normalHit = __args.Length <= 5 || (bool)__args[5];
            bool showRedScreen = __args.Length > 6 && (bool)__args[6];

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

            // Player FF / bullets: force red screen. AI AoE keeps caller's showRedScreen
            // (vanilla damagesAroundMe uses normalHit=false, showRed=false).
            if (isPlayerSourced)
                showRedScreen = true;

            Vector3 atkPos = attackerTransform != null
                ? attackerTransform.position
                : proxy.transform.position;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(damage));
            int attackerId = ProxyCombatRelay.ResolveAttackerPlayerId(attackerTransform, net.LocalPlayerId);
            ProxyCombatRelay.TryMarkGetHitRelay(attackerId, proxy.PlayerId);

            if (net.Role == NetworkRole.Host)
            {
                net.SendToPlayer(proxy.PlayerId, NetMessageType.DamagePlayer, w =>
                {
                    new DamagePlayerMessage
                    {
                        Damage = dmg,
                        AttackerPosX = atkPos.x,
                        AttackerPosY = atkPos.y,
                        AttackerPosZ = atkPos.z,
                        CanCutInHalf = CanCutInHalf,
                        ShowRedScreen = showRedScreen,
                        NormalHit = normalHit,
                        CanInterrupt = canInterrupt
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                ModRuntime.LegacyInfo("[ProxyDmg] host proxy took " + dmg + " damage — sent to client p"
                    + proxy.PlayerId + " playerSourced=" + isPlayerSourced
                    + " normalHit=" + normalHit);
            }
            else
            {
                int localId = net.LocalPlayerId;
                net.Send(NetMessageType.FriendlyFire, w =>
                {
                    new FriendlyFireMessage
                    {
                        Damage = dmg,
                        AttackerPosX = atkPos.x,
                        AttackerPosY = atkPos.y,
                        AttackerPosZ = atkPos.z,
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
