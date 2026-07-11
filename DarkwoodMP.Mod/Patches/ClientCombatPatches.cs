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

            if (!Config.ModConfig.FriendlyFireEnabled.Value)
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
                AttackerPlayerId = LanNetworkManager.Instance?.LocalPlayerId ?? 0,
                VictimPlayerId = proxy.PlayerId
            };
            LanNetworkManager.Instance?.Send(NetMessageType.FriendlyFire, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            return false;
        }
    }

    /// <summary>
    /// On the client, when the local player hits any target (Character,
    /// Door, Window, Item) with a melee weapon, sends the attack to the
    /// host for authoritative damage processing and broadcasts the hit
    /// sound to other clients.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class ClientMeleeSensorPatch
    {
        // Time-based debounce per character to prevent duplicate
        // OnTriggerEnter from multiple colliders on the same target
        // in one swing.  Time.time keyed by character nameHash.
        // This avoids pooling issues with GetInstanceID().
        private const float HIT_DEBOUNCE = 0.2f;
        private static readonly Dictionary<short, float> _lastCharHitTime =
            new Dictionary<short, float>();

        public static void Reset()
        {
            _lastCharHitTime.Clear();
        }

        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            if (__instance.type != MeleeSensor.MeleeSensorType.player)
                return true;

            // Skip proxy hits — ClientFriendlyFirePatch handles those
            if (_collider.GetComponentInParent<RemotePlayerProxy>() != null)
                return true;

            // Send sound for all player melee hits (Character, Door, Window, Item)
            var soundMsg = new PlayerSoundMessage
            {
                Range = 600f,
                DangerousSound = false,
                Volume = 1f,
                Gunshot = false
            };
            LanNetworkManager.Instance?.Send(NetMessageType.PlayerSound, w => soundMsg.Serialize(w), DeliveryMethod.ReliableOrdered);

            Character c = _collider.GetComponent<Character>();
            if (c == null)
            {
                Rigidbody rb = _collider.attachedRigidbody;
                if (rb != null) c = rb.GetComponent<Character>();
            }
            if (c == null) return true;

            // Prefer host stable id when known; 0 → host resolves by name+hit pos (phantoms).
            short nameHash = 0;
            if (ClientEntityInterpolationService.IsHostSynced(c))
                CharacterTracker.TryGetStableId(c, out nameHash);
            // Debounce key: stable id or a stable hash of name when unsynced.
            short debounceKey = nameHash != 0 ? nameHash : (short)(c.name.GetHashCode() & 0x7FFF);

            float now = Time.time;
            if (_lastCharHitTime.TryGetValue(debounceKey, out float lastHit) &&
                now - lastHit < HIT_DEBOUNCE)
                return false;
            _lastCharHitTime[debounceKey] = now;

            // Hit SFX comes from host EntitySoundType.GetHit after damage applies.

            Vector3 pos = Player.Instance != null
                ? Player.Instance.transform.position
                : c.transform.position;

            float strengthMod = 1f;
            bool canCutInHalf = false;
            if (Player.Instance != null)
            {
                CharBase cb = Player.Instance.GetComponent<CharBase>();
                if (cb != null)
                    strengthMod = cb.strengthModifier;
                if (!InvItemClass.isNull(Player.Instance.currentItem) && Player.Instance.currentItem.baseClass != null)
                    canCutInHalf = Player.Instance.currentItem.baseClass.canCutInHalf;
            }

            int dmg = Mathf.Max(1, Mathf.RoundToInt((float)__instance.damage * strengthMod));
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
                TargetPosZ = c.transform.position.z
            };
            LanNetworkManager.Instance?.Send(NetMessageType.PlayerAttack, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            return false;
        }
    }
}
