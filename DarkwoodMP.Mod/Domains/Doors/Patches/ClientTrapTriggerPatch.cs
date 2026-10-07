using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client: trap fire → TrapTriggered with TrapNetId so host applies + rebroadcasts.
    /// Host: mint id if needed (switchToTriggered path also broadcasts TrapState).
    /// </summary>
    [HarmonyPatch(typeof(Trigger), "OnAfterTrigger", typeof(Collider), typeof(bool))]
    public static class ClientTrapTriggerPatch
    {
        private static void Prefix(Trigger __instance)
        {
            if (__instance == null) return;
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                return;
            if (net.Role != NetworkRole.Client)
                return;
            if (!TrapNetworkId.IsWorldTrap(__instance.gameObject))
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;
            // Silent disarm / already sprung — do not ask host to boom.
            if (TrapDisarmHarvestTracker.IsSilentDisarm)
                return;
            if (__instance.triggered)
                return;

            Vector3 pos = __instance.transform.position;
            int trapId = TrapNetworkId.GetId(__instance.gameObject);
            // Client may not have an id yet — host will mint on apply.
            net.Send(NetMessageType.TrapTriggered, w => new TrapTriggeredMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                TrapNetId = trapId
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            ClientOwnTrapTriggers.Note(__instance.gameObject);
            ModRuntime.LegacyInfo($"[TrapTrigger] Client sent trap triggered id={trapId} at {pos}");
        }
    }

    /// <summary>
    /// Client: traps this player sprang and told the host about, until the host's sprung state
    /// comes back. A host scan sent before the host had the trigger still says "armed"; applied
    /// on arrival it re-armed the trap under the player caught in it.
    /// </summary>
    internal static class ClientOwnTrapTriggers
    {
        private const float AckWaitSec = 3f;

        private static readonly Dictionary<int, float> _sentAt = new Dictionary<int, float>(); // reset-in: Reset

        internal static void Reset() => _sentAt.Clear();

        internal static void Note(GameObject go)
        {
            if (go != null)
                _sentAt[go.GetInstanceID()] = Time.unscaledTime;
        }

        /// <summary>True when an "armed" state for this trap predates the host's answer to this player's trigger.</summary>
        internal static bool IsStaleArmed(GameObject go, bool triggered)
        {
            if (go == null) return false;
            int id = go.GetInstanceID();
            if (!_sentAt.TryGetValue(id, out float sent)) return false;
            if (triggered || Time.unscaledTime - sent > AckWaitSec)
            {
                _sentAt.Remove(id);
                return false;
            }
            return true;
        }
    }
}
