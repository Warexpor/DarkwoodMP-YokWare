using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Examinable / story onExamine:
    /// Client keeps local HUD (<c>displayMessage</c> + local <c>DescriptionPool</c> draw)
    /// but must not fire <c>EventTrigger.onExamine</c> — one-shot GE is host-auth via
    /// <see cref="GameEventsFiredPatch"/>. Client Prefix sends <c>ExamineObject</c> request;
    /// host re-runs <c>examine()</c> for triggers + shared flags, with HUD suppressed so the
    /// host does not see the client's flavor text.
    ///
    /// <c>DescriptionPool</c> depletion stays per-peer presentation (no protocol bump for the
    /// drawn key). Examined / displayedDescriptionPool flags fan out so re-examine / pool
    /// one-shots latch consistently. HidingPlace is AI cabinet hideouts — host Character AI.
    /// </summary>
    internal static class ExaminableExamineSync
    {
        /// <summary>Host: suppress Player.displayMessage while applying a remote examine request.</summary>
        internal static int SuppressHostExamineHud;
    }

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
            // Local examine still runs for personal HUD; onExamine triggers are blocked
            // in Core.sendTriggerInfo (see ExaminableOnExamineTriggerPatch).
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
    /// Block client-local onExamine EventTriggers. ExamineObject request already asked the
    /// host to re-run examine for story GE; client one-shots would latch EventTrigger.fired
    /// without applying GE (GameEventsFiredPatch) and desync late-join assumptions.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(bool) })]
    public static class ExaminableOnExamineTriggerPatch
    {
        private static bool Prefix(EventTrigger.Type triggerType)
        {
            return ExaminableOnExamineTrigger.Allow(triggerType);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) })]
    public static class ExaminableOnExamineTriggerValuePatch
    {
        private static bool Prefix(EventTrigger.Type triggerType)
        {
            return ExaminableOnExamineTrigger.Allow(triggerType);
        }
    }

    internal static class ExaminableOnExamineTrigger
    {
        internal static bool Allow(EventTrigger.Type triggerType)
        {
            if (triggerType != EventTrigger.Type.onExamine) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Client) return true;
            return false;
        }
    }

    /// <summary>Host must not show the client's examine flavor text when re-running examine.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.displayMessage),
        new[] { typeof(string), typeof(bool), typeof(bool) })]
    public static class ExaminableHostHudSuppressPatch
    {
        private static bool Prefix()
        {
            return ExaminableExamineSync.SuppressHostExamineHud <= 0;
        }
    }

    /// <summary>
    /// HidingPlace spawns AI on enable. In multiplayer clients already disable AI;
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
            ModRuntime.LegacyInfo($"[HidingPlace] client suppressed OnEnable spawn on {__instance?.name}");
            return false;
        }
    }
}
