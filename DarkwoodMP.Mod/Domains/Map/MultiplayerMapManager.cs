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
            _announced.Clear();
            _announcedMap = null;
            _nextScanAt = 0f;
        }

        // ---- discovery scan ----------------------------------------------------------------
        // Vanilla reveals a piece through Map.showElement(MapElement), a one-line setter the
        // runtime inlines into its callers (showElementsCloseToPlayer as the player walks,
        // Location.discoverMe, a bought map's markLocationsOnMap), so the patch on it ran for a
        // handful of pieces a session: most trails, places and map-item locations stayed on the
        // finder's map only. Every few seconds the pieces on this map are compared with the ones
        // already announced, and each new one goes to the others, whatever revealed it.

        private static readonly HashSet<MapElement> _announced = new HashSet<MapElement>(); // reset-in: Reset (and a new Map)
        private static Map _announcedMap; // reset-in: Reset
        private static float _nextScanAt; // reset-in: Reset
        private const float ScanInterval = 2f;
        private const int MaxBroadcastPerScan = 64;

        /// <summary>A piece this machine shows because another player found it: not announced back.</summary>
        internal static void NoteKnown(MapElement element)
        {
            if (element != null)
                _announced.Add(element);
        }

        /// <summary>Network tick: announce pieces revealed on this map since the last scan.</summary>
        internal static void ScanForDiscoveries()
        {
            float now = Time.unscaledTime;
            if (now < _nextScanAt)
                return;
            _nextScanAt = now + ScanInterval;
            if (!NetGuard.Connected(out var net) || GameScreen.AtTitle || Core.loadingGame)
                return;
            if (net.Role == NetworkRole.Client && !LanNetworkManager.ClientCanApplyWorldBulk())
                return;
            Map map = Map.Instance;
            if (map == null)
                return;
            // A new world (load, world share): the first scan takes what is already shown. A
            // client tells the host what it found before (the host shares its own map at the
            // join); the host's map already reached everyone with the world.
            bool baseline = !ReferenceEquals(map, _announcedMap);
            if (baseline)
            {
                _announced.Clear();
                _announcedMap = map;
            }
            bool sendBaseline = net.Role == NetworkRole.Client;
            List<MapElement> all = CollectMapElements();
            int sent = 0;
            for (int i = 0; i < all.Count && sent < MaxBroadcastPerScan; i++)
            {
                MapElement e = all[i];
                if (e == null || !e.isOnMap || _announced.Contains(e))
                    continue;
                if (baseline && !sendBaseline)
                {
                    _announced.Add(e);
                    continue;
                }
                if (Broadcast(net, e))
                    sent++;
            }
        }

        public static void OnElementDiscovered(MapElement element)
        {
            // Applying a remote discovery must not re-broadcast (loop).
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
            {
                NoteKnown(element);
                return;
            }
            if (!NetGuard.Connected(out var net)) return;
            Broadcast(net, element);
        }

        private static bool Broadcast(LanNetworkManager net, MapElement element)
        {
            if (element == null || !_announced.Add(element))
                return false;
            if (string.IsNullOrEmpty(element.elementName) || element.isWorldChunk || element.isDeathDrop)
                return false;
            // A pin on an item (the Navigator skill's meat marker) is that player's own and does
            // not exist on other machines; broadcasting it left each peer rescanning for 5 minutes.
            if (element.GetComponent<Item>() != null)
                return false;
            // A piece of a personal pad (the host's prologue, a solo dream) is not the others' map.
            if (PersonalPads.Contains(element.transform))
                return false;

            var msg = new MapElementDiscoveredMessage { Piece = Describe(element) };
            net.Broadcast(NetMessageType.MapElementDiscovered, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[MapDiscovery] discovered {PieceText(msg.Piece)} — broadcast to remote");
            return true;
        }

        public static void OnRemoteElementDiscovered(MapPieceWire piece)
        {
            if (string.IsNullOrEmpty(piece.Name)) return;
            if (!TryApplyRemoteDiscovery(piece))
                QueuePendingDiscovery(piece);
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
