using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// ShadowArmor absolute health fan-out (world Items + pos-keyed Character armor).
    /// Forwardable Broadcast for 3+.
    /// </summary>
    internal static class ShadowArmorSyncHelpers
    {
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
                Destroyed = (byte)(destroyed ? 1 : 0)
            };
        }

        internal static void SendState(ShadowArmor armor, bool destroyed, string reason)
        {
            if (armor == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
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
    }

    /// <summary>Melee / light damageMe. Host broadcasts; client → host (Forwardable).</summary>
    [HarmonyPatch(typeof(ShadowArmor), "damageMe")]
    public static class ShadowArmorDamageMeSyncPatch
    {
        private static void Postfix(ShadowArmor __instance)
        {
            if (__instance == null) return;
            ShadowArmorSyncHelpers.SendState(__instance, destroyed: false, "damageMe");
        }
    }

    /// <summary>Armor break. Prefix captures so Postfix can still read transform.</summary>
    [HarmonyPatch(typeof(ShadowArmor), "die")]
    public static class ShadowArmorDieSyncPatch
    {
        private static void Prefix(ShadowArmor __instance, out ShadowArmorStateMessage __state)
        {
            __state = default;
            if (__instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            __state = ShadowArmorSyncHelpers.BuildMessage(__instance, destroyed: true);
        }

        private static void Postfix(ShadowArmor __instance, ShadowArmorStateMessage __state)
        {
            // Prefix left Destroyed=0 when skipped (remote apply / null).
            if (__state.Destroyed == 0) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            ModRuntime.Network.Broadcast(NetMessageType.ShadowArmorState,
                w => __state.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ShadowArmorSync] send die at ({__state.PosX:F1},{__state.PosZ:F1})");
        }
    }
}
