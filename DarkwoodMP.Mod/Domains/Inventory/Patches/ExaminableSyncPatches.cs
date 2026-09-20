using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.11 Examinable / story onExamine:
    /// Client examine shows local description but host must run EventTriggers → GameEvents
    /// (4.2 blocks client one-shots). After host examine, peers get examined flags so
    /// re-examine / description-pool one-shots stay consistent.
    ///
    /// HidingPlace is AI cabinet hideouts (not player stealth) — host Character AI (1.2).
    /// </summary>
    [HarmonyPatch(typeof(Examinable), "examine")]
    public static class ExaminableExaminePatch
    {
        private static void Prefix(Examinable __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            // Client: ask host to run authoritative examine (triggers + flags).
            if (net.Role == NetworkRole.Client)
            {
                Vector3 p = __instance.transform.position;
                net.Send(NetMessageType.ExamineObject,
                    w => new ExamineObjectMessage
                    {
                        Action = ExamineObjectMessage.ActionRequest,
                        PosX = p.x,
                        PosY = p.y,
                        PosZ = p.z,
                        ObjectName = __instance.name ?? "",
                        Examined = __instance.examined,
                        DisplayedDescriptionPool = __instance.displayedDescriptionPool
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo($"[ExamineSync] client request {__instance.name} at {p}");
            }
        }

        private static void Postfix(Examinable __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host) return;

            // Host (local or via request): fan out examined state to all clients.
            Vector3 p = __instance.transform.position;
            net.Broadcast(NetMessageType.ExamineObject,
                w => new ExamineObjectMessage
                {
                    Action = ExamineObjectMessage.ActionState,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = __instance.name ?? "",
                    Examined = __instance.examined,
                    DisplayedDescriptionPool = __instance.displayedDescriptionPool
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ExamineSync] host state {__instance.name} examined={__instance.examined} pool={__instance.displayedDescriptionPool}");
        }
    }

    /// <summary>
    /// HidingPlace spawns AI on enable. In multiplayer clients already disable AI (1.2);
    /// skip client spawn so only host owns the hider character (avoids double Characters).
    /// </summary>
    [HarmonyPatch(typeof(HidingPlace), "OnEnable")]
    public static class HidingPlaceClientSpawnSuppressPatch
    {
        private static bool Prefix(HidingPlace __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            // Client: do not spawn a second AI into the cabinet.
            // Host entity snapshots will drive any visible character if needed.
            ModRuntime.LegacyInfo($"[HidingPlace] client suppressed OnEnable spawn on {__instance?.name}");
            return false;
        }
    }
}
