using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static class MultiplayerMapManager
    {
        // World-position lists for persistence across map open/close cycles
        public static readonly List<Vector3> LocalMarkers = new List<Vector3>();
        public static readonly Dictionary<int, List<Vector3>> RemoteMarkers = new Dictionary<int, List<Vector3>>();

        // Track spawned GameObjects so we can destroy specific markers on deletion
        private static readonly List<GameObject> _localMarkerObjects = new List<GameObject>();
        private static readonly Dictionary<int, List<GameObject>> _remoteMarkerObjects = new Dictionary<int, List<GameObject>>();

        private static GameObject _clickPlane;

        // ~15 screen-pixel tolerance for marker hover/deletion, multiplied by map scale
        private const float PixelTolerance = 15f;

        public static void OnMapOpen(Map map)
        {
            _clickPlane = new GameObject("MultiplayerMapClickPlane");
            _clickPlane.layer = 17;
            _clickPlane.transform.position = new Vector3(0f, 5f, 0f);
            var bc = _clickPlane.AddComponent<BoxCollider>();
            bc.size = new Vector3(5000f, 1f, 5000f);
            bc.isTrigger = true;

            RenderMarkers(map);
        }

        public static void OnMapClose()
        {
            if (_clickPlane != null)
            {
                Object.Destroy(_clickPlane);
                _clickPlane = null;
            }
            // Destroy marker GameObjects — they'll be re-created on next open
            ClearMarkerObjects();
        }

        private static void ClearMarkerObjects()
        {
            foreach (var go in _localMarkerObjects)
                if (go != null) Object.Destroy(go);
            _localMarkerObjects.Clear();

            foreach (var kvp in _remoteMarkerObjects)
            {
                foreach (var go in kvp.Value)
                    if (go != null) Object.Destroy(go);
            }
            _remoteMarkerObjects.Clear();
        }

        public static void OnMapUpdate(Map map)
        {
            if (!map.opened) return;

            Vector3 worldPos = GetClickWorldPos();
            int hoverIndex = (worldPos != Vector3.zero) ? FindLocalMarkerAtWorldPos(worldPos) : -1;
            UpdateMarkerHover(hoverIndex);

            if (Input.GetMouseButtonDown(1))
            {
                if (worldPos == Vector3.zero) return;

                // Check if right-click hit an existing local marker → delete it
                if (hoverIndex >= 0)
                {
                    RemoveLocalMarkerAt(hoverIndex);
                    return;
                }

                // Otherwise place a new local marker
                LocalMarkers.Add(worldPos);
                GameObject go = AddMarkerElement(map, worldPos, Color.blue);
                if (go != null)
                    _localMarkerObjects.Add(go);
                SendMarkerMessage(worldPos);
            }
        }

        private static void UpdateMarkerHover(int hoveredIndex)
        {
            for (int i = 0; i < _localMarkerObjects.Count; i++)
            {
                if (_localMarkerObjects[i] == null) continue;
                tk2dBaseSprite sprite = _localMarkerObjects[i].GetComponent<tk2dBaseSprite>();
                if (sprite == null) continue;
                sprite.color = (i == hoveredIndex) ? Color.yellow : Color.blue;
            }
        }

        private static float GetMapScale()
        {
            Map map = Map.Instance;
            if (map == null) return 1f;
            var currentType = Traverse.Create(map).Method("getCurrentType").GetValue<Map.Type>();
            return currentType?.scale ?? 1f;
        }

        private static int FindLocalMarkerAtWorldPos(Vector3 worldPos)
        {
            float radius = PixelTolerance * GetMapScale();
            float sqrRadius = radius * radius;
            for (int i = 0; i < LocalMarkers.Count; i++)
            {
                if ((LocalMarkers[i] - worldPos).sqrMagnitude < sqrRadius)
                    return i;
            }
            return -1;
        }

        public static void AddRemoteMarker(int playerId, Vector3 worldPos)
        {
            if (!RemoteMarkers.TryGetValue(playerId, out var markers))
            {
                markers = new List<Vector3>();
                RemoteMarkers[playerId] = markers;
            }
            markers.Add(worldPos);

            Map map = Map.Instance;
            if (map != null && map.opened)
            {
                GameObject go = AddMarkerElement(map, worldPos, Color.green);
                if (go != null)
                {
                    if (!_remoteMarkerObjects.TryGetValue(playerId, out var objects))
                    {
                        objects = new List<GameObject>();
                        _remoteMarkerObjects[playerId] = objects;
                    }
                    objects.Add(go);
                }
            }
        }

        public static void RemoveLocalMarkerAt(int index)
        {
            if (index < 0 || index >= LocalMarkers.Count) return;

            Vector3 pos = LocalMarkers[index];
            LocalMarkers.RemoveAt(index);

            // Destroy the corresponding GameObject
            if (index < _localMarkerObjects.Count && _localMarkerObjects[index] != null)
                Object.Destroy(_localMarkerObjects[index]);
            _localMarkerObjects.RemoveAt(index);

            // Notify remote
            SendMarkerRemoveMessage(pos);
        }

        public static void RemoveRemoteMarker(int playerId, Vector3 worldPos)
        {
            if (!RemoteMarkers.TryGetValue(playerId, out var markers)) return;

            float radius = PixelTolerance * GetMapScale();
            float sqrRadius = radius * radius;
            for (int i = 0; i < markers.Count; i++)
            {
                if ((markers[i] - worldPos).sqrMagnitude < sqrRadius)
                {
                    markers.RemoveAt(i);

                    if (_remoteMarkerObjects.TryGetValue(playerId, out var objects)
                        && i < objects.Count && objects[i] != null)
                        Object.Destroy(objects[i]);

                    if (_remoteMarkerObjects.TryGetValue(playerId, out var objList))
                        objList.RemoveAt(i);

                    if (markers.Count == 0)
                    {
                        RemoteMarkers.Remove(playerId);
                        _remoteMarkerObjects.Remove(playerId);
                    }
                    return;
                }
            }
        }

        public static void Reset()
        {
            ClearMarkerObjects();
            LocalMarkers.Clear();
            RemoteMarkers.Clear();
            _remoteMarkerObjects.Clear();
            if (_clickPlane != null)
            {
                Object.Destroy(_clickPlane);
                _clickPlane = null;
            }
        }

        private static Vector3 GetClickWorldPos()
        {
            Camera uiCam = Core.CamUI?.GetComponent<Camera>();
            if (uiCam == null) return Vector3.zero;

            Ray ray = uiCam.ScreenPointToRay(Core.cursorPos());
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, 2000f, 131072))
                return Vector3.zero;

            return HitPointToWorldPos(hit.point);
        }

        private static Vector3 HitPointToWorldPos(Vector3 hitPoint)
        {
            Map map = Map.Instance;
            if (map == null) return Vector3.zero;

            var currentType = Traverse.Create(map).Method("getCurrentType").GetValue<Map.Type>();
            if (currentType == null) return Vector3.zero;

            Vector3 vector = Vector3.zero;
            if (currentType.name != "World")
            {
                if (Core.randomGeneration && Singleton<OutsideLocations>.Instance != null
                    && Singleton<OutsideLocations>.Instance.spawnedLocations.ContainsKey(currentType.name))
                {
                    vector = Singleton<OutsideLocations>.Instance.spawnedLocations[currentType.name].transform.position;
                }
            }

            // Convert click-plane hit point to game-world position.
            // The iconHolder's world position changes as the user scrolls/drags the map.
            // Each child element's local position in iconHolder is:
            //   localPos = (worldPos - offset) / scale + (0, 5, 0)
            // Clicking the plane at hitPoint gives a world position; subtract iconHolder's
            // current world position to get the equivalent local position, then reverse the formula.
            if (map.iconHolder == null)
                return Vector3.zero;

            Vector3 iconHolderPos = map.iconHolder.transform.position;
            Vector3 unscaled = hitPoint - iconHolderPos - new Vector3(0f, 5f, 0f);
            return new Vector3(
                unscaled.x * currentType.scale + vector.x,
                0f,
                unscaled.z * currentType.scale + vector.z
            );
        }

        private static void RenderMarkers(Map map)
        {
            var currentType = Traverse.Create(map).Method("getCurrentType").GetValue<Map.Type>();
            if (currentType == null) return;

            foreach (Vector3 marker in LocalMarkers)
            {
                GameObject go = AddMarkerElement(map, marker, Color.blue, currentType);
                if (go != null) _localMarkerObjects.Add(go);
            }
            foreach (var kvp in RemoteMarkers)
            {
                int playerId = kvp.Key;
                foreach (Vector3 marker in kvp.Value)
                {
                    GameObject go = AddMarkerElement(map, marker, Color.green, currentType);
                    if (go != null)
                    {
                        if (!_remoteMarkerObjects.TryGetValue(playerId, out var objects))
                        {
                            objects = new List<GameObject>();
                            _remoteMarkerObjects[playerId] = objects;
                        }
                        objects.Add(go);
                    }
                }
            }
        }

        private static GameObject AddMarkerElement(Map map, Vector3 worldPos, Color color)
        {
            var currentType = Traverse.Create(map).Method("getCurrentType").GetValue<Map.Type>();
            if (currentType == null) return null;
            return AddMarkerElement(map, worldPos, color, currentType);
        }

        private static GameObject AddMarkerElement(Map map, Vector3 worldPos, Color color, Map.Type currentType)
        {
            if (map.iconHolder == null)
                return null;

            Vector3 vector = Vector3.zero;
            if (currentType.name != "World")
            {
                if (Core.randomGeneration && Singleton<OutsideLocations>.Instance != null
                    && Singleton<OutsideLocations>.Instance.spawnedLocations.ContainsKey(currentType.name))
                {
                    vector = Singleton<OutsideLocations>.Instance.spawnedLocations[currentType.name].transform.position;
                }
            }

            Vector3 pos = (worldPos - vector) / currentType.scale + new Vector3(0f, 5f, 0f);
            GameObject go = Core.AddPrefab("UI/UIMapElement", pos, Quaternion.Euler(90f, 0f, 0f), map.iconHolder);
            if (go == null) return null;

            go.transform.localScale = Vector3.one;

            tk2dBaseSprite sprite = go.GetComponent<tk2dBaseSprite>();
            if (sprite == null) return null;

            if (sprite.Collection != null && sprite.Collection.GetSpriteDefinition("generic_marker") != null)
                sprite.SetSprite("generic_marker");

            // Shrink the sprite further to make it a compact dot-like marker
            go.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            sprite.color = color;

            sprite.transform.position = new Vector3(
                (int)sprite.transform.position.x + currentType.iconOffset.x * Core.ResolutionWidthModifier,
                (int)sprite.transform.position.y,
                (int)sprite.transform.position.z + currentType.iconOffset.y * Core.ResolutionHeightModifier
            );

            return go;
        }

        public static void OnElementDiscovered(MapElement element)
        {
            if (element == null || string.IsNullOrEmpty(element.elementName))
                return;
            if (element.isWorldChunk || element.isDeathDrop)
                return;
            // Applying a remote discovery must not re-broadcast (loop).
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            var msg = new MapElementDiscoveredMessage { ElementName = element.elementName };
            net.Broadcast(NetMessageType.MapElementDiscovered, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[MapDiscovery] discovered '{element.elementName}' — broadcast to remote");
        }

        public static void OnRemoteElementDiscovered(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return;
            Map map = Map.Instance;
            if (map == null) return;

            map.showElement(elementName);
            ModRuntime.LegacyInfo($"[MapDiscovery] remote discovered '{elementName}' — showing locally");
        }

        private static void SendMarkerMessage(Vector3 worldPos)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            var msg = new MapMarkerMessage
            {
                PosX = worldPos.x,
                PosY = worldPos.y,
                PosZ = worldPos.z,
                PlayerId = net.LocalPlayerId
            };

            net.Broadcast(NetMessageType.MapMarker, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        private static void SendMarkerRemoveMessage(Vector3 worldPos)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            var msg = new MapMarkerRemoveMessage
            {
                PosX = worldPos.x,
                PosY = worldPos.y,
                PosZ = worldPos.z,
                PlayerId = net.LocalPlayerId
            };

            net.Broadcast(NetMessageType.MapMarkerRemove, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
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
            MultiplayerMapManager.OnMapOpen(__instance);
        }

        [HarmonyPostfix, HarmonyPatch("close")]
        internal static void OnClose()
        {
            MultiplayerMapManager.OnMapClose();
        }

        [HarmonyPostfix, HarmonyPatch("Update")]
        internal static void OnUpdate(Map __instance)
        {
            MultiplayerMapManager.OnMapUpdate(__instance);
        }
    }
}
