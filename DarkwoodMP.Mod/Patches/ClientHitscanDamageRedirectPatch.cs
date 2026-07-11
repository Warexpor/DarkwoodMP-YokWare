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

            try
            {
                var net = ModRuntime.Network as LanNetworkManager;
                if (net == null || net.Role != NetworkRole.Client)
                    return true;

                // Incoming damage to local player body must never become a PlayerAttack.
                // (Player overrides getHit; this is a belt for odd Character refs.)
                if (Player.Instance != null && __instance != null
                    && __instance.gameObject == Player.Instance.gameObject)
                    return true;

                // Outgoing attacks only: local player melee root, player bullets, local explosion AOE.
                bool isPlayerDamage = attackerTransform != null && Player.Instance != null
                    && (attackerTransform == Player.Instance.transform
                        || attackerTransform.IsChildOf(Player.Instance.transform));
                bool isProjectileDamage = attackerTransform == null && TraverseHack.IsInsidePlayerBulletCollision;
                bool isExplosionAOE = TraverseHack.IsInsideLocalExplosion;

                if (!isPlayerDamage && !isProjectileDamage && !isExplosionAOE)
                    return true;

                // Muted throwables (visualOnly / client own throw) zero Explodes.damage.
                if (isExplosionAOE && Damage <= 0f)
                    return false;

                // Always send name + hit pos so host can resolve phantoms / unsynced ids.
                // Prefer stable id when host-synced; 0 forces position+name match on host.
                short stableId = 0;
                if (ClientEntityInterpolationService.IsHostSynced(__instance))
                    CharacterTracker.TryGetStableId(__instance, out stableId);
                Vector3 pos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
                Vector3 targetPos = __instance.transform.position;

                bool canCut = false;
                if (Player.Instance != null && !InvItemClass.isNull(Player.Instance.currentItem)
                    && Player.Instance.currentItem.baseClass != null)
                    canCut = Player.Instance.currentItem.baseClass.canCutInHalf;

                int dmg = Mathf.Max(1, Mathf.RoundToInt(Damage));
                string entityName = __instance.name;
                if (entityName.EndsWith("(Clone)"))
                    entityName = entityName.Substring(0, entityName.Length - 7);

                net.Send(NetMessageType.PlayerAttack, w => new PlayerAttackMessage
                {
                    TargetNameHash = stableId,
                    Damage = dmg,
                    AttackerPosX = pos.x,
                    AttackerPosY = pos.y,
                    AttackerPosZ = pos.z,
                    TargetName = entityName,
                    TargetPosX = targetPos.x,
                    TargetPosY = targetPos.y,
                    TargetPosZ = targetPos.z,
                    CanCutInHalf = canCut
                }.Serialize(w), DeliveryMethod.ReliableOrdered);

                ModRuntime.LegacyInfo($"[DamageRedirect] sent PlayerAttack: target={entityName} id={stableId} dmg={dmg}");

                // Host authoritative — never also apply locally (double-kill / desync).
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
