using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// CustomCursorAction ("Lie down", custom UI actions) → EventTrigger.onActivate →
    /// one-shot GameEvents. Client one-shots are blocked by GameEventsFiredPatch, so
    /// the press was a silent no-op (dream bed → GE "item" → endDream dream_underground_bed).
    /// Mirror ExaminableSync: client defers onActivate to host.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(bool) })]
    public static class CustomCursorActionActivateSyncPatch
    {
        private static bool Prefix(GameObject destGO, EventTrigger.Type triggerType)
        {
            return CustomCursorActionSync.TryDeferClientActivate(destGO, triggerType);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) })]
    public static class CustomCursorActionActivateSyncValuePatch
    {
        private static bool Prefix(GameObject destGO, EventTrigger.Type triggerType)
        {
            return CustomCursorActionSync.TryDeferClientActivate(destGO, triggerType);
        }
    }

    internal static class CustomCursorActionSync
    {
        internal static bool TryDeferClientActivate(GameObject destGO, EventTrigger.Type triggerType)
        {
            if (destGO == null) return true;
            if (triggerType != EventTrigger.Type.onActivate) return true;
            if (destGO.GetComponent<CustomCursorAction>() == null) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Client) return true;

            Vector3 p = destGO.transform.position;
            net.Send(NetMessageType.ActivateCursorAction,
                w => new ActivateCursorActionMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = destGO.name ?? ""
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[CursorActionSync] client request {destGO.name} at {p}");
            return false;
        }
    }
}

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleActivateCursorAction(ActivateCursorActionMessage msg)
        {
            if (_role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            CustomCursorAction best = FindCustomCursorAction(pos, msg.ObjectName);
            if (best == null)
            {
                ModRuntime.Log?.LogWarning(
                    $"[CursorActionSync] host: no CustomCursorAction near {pos} name={msg.ObjectName}");
                return;
            }

            ModRuntime.LegacyInfo(
                $"[CursorActionSync] host activate {best.name} at {best.transform.position}");
            best.activate();
        }

        private static CustomCursorAction FindCustomCursorAction(Vector3 pos, string name)
        {
            Transform dreamRoot = DreamSyncManager.IsDreamActive
                ? DreamSyncManager.GetDreamLocationTransform()
                : null;

            bool OnPad(CustomCursorAction c) =>
                dreamRoot == null
                || c.transform.IsChildOf(dreamRoot)
                || c.transform == dreamRoot;

            CustomCursorAction Pick(CustomCursorAction c) =>
                c != null && OnPad(c) ? c : null;

            CustomCursorAction byName = null;
            if (!string.IsNullOrEmpty(name))
                byName = Pick(
                    WorldQueryHelper.FindNearestByName<CustomCursorAction>(pos, name, 3f));
            if (byName != null) return byName;

            CustomCursorAction nearest = Pick(
                WorldQueryHelper.FindNearest<CustomCursorAction>(pos, 2.5f));
            if (nearest != null) return nearest;

            if (dreamRoot == null) return null;

            CustomCursorAction[] all = Object.FindObjectsOfType<CustomCursorAction>(true);
            CustomCursorAction padBest = null;
            float bestDist = 3f;
            for (int i = 0; i < all.Length; i++)
            {
                CustomCursorAction c = all[i];
                if (c == null || !OnPad(c)) continue;
                if (!string.IsNullOrEmpty(name)
                    && !c.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    padBest = c;
                }
            }
            if (padBest != null) return padBest;

            // Name mismatch (parent vs leaf) — nearest on pad within range.
            for (int i = 0; i < all.Length; i++)
            {
                CustomCursorAction c = all[i];
                if (c == null || !OnPad(c)) continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    padBest = c;
                }
            }
            return padBest;
        }
    }
}
