using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// On client, intercepts Character.getHit() for local player attacks: sends
    /// PlayerAttackMessage to host instead of applying damage locally. Handles
    /// hitscan, projectile, and explosion AOE damage. Host is authoritative.
    /// </summary>
    [HarmonyPatch(typeof(Character), "getHit", new[] { typeof(float), typeof(Transform), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class ClientDamageRedirectPatch
    {
        private static bool Prefix(Character __instance, object[] __args)
        {
            float Damage = (float)__args[0];
            Transform attackerTransform = (Transform)__args[1];
            bool byPlayer = (bool)__args[3];

            try
            {
                var net = ModRuntime.Network as LanNetworkManager;
                if (net == null || net.Role != NetworkRole.Client)
                    return true;

                bool isPlayerDamage = attackerTransform != null && Player.Instance != null && attackerTransform == Player.Instance.transform;
                bool isProjectileDamage = attackerTransform == null && TraverseHack.IsInsidePlayerBulletCollision;
                bool isExplosionAOE = TraverseHack.IsInsideLocalExplosion;

                if (!isPlayerDamage && !isProjectileDamage && !isExplosionAOE)
                    return true;

                // Muted throwables (visualOnly / client own throw) zero Explodes.damage.
                // Still block local getHit so ghost blasts never hurt, but do not spam
                // PlayerAttack(0) — host already owns combat via its full projectile.
                if (isExplosionAOE && Damage <= 0f)
                    return false;

                bool isSynced = ClientEntityInterpolationService.IsHostSynced(__instance);
                short stableId = isSynced ? CharacterTracker.GetStableId(__instance) : (short)0;
                Vector3 pos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
                Vector3 targetPos = __instance.transform.position;

                bool canCut = false;
                if (Player.Instance != null && !InvItemClass.isNull(Player.Instance.currentItem)
                    && Player.Instance.currentItem.baseClass != null)
                    canCut = Player.Instance.currentItem.baseClass.canCutInHalf;

                net.Send(NetMessageType.PlayerAttack, w => new PlayerAttackMessage
                {
                    TargetNameHash = stableId,
                    Damage = (int)Damage,
                    AttackerPosX = pos.x,
                    AttackerPosY = pos.y,
                    AttackerPosZ = pos.z,
                    TargetName = __instance.name,
                    TargetPosX = targetPos.x,
                    TargetPosY = targetPos.y,
                    TargetPosZ = targetPos.z,
                    CanCutInHalf = canCut
                }.Serialize(w), DeliveryMethod.ReliableOrdered);

                ModRuntime.LegacyInfo($"[DamageRedirect] sent PlayerAttack: target={__instance.name} id={stableId} dmg={(int)Damage} synced={isSynced}");

                // Always block local getHit — host is authoritative. Even for unsynced entities,
                // the host will try name-fallback; letting local damage also apply would
                // result in double damage if host finds the entity.
                return false;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError($"[DamageRedirect] EXCEPTION in Prefix: {ex}");
                return true;
            }
        }
    }
}
