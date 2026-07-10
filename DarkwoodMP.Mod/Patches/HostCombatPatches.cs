using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Intercepts MeleeSensor.OnTriggerEnter on the host when the hit
    /// target is the remote proxy. Sends a DamagePlayerMessage to the
    /// client and drains host weapon durability. Skips vanilla hit logic
    /// since the proxy is not a real Player and would be ignored.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class HostMeleeSensorPatch
    {
        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;

            RemotePlayerProxy proxy = _collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null)
                return true;

            if (__instance.type == MeleeSensor.MeleeSensorType.player
                && !Config.ModConfig.FriendlyFireEnabled.Value)
                return true;

            // Don't damage client if proxy's CharBase is dead (e.g. after client died
            // and before respawn). The proxy is immortal during normal play but set to
            // dead explicitly by HandlePlayerDied on the host.
            CharBase proxyCB = proxy.GetComponent<CharBase>();
            if (proxyCB == null || !proxyCB.alive)
                return true;

            bool isPlayer = __instance.type == MeleeSensor.MeleeSensorType.player;

            // Drain weapon durability if it's the host player attacking
            if (isPlayer && Player.Instance != null && Player.Instance.currentItem != null)
            {
                Player.Instance.currentItem.drainDurability(__instance.itemDurabilityDrain);
            }

            float strengthMod = 1f;
            if (__instance.attackerTransform != null)
            {
                CharBase atkCB = __instance.attackerTransform.GetComponent<CharBase>();
                if (atkCB != null)
                    strengthMod = atkCB.strengthModifier;
            }
            int dmg = Mathf.Max(1, (int)((float)__instance.damage * strengthMod));
            Vector3 atkPos = __instance.attackerTransform != null
                ? __instance.attackerTransform.position
                : proxy.transform.position;

            // Play hit sound at proxy position
            Vector3 proxyPos = proxy.transform.position;
            AudioController.Play("player_melee_hit", proxyPos);

            // Find hit point on proxy
            Vector3 hitPoint = _collider.ClosestPoint(atkPos);

            // Spawn blood locally — nest-safe apply flag so we do not clobber an
            // outer NetworkApplyGuard / TraverseHack scope.
            bool inWater = proxyCB != null && proxyCB.inWater;
            string bloodPrefab = inWater ? "FX/Bloodsplats/Shotsplat" : "FX/Bloodsplats/Shotsplat_stay";
            float rotY = __instance.attackerTransform != null ? __instance.attackerTransform.eulerAngles.y : 0f;
            float rotVariance = Random.Range(-20f, 20f);
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try { Core.AddPrefab(bloodPrefab, hitPoint, Quaternion.Euler(90f, rotY + rotVariance, 0f), null); }
            finally { TraverseHack.SetExplicitFlag(prevHack); }

            LanNetworkManager.Instance?.Broadcast(NetMessageType.BulletImpact, w => new BulletImpactMessage
            {
                PrefabName = bloodPrefab,
                PoolName = "",
                PosX = hitPoint.x,
                PosY = hitPoint.y,
                PosZ = hitPoint.z,
                RotX = 90f,
                RotY = rotY + rotVariance,
                RotZ = 0f
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            var msg = new DamagePlayerMessage
            {
                Damage = dmg,
                AttackerPosX = atkPos.x,
                AttackerPosY = atkPos.y,
                AttackerPosZ = atkPos.z,
                CanCutInHalf = dmg >= 80,
                ShowRedScreen = true
            };
            LanNetworkManager.Instance?.SendToPlayer(proxy.PlayerId, NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            return false;
        }
    }
}
