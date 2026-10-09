using System.Collections.Generic;
using DWMPHorde;
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
    ///
    /// Enemy Character swings that were fanned out (EnemyAttack) skip the proxy:
    /// the victim's own client decides them. What remains here is player weapons
    /// (friendly fire) and shadow sensors.
    ///
    /// The proxy has several colliders, each firing OnTriggerEnter: a per-player
    /// debounce keeps one swing to one DamagePlayer. A player weapon sensor is
    /// consumed by the hit (vanilla Character branch); an enemy sensor is not
    /// (vanilla player branch), so it can still reach the host player.
    /// </summary>
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class HostMeleeSensorPatch
    {
        private const float ProxyHitDebounce = 0.25f;
        private static readonly Dictionary<int, float> _lastProxyHitTime = new Dictionary<int, float>();

        public static void Reset() => _lastProxyHitTime.Clear();

        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host
                || !ModRuntime.Network.IsConnected)
                return true;

            RemotePlayerProxy proxy = _collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null)
                return true;

            // Defender original (EnemyAttack sent): that player's own client re-created this
            // swing on its copy of the enemy and decides the hit there.
            if (DefenderAttackMarker.TryGetActive(__instance, out DefenderAttackMarker defender)
                && !defender.ClientCopy)
            {
                __instance.collidersToIgnore.Add(_collider);
                __instance.gameObjectsToIgnore.Add(_collider.gameObject);
                return false;
            }

            // Vanilla early-outs (ignore lists, attacker's own colliders, line of sight): where
            // vanilla would drop the hit, let it run — it returns without effect on a proxy.
            Transform attacker = __instance.attackerTransform;
            if (_collider.transform == null || attacker == null || IsIgnored(__instance, _collider))
                return true;
            if (attacker == _collider.transform || _collider.transform.IsChildOf(attacker))
                return true;
            if (!__instance.doesNotNeedLineOfSight && !Core.canSeeAttack(attacker, _collider.transform))
                return true;
            // Vanilla bookkeeping once a collider passed the gates.
            __instance.collidersToIgnore.Add(_collider);
            __instance.gameObjectsToIgnore.Add(_collider.gameObject);

            // NightShadows: only the curse owner takes damage from that wave.
            if (__instance.attackerTransform != null)
            {
                var shadowInfo = __instance.attackerTransform.GetComponent<ShadowSyncInfo>();
                if (shadowInfo == null && __instance.attackerTransform.parent != null)
                    shadowInfo = __instance.attackerTransform.parent.GetComponent<ShadowSyncInfo>();
                if (shadowInfo != null && shadowInfo.OwnerPlayerId > 0
                    && shadowInfo.OwnerPlayerId != proxy.PlayerId)
                    return false;
            }

            if (__instance.type == MeleeSensor.MeleeSensorType.player
                && !SessionSettings.FriendlyFireEnabled)
            {
                // Still consume the sensor — returning false alone left it lingering and
                // retriggering every FixedUpdate against the proxy colliders.
                ConsumeSensor(__instance);
                return false;
            }

            // Don't damage client if proxy's CharBase is dead / night-dead.
            CharBase proxyCB = proxy.CachedCharBase;
            if (proxyCB == null || !proxyCB.alive)
                return true;
            if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                return true;

            int pid = proxy.PlayerId;
            float now = Time.time;
            bool isPlayer = __instance.type == MeleeSensor.MeleeSensorType.player;
            if (_lastProxyHitTime.TryGetValue(pid, out float lastHit)
                && now - lastHit < ProxyHitDebounce)
            {
                // Same swing / multi-collider. A player weapon sensor is consumed (vanilla
                // consumes it on a Character hit); an enemy sensor lingers as in vanilla's
                // player branch, its collider already on the ignore list.
                if (isPlayer)
                    ConsumeSensor(__instance);
                return false;
            }
            _lastProxyHitTime[pid] = now;

            float strengthMod = 1f;
            if (__instance.attackerTransform != null)
            {
                CharBase atkCB = __instance.attackerTransform.GetComponent<CharBase>();
                if (atkCB != null)
                    strengthMod = atkCB.strengthModifier;
            }

            // Vanilla drain: melee weapons only, scaled by the attacker's strength modifier.
            Player hostPlayer = Player.Instance;
            if (isPlayer && hostPlayer != null && !InvItemClass.isNull(hostPlayer.currentItem)
                && hostPlayer.currentItem.baseClass != null && hostPlayer.currentItem.baseClass.isMelee)
            {
                hostPlayer.currentItem.drainDurability((float)__instance.itemDurabilityDrain * strengthMod);
            }

            // Vanilla fires the sensor's onHit callback when a non-player sensor lands on the player.
            if (!isPlayer && __instance.onHit != null)
                __instance.onHit();

            int dmg = Mathf.Max(1, (int)((float)__instance.damage * strengthMod));
            Vector3 atkPos = __instance.attackerTransform != null
                ? __instance.attackerTransform.position
                : proxy.transform.position;

            // No hit sound here: the victim's own getHit plays it (hit, blocked or dodged) and
            // sends it, so the host and every other peer hear it once on the stand-in.

            // No splat here: the victim's own getHit (DamagePlayer) shows its blood to everyone,
            // none on a block; one from the host as well doubled it.

            var msg = new DamagePlayerMessage
            {
                Damage = dmg,
                AttackerPosX = atkPos.x,
                AttackerPosY = atkPos.y,
                AttackerPosZ = atkPos.z,
                CanCutInHalf = dmg >= 80,
                ShowRedScreen = true,
                NormalHit = true,
                CanInterrupt = true,
                // Vanilla shadow sensors hit the player through getHitByShadow (flat, no armor).
                ShadowHit = !isPlayer && __instance.shadowSensor,
                // The victim's own client activates these (vanilla applies sensor effects after getHit).
                Effects = SensorEffectCodec.ToWire(__instance.effects)
            };
            ModRuntime.Network?.SendToPlayer(proxy.PlayerId, NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo(
                "[ProxyMelee] sensor hit p" + pid + " dmg=" + dmg
                + " atk=" + (__instance.attackerTransform != null ? __instance.attackerTransform.name : "?"));

            // Mirror vanilla MeleeSensor: a player weapon is consumed by its Character hit; an
            // enemy sensor is not consumed by a player hit (it can still reach the host player).
            if (isPlayer)
            {
                ConsumeSensor(__instance);
                if (hostPlayer != null && !InvItemClass.isNull(hostPlayer.currentItem))
                    hostPlayer.currentItem.refresh();
            }
            return false;
        }

        private static bool IsIgnored(MeleeSensor sensor, Collider col)
        {
            List<Collider> ignored = sensor.collidersToIgnore;
            for (int i = 0; i < ignored.Count; i++)
            {
                if (ignored[i] != null && ignored[i] == col)
                    return true;
            }
            return false;
        }

        private static void ConsumeSensor(MeleeSensor sensor)
        {
            if (sensor == null) return;
            try { Core.RemovePooledPrefab("Sensors", sensor.transform); }
            catch { /* ignore */ }
        }
    }
}
