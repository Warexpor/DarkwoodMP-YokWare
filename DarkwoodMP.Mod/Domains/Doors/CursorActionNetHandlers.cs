using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Cursor action + location transport handlers composed for 0.8.</summary>
    internal sealed class CursorActionNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CursorActionNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        private string _cursorEnterDebounceKey;
        private float _cursorEnterDebounceUntil;

        internal void HandleActivateCursorAction(ActivateCursorActionMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            CustomCursorAction best = FindCustomCursorAction(pos, msg.ObjectName);
            if (best == null)
            {
                ModRuntime.Log?.LogWarning(
                    $"[CursorActionSync] host: no CustomCursorAction near {pos} name={msg.ObjectName}");
                return;
            }

            string actionName = best.name ?? msg.ObjectName ?? "";
            int requesterId = _net.CurrentReceivePlayerId;

            // Location enter is per-player transport — never activate() on host (TPs host).
            if (DWMPHorde.Patches.CustomCursorActionSync.IsLocationEnterAction(actionName))
            {
                string debounceKey = actionName + "@" + requesterId;
                float now = Time.time;
                if (debounceKey == _cursorEnterDebounceKey && now < _cursorEnterDebounceUntil)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo(
                            $"[CursorActionSync] debounced location enter {actionName} p{requesterId}");
                    return;
                }
                _cursorEnterDebounceKey = debounceKey;
                _cursorEnterDebounceUntil = now + 2f;

                if (requesterId <= 0)
                {
                    ModRuntime.Log?.LogWarning(
                        $"[CursorActionSync] location enter {actionName} but no requester id");
                    return;
                }

                if (!DWMPHorde.Patches.CustomCursorActionSync.TryResolveLocationEnterName(best, out string locName)
                    || string.IsNullOrEmpty(locName))
                {
                    ModRuntime.Log?.LogWarning(
                        $"[CursorActionSync] location enter {actionName}: could not resolve dest");
                    return;
                }

                bool fromWorld = Singleton<WorldGrid>.Instance != null
                    && Singleton<WorldGrid>.Instance.currentGrid != null
                    && Singleton<WorldGrid>.Instance.currentGrid.name == "World";

                _net.SendToPlayer(requesterId, NetMessageType.LocationTransport,
                    w => new LocationTransportMessage
                    {
                        LocationName = locName,
                        FromWorld = fromWorld
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo(
                    $"[CursorActionSync] LocationTransport → p{requesterId} '{locName}' (from {actionName})");
                return;
            }

            ModRuntime.LegacyInfo(
                $"[CursorActionSync] host activate {best.name} at {best.transform.position}");
            best.activate();
        }

        internal void HandleLocationTransport(LocationTransportMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (string.IsNullOrEmpty(msg.LocationName)) return;

            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null)
            {
                ModRuntime.Log?.LogWarning(
                    $"[LocationTransport] OutsideLocations null for '{msg.LocationName}'");
                return;
            }

            // Already inside this location — ignore (debounce / duplicate).
            if (ol.playerInOutsideLocation
                && string.Equals(
                    DreamSyncManager.CanonicalDreamLocationName(ol.currentLocationName ?? ""),
                    DreamSyncManager.CanonicalDreamLocationName(msg.LocationName),
                    System.StringComparison.OrdinalIgnoreCase))
            {
                ModRuntime.LegacyInfo(
                    $"[LocationTransport] already in '{msg.LocationName}' — skip");
                return;
            }

            ModRuntime.LegacyInfo(
                $"[LocationTransport] createLocation '{msg.LocationName}' fromWorld={msg.FromWorld}");
            // Vanilla createLocation: loading screen + spawn-if-needed + transport.
            ol.createLocation(msg.LocationName);
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

            CustomCursorAction[] all = WorldQueryHelper.GetCachedSceneComponents<CustomCursorAction>();
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
