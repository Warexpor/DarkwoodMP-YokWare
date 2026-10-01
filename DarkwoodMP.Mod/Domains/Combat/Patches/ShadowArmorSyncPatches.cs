using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// ShadowArmor health is host-authoritative (world Items + pos-keyed Character armor).
    /// The host broadcasts absolute state; a client only asks the host to apply the damage
    /// its own damageMe dealt (<see cref="ModeDamageRequest"/>) and never breaks an armor
    /// itself. <see cref="ShadowArmorStateMessage.Destroyed"/> carries the mode.
    /// </summary>
    internal static class ShadowArmorSyncHelpers
    {
        /// <summary>Host absolute state: alive with Health / MaxHealth.</summary>
        internal const byte ModeAlive = 0;
        /// <summary>Host absolute state: destroyed (apply die).</summary>
        internal const byte ModeDestroyed = 1;
        /// <summary>Client → host: Health is a damage amount to apply on the host's armor.</summary>
        internal const byte ModeDamageRequest = 2;

        internal static ShadowArmorStateMessage BuildMessage(ShadowArmor armor, bool destroyed)
        {
            Vector3 p = armor.transform.position;
            float maxHp = armor.maxHealth > 0f ? armor.maxHealth : 100f;
            // destHealth is the authority target; health lerps toward it in Update.
            float dest = Traverse.Create(armor).Field("destHealth").GetValue<float>();
            float hp = destroyed ? 0f : Mathf.Min(armor.health, dest);
            return new ShadowArmorStateMessage
            {
                PosX = Mathf.Round(p.x * 10f) / 10f,
                PosY = Mathf.Round(p.y * 10f) / 10f,
                PosZ = Mathf.Round(p.z * 10f) / 10f,
                Health = Mathf.Max(0f, hp),
                MaxHealth = maxHp,
                Destroyed = destroyed ? ModeDestroyed : ModeAlive
            };
        }

        /// <summary>A connected client's own (not network-applied) armor damage / break.</summary>
        internal static bool IsClientLocalAction()
        {
            var net = ModRuntime.Network;
            return net != null && net.IsConnected && net.Role == NetworkRole.Client
                && !LanNetworkManager.IsApplyingRemoteState && !TraverseHack.ApplyingFromNetwork;
        }

        internal static float ReadDestHealth(ShadowArmor armor)
            => Traverse.Create(armor).Field("destHealth").GetValue<float>();

        /// <summary>Host only: broadcast the armor's absolute state.</summary>
        internal static void SendState(ShadowArmor armor, bool destroyed, string reason)
        {
            if (armor == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            // Character-owned armor: still sync by pos for HP bar; do not touch
            // Character / entity combat authority on apply (handlers only set ShadowArmor).
            var msg = BuildMessage(armor, destroyed);
            ModRuntime.Network.Broadcast(NetMessageType.ShadowArmorState,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ShadowArmorSync] send {reason} at ({msg.PosX:F1},{msg.PosZ:F1}) " +
                $"health={msg.Health:F1}/{msg.MaxHealth:F1} destroyed={destroyed}");
        }

        /// <summary>Client: ask the host to apply <paramref name="damage"/> to this armor.</summary>
        internal static void SendDamageRequest(ShadowArmor armor, float damage)
        {
            if (armor == null || !(damage > 0f)) return;
            Vector3 p = armor.transform.position;
            var msg = new ShadowArmorStateMessage
            {
                PosX = Mathf.Round(p.x * 10f) / 10f,
                PosY = Mathf.Round(p.y * 10f) / 10f,
                PosZ = Mathf.Round(p.z * 10f) / 10f,
                Health = damage,
                MaxHealth = armor.maxHealth,
                Destroyed = ModeDamageRequest
            };
            ModRuntime.Network.Broadcast(NetMessageType.ShadowArmorState,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ShadowArmorSync] damage request {damage:F1} at ({msg.PosX:F1},{msg.PosZ:F1})");
        }
    }

    /// <summary>Melee / light damageMe. Host broadcasts absolute state; a client sends the damage it dealt.</summary>
    [HarmonyPatch(typeof(ShadowArmor), "damageMe")]
    public static class ShadowArmorDamageMeSyncPatch
    {
        // __state: client's destHealth before the hit; -1 when this is not a client-local hit.
        private static void Prefix(ShadowArmor __instance, out float __state)
        {
            __state = -1f;
            if (__instance == null || !ShadowArmorSyncHelpers.IsClientLocalAction()) return;
            try { __state = Mathf.Max(0f, ShadowArmorSyncHelpers.ReadDestHealth(__instance)); }
            catch { __state = -1f; }
        }

        private static void Postfix(ShadowArmor __instance, float __state)
        {
            if (__instance == null) return;
            if (__state >= 0f)
            {
                float after;
                try { after = ShadowArmorSyncHelpers.ReadDestHealth(__instance); }
                catch { return; }
                float dealt = __state - Mathf.Max(0f, after);
                if (dealt > 0f)
                    ShadowArmorSyncHelpers.SendDamageRequest(__instance, dealt);
                return;
            }
            ShadowArmorSyncHelpers.SendState(__instance, destroyed: false, "damageMe");
        }
    }

    /// <summary>
    /// Armor break. A connected client never breaks an armor on its own: the host breaks it
    /// when its own health reaches 0 and broadcasts the destroyed state. Host Prefix captures
    /// the state so the Postfix can still read the transform.
    /// </summary>
    [HarmonyPatch(typeof(ShadowArmor), "die")]
    public static class ShadowArmorDieSyncPatch
    {
        private static bool Prefix(ShadowArmor __instance, out ShadowArmorStateMessage __state)
        {
            __state = default;
            if (__instance == null) return true;
            if (ShadowArmorSyncHelpers.IsClientLocalAction())
                return false;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return true;
            __state = ShadowArmorSyncHelpers.BuildMessage(__instance, destroyed: true);
            return true;
        }

        private static void Postfix(ShadowArmor __instance, ShadowArmorStateMessage __state)
        {
            // Prefix left the mode at ModeAlive when skipped (remote apply / client / null).
            if (__state.Destroyed != ShadowArmorSyncHelpers.ModeDestroyed) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            ModRuntime.Network.Broadcast(NetMessageType.ShadowArmorState,
                w => __state.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ShadowArmorSync] send die at ({__state.PosX:F1},{__state.PosZ:F1})");
        }
    }
}
