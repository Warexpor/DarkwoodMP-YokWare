using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static partial class MultiplayerMapManager
    {
        /// <summary>
        /// Session end: the party map board (<see cref="MapPinBoard"/>) saves and forgets itself, the
        /// open map drops its pins, and discoveries waiting for their MapElement are dropped.
        /// </summary>
        public static void Reset()
        {
            MapPinView.Reset();
            MapPinOverlay.Reset();
            MapPinBoard.Reset();
            ClearPendingDiscoveries();
        }

        public static void OnElementDiscovered(MapElement element)
        {
            if (element == null || string.IsNullOrEmpty(element.elementName))
                return;
            if (element.isWorldChunk || element.isDeathDrop)
                return;
            // A pin on an item (the Navigator skill's meat marker) is that player's own and does
            // not exist on other machines; broadcasting it left each peer rescanning for 5 minutes.
            if (element.GetComponent<Item>() != null)
                return;
            // Applying a remote discovery must not re-broadcast (loop).
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            if (!NetGuard.Connected(out var net)) return;

            Vector3 at = element.transform.position;
            var msg = new MapElementDiscoveredMessage { ElementName = element.elementName, HasPos = true, PosX = at.x, PosZ = at.z };
            net.Broadcast(NetMessageType.MapElementDiscovered, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[MapDiscovery] discovered '{element.elementName}' — broadcast to remote");
        }

        public static void OnRemoteElementDiscovered(string elementName, Vector3? at = null)
        {
            if (string.IsNullOrEmpty(elementName)) return;
            if (!TryApplyRemoteDiscovery(elementName, at))
                QueuePendingDiscovery(elementName, at);
        }
    }

    [HarmonyPatch(typeof(Map), "showElement", typeof(MapElement))]
    internal static class MapElementDiscoverPatch
    {
        [HarmonyPrefix]
        internal static void BeforeShowElement(out bool __state, object[] __args)
        {
            MapElement element = (MapElement)__args[0];
            __state = element != null && element.isOnMap;
        }

        [HarmonyPostfix]
        internal static void AfterShowElement(bool __state, object[] __args)
        {
            MapElement element = (MapElement)__args[0];

            if (element == null) return;
            if (__state) return; // already discovered, not a new discovery
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            MultiplayerMapManager.OnElementDiscovered(element);
        }
    }

    [HarmonyPatch(typeof(Map))]
    internal static class MapMarkerPatches
    {
        [HarmonyPostfix, HarmonyPatch("open")]
        internal static void OnOpen(Map __instance)
        {
            // The party board is co-op only (MapPinView checks the session); offline the vanilla map is untouched.
            MapPinView.OnMapOpen(__instance);
        }

        [HarmonyPostfix, HarmonyPatch("close")]
        internal static void OnClose()
        {
            // Not gated: close is idempotent cleanup, so pins drawn while in a session are removed
            // even if the session ended while the map was open.
            MapPinView.OnMapClose();
        }

        [HarmonyPostfix, HarmonyPatch("Update")]
        internal static void OnUpdate(Map __instance)
        {
            MapPinView.OnMapUpdate(__instance);
        }
    }
}
