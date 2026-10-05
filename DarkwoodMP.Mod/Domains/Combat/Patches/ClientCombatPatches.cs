using System.Collections.Generic;
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
    /// On the client, when the local player hits the remote proxy with
    /// a melee weapon, sends a FriendlyFireMessage to the host instead
    /// of applying damage locally (host is authoritative for proxy damage).
    /// </summary>
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class ClientFriendlyFirePatch
    {
        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            if (__instance.type != MeleeSensor.MeleeSensorType.player)
                return true;

            RemotePlayerProxy proxy = _collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null)
                return true;

            if (!SessionSettings.FriendlyFireEnabled)
                return true;

            Vector3 proxyPos = proxy.transform.position;
            AudioController.Play("player_melee_hit", proxyPos);

            Player local = Player.Instance;
            if (local != null && local.currentItem != null)
                local.currentItem.drainDurability(__instance.itemDurabilityDrain);

            float strengthMod = 1f;
            if (local != null)
            {
                CharBase cb = local.GetComponent<CharBase>();
                if (cb != null)
                    strengthMod = cb.strengthModifier;
            }
            int dmg = Mathf.Max(1, (int)((float)__instance.damage * strengthMod));
            Vector3 pos = local != null ? local.transform.position : proxy.transform.position;

            var msg = new FriendlyFireMessage
            {
                Damage = dmg,
                AttackerPosX = pos.x,
                AttackerPosY = pos.y,
                AttackerPosZ = pos.z,
                CanCutInHalf = dmg >= 80,
                AttackerPlayerId = ModRuntime.Network?.LocalPlayerId ?? 0,
                VictimPlayerId = proxy.PlayerId,
                Effects = SensorEffectCodec.ToWire(__instance.effects)
            };
            ModRuntime.Network?.Send(NetMessageType.FriendlyFire, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            return false;
        }
    }

    /// <summary>
    /// On the client, when the local player hits any target (Character,
    /// Door, Window, Item) with a melee weapon, sends the attack to the
    /// host for authoritative damage processing.
    /// Mirrors vanilla <c>MeleeSensor.OnTriggerEnter</c> for the local player's sensor: same
    /// early-outs (ignore lists, attacker self, line of sight), same Character gate, the sensor is
    /// consumed and the weapon durability drained exactly as vanilla does. Only the damage itself
    /// is host-authoritative. Door / barricaded window / destructible item hits keep running
    /// vanilla locally; vanilla's own <c>alertInArea(600)</c> for those is a no-op on a client
    /// (host owns AI), so the host is told to run it.
    /// </summary>
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class ClientMeleeSensorPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            if (__instance.type != MeleeSensor.MeleeSensorType.player)
                return true;

            // Skip proxy hits — ClientFriendlyFirePatch handles those
            if (_collider.GetComponentInParent<RemotePlayerProxy>() != null)
                return true;

            // Vanilla early-outs: vanilla runs them again and returns, so just let it.
            Transform attacker = __instance.attackerTransform;
            if (_collider.transform == null || attacker == null || IsIgnored(__instance, _collider))
                return true;
            if (attacker == _collider.transform || _collider.transform.IsChildOf(attacker))
                return true;
            if (!__instance.doesNotNeedLineOfSight && !Core.canSeeAttack(attacker, _collider.transform))
                return true;

            GameObject go = _collider.gameObject;
            Character c = go.GetComponent<Character>();
            if (c == null && go.transform.parent != null)
                c = go.transform.parent.GetComponent<Character>();

            if (c == null || !CanBeHit(c))
            {
                // Not a Character hit: vanilla runs locally. Its alertInArea(600) for doors,
                // barricaded windows and destructible items only wakes the host's AI if the host
                // is told, so forward exactly those (never a blanket sound per trigger).
                if (WouldVanillaAlert(__instance, _collider, go))
                    SendMeleeAlert();
                return true;
            }

            // ---- Character hit: damage is host-authoritative ----
            // Vanilla bookkeeping: ignore this collider/GO and the target's own colliders so the
            // same swing cannot re-trigger on another collider of the same character.
            __instance.collidersToIgnore.Add(_collider);
            __instance.gameObjectsToIgnore.Add(go);
            Collider cc = c.GetComponent<Collider>();
            if (cc != null && !__instance.collidersToIgnore.Contains(cc))
                __instance.collidersToIgnore.Add(cc);
            if (c.hitCollider != null)
            {
                Collider hc = c.hitCollider.GetComponent<Collider>();
                if (hc != null && !__instance.collidersToIgnore.Contains(hc))
                    __instance.collidersToIgnore.Add(hc);
            }

            float strengthMod = 1f;
            CharBase attackerBase = attacker.GetComponent<CharBase>();
            if (attackerBase != null)
                strengthMod = attackerBase.strengthModifier;

            Player local = Player.Instance;
            bool canCutInHalf = false;
            if (local != null && !InvItemClass.isNull(local.currentItem) && local.currentItem.baseClass != null)
                canCutInHalf = local.currentItem.baseClass.canCutInHalf;

            // Prefer host stable id when known; 0 → host resolves by name+hit pos (phantoms).
            short nameHash = 0;
            if (ClientEntityInterpolationService.IsHostSynced(c))
                CharacterTracker.TryGetStableId(c, out nameHash);

            // Hit SFX + Hit roll locally (host owns damage). Echo GetHit is ignored briefly.
            ClientEntityInterpolationService.NoteLocalHitPresentation(c, nameHash);

            Vector3 pos = local != null ? local.transform.position : c.transform.position;
            int dmg = Mathf.Max(1, (int)((float)__instance.damage * strengthMod));
            string entityName = c.name;
            if (entityName.EndsWith("(Clone)"))
                entityName = entityName.Substring(0, entityName.Length - 7);

            ModRuntime.LegacyInfo($"[Combat] client sending attack: target={entityName}(id={nameHash}) dmg={dmg}");

            var msg = new PlayerAttackMessage
            {
                TargetNameHash = nameHash,
                Damage = dmg,
                CanCutInHalf = canCutInHalf,
                AttackerPosX = pos.x,
                AttackerPosY = pos.y,
                AttackerPosZ = pos.z,
                TargetName = entityName,
                TargetPosX = c.transform.position.x,
                TargetPosY = c.transform.position.y,
                TargetPosZ = c.transform.position.z,
                Effects = SensorEffectCodec.ToWire(__instance.effects),
                IsMelee = true
            };
            ModRuntime.Network?.Send(NetMessageType.PlayerAttack, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ClientEntityInterpolationService.ShowHitHealthBar(c);

            // Vanilla's remaining Character-hit steps for a player sensor.
            if (local != null && !InvItemClass.isNull(local.currentItem) && local.currentItem.baseClass.isMelee)
                local.currentItem.drainDurability((float)__instance.itemDurabilityDrain * strengthMod);
            if (__instance.adrenalineSkillActive)
                Core.AddPrefab("FX/skills/hit_adrenalin_prefab", c.transform.position,
                    Quaternion.Euler(0f, Random.Range(0, 360), 0f), null);
            Core.RemovePooledPrefab("Sensors", __instance.transform);
            if (local != null && !InvItemClass.isNull(local.currentItem))
                local.currentItem.refresh();

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

        /// <summary>Vanilla's Character gate for a player melee sensor hit.</summary>
        private static bool CanBeHit(Character c)
        {
            return !c.ethereal
                && (c.alive || (c.hasPreDeath && c.health > 0f))
                && !c.isUnderwater
                && !c.isUnderground
                && (c.flier == null || !c.flier.inFlight || c.flier.diving);
        }

        /// <summary>True when vanilla's non-Character branches would call alertInArea(600).</summary>
        private static bool WouldVanillaAlert(MeleeSensor sensor, Collider col, GameObject go)
        {
            if (go.CompareTag("Door"))
                return true;

            Window window = go.GetComponent<Window>();
            if (window == null && col.attachedRigidbody != null)
                window = col.attachedRigidbody.GetComponent<Window>();
            if (window != null && window.barricaded)
                return true;

            Item item = go.GetComponent<Item>();
            if (item == null && col.attachedRigidbody != null)
                item = col.attachedRigidbody.GetComponent<Item>();
            return item != null && (item.shadowArmor != null || (item.destructible && !item.destroyed));
        }

        private static void SendMeleeAlert()
        {
            // Same radius / loudness as vanilla's Character.alertInArea(pos, 600f, false, 1f).
            var soundMsg = new PlayerSoundMessage
            {
                Range = 600f,
                DangerousSound = false,
                Volume = 1f,
                Gunshot = false
            };
            ModRuntime.Network?.Send(NetMessageType.PlayerSound, w => soundMsg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
