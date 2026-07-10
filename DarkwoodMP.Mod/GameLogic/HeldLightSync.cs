using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Shows the light of a remote player's active light item (torch, flashlight,
/// lighter, held flare). The send side is ItemActive_Patch on
/// InvItemClass.switchActive; here the item's own lightEmitter prefab (from
/// the game's ItemsDatabase) is instantiated under the remote player clone -
/// the real torch/flashlight light, flicker included.
/// </summary>
public class HeldLightSync
{
    private class RemoteLight
    {
        public string ItemType;
        public GameObject Emitter;      // spawned instance (null while pending)
        public bool Active;
    }

    private readonly Dictionary<int, RemoteLight> _lights = new();
    private const float RetryInterval = 1f;
    private float _lastRetry;

    // A partner's live flares: a light per flare id that FOLLOWS the flare (so a
    // thrown flare's light travels to where it lands). Keyed "senderId:instanceId".
    private class RemoteFlare { public GameObject Obj; public float LastSeen; }
    private readonly Dictionary<string, RemoteFlare> _remoteFlares = new();
    private static readonly List<string> _flareStale = new();
    private float _lastFlareBroadcast;
    private static System.Reflection.MethodInfo _lightCreate;

    /// <summary>Sender: broadcast every locally-owned flare's current position (called from OnUpdate).</summary>
    private void BroadcastLocalFlares()
    {
        if (Patches.Flare_Patch.Active.Count == 0) return;
        if (Time.time - _lastFlareBroadcast < 0.25f) return;
        _lastFlareBroadcast = Time.time;

        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;
        var list = Patches.Flare_Patch.Active;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            var f = list[i];
            if (f == null || f.dead) { list.RemoveAt(i); continue; }
            var pos = f.transform.position;
            network.Send(new FlarePosPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                InstanceId = f.GetInstanceID(),
                X = pos.x, Y = pos.y, Z = pos.z,
                Life = f.longevity > 0f ? f.longevity : 90f
            });
        }
    }

    /// <summary>Receiver: a partner flare's position - create/move a light that follows it.</summary>
    public void OnRemoteFlarePos(int senderId, int instanceId, Vector3 pos, float life)
    {
        var key = senderId + ":" + instanceId;
        if (!_remoteFlares.TryGetValue(key, out var rf) || rf.Obj == null)
        {
            var mirror = CreateFlareMirror(pos, life);
            if (mirror == null) return;
            rf = new RemoteFlare { Obj = mirror };
            _remoteFlares[key] = rf;
        }
        rf.Obj.transform.position = pos;
        rf.LastSeen = Time.time;
    }

    /// <summary>
    /// Spawn the game's REAL flare prefab (InvItem.item) as a purely cosmetic
    /// mirror: physics frozen, pickup/throw components stripped, so what
    /// remains is the authentic Flare light + flicker + particles. The old
    /// reflection-built Light2D never rendered reliably ("flares don't light
    /// up"). Falls back to the reflection light if the prefab is unavailable.
    /// </summary>
    private static GameObject CreateFlareMirror(Vector3 pos, float life)
    {
        try
        {
            var db = UnityEngine.Object.FindObjectOfType(typeof(ItemsDatabase)) as ItemsDatabase;
            var invItem = db != null ? (db.getItem("flare", false) ?? db.getItem("Flare", false)) : null;
            var prefab = invItem != null ? invItem.item : null;
            if (prefab != null)
            {
                var go = Core.AddPrefab(prefab, pos, Quaternion.Euler(90f, 0f, 0f), null);
                if (go != null)
                {
                    // Never a world object: our machine must not pick it up,
                    // collide with it, throw it - or rebroadcast it (the marker
                    // makes Flare_Patch skip its Start).
                    RemoteApply.MarkRemoteSpawned(go);
                    foreach (var col in go.GetComponentsInChildren<Collider>(true))
                        col.enabled = false;
                    var body = go.GetComponent<Rigidbody>();
                    if (body != null) body.isKinematic = true;
                    foreach (var comp in go.GetComponentsInChildren<Item>(true))
                        UnityEngine.Object.Destroy(comp);
                    foreach (var comp in go.GetComponentsInChildren<ThrownItem>(true))
                        UnityEngine.Object.Destroy(comp);
                    foreach (var comp in go.GetComponentsInChildren<Inventory>(true))
                        UnityEngine.Object.Destroy(comp);

                    // Burn as long as the sender says it will (Start reads this)
                    var flare = go.GetComponentInChildren<Flare>(true);
                    if (flare != null && life > 0f) flare.longevity = life;

                    go.name = "DWMP_RemoteFlare";
                    ModLogger.Msg($"[HeldLightSync] flare mirror spawned at {pos:F1} (life {life:F0}s)");
                    return go;
                }
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[HeldLightSync] flare mirror: {ex.Message}");
        }

        // Prefab unavailable - reflection-built light is better than darkness
        var fallback = CreateFlareLight(pos);
        return fallback != null ? fallback.gameObject : null;
    }

    /// <summary>
    /// Create a registered, rendering flare light via the game's OWN factory
    /// (Light2D.Create). Cloning Player.lightDot produced an unregistered light
    /// that never rendered. Enums are nested/namespaced, so invoke by reflection.
    /// </summary>
    private static Light2D CreateFlareLight(Vector3 pos)
    {
        try
        {
            if (_lightCreate == null)
            {
                foreach (var m in typeof(Light2D).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "Create") continue;
                    var ps0 = m.GetParameters();
                    if (ps0.Length == 7 && ps0[1].ParameterType == typeof(Color)) { _lightCreate = m; break; }
                }
                if (_lightCreate == null) { ModLogger.Warning("[HeldLightSync] Light2D.Create not found"); return null; }
            }

            var ps = _lightCreate.GetParameters();
            var detail = Enum.ToObject(ps[4].ParameterType, 96); // Rays_100
            var type = Enum.ToObject(ps[6].ParameterType, 0);    // Radial
            var light = _lightCreate.Invoke(null,
                new object[] { pos, new Color(1f, 0.55f, 0.25f), 15f, 360, detail, true, type }) as Light2D;
            if (light == null) { ModLogger.Warning("[HeldLightSync] Light2D.Create returned null"); return null; }
            light.lightIntensity = 2.2f;
            light.gameObject.name = "DWMP_RemoteFlare";
            if (!light.gameObject.activeSelf) light.gameObject.SetActive(true);
            if (!light.enabled) light.enabled = true;
            UnityEngine.Object.DontDestroyOnLoad(light.gameObject);
            ModLogger.Msg($"[HeldLightSync] created flare light at {pos:F1}");
            return light;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[HeldLightSync] flare light: {ex.Message}");
            return null;
        }
    }

    /// <summary>Remote player toggled a light item.</summary>
    public void OnRemoteItemLight(int playerId, string itemType, bool active)
    {
        if (!_lights.TryGetValue(playerId, out var light))
        {
            light = new RemoteLight();
            _lights[playerId] = light;
        }

        // Turning something off only matters if it is the light we show
        if (!active && light.ItemType != itemType && light.Emitter != null) return;

        light.ItemType = itemType;
        light.Active = active;

        if (!active)
        {
            DestroyEmitter(light);
            return;
        }

        // Different item than the current emitter - replace
        DestroyEmitter(light);
        TrySpawn(playerId, light);
    }

    /// <summary>Called every frame; retries lights whose player object wasn't ready.</summary>
    public void OnUpdate()
    {
        // Sender: broadcast our own live flares' positions so partners' lights follow.
        BroadcastLocalFlares();

        // Receiver: drop flare lights we haven't heard about for a while (flare
        // burned out / was picked up / we walked away).
        if (_remoteFlares.Count > 0)
        {
            _flareStale.Clear();
            foreach (var kvp in _remoteFlares)
                if (kvp.Value.Obj == null || Time.time - kvp.Value.LastSeen > 2f)
                    _flareStale.Add(kvp.Key);
            foreach (var key in _flareStale)
            {
                if (_remoteFlares[key].Obj != null) UnityEngine.Object.Destroy(_remoteFlares[key].Obj);
                _remoteFlares.Remove(key);
            }
        }

        if (_lights.Count == 0) return;
        if (Time.time - _lastRetry < RetryInterval) return;
        _lastRetry = Time.time;

        foreach (var kvp in _lights)
        {
            var light = kvp.Value;
            if (light.Active && light.Emitter == null)
                TrySpawn(kvp.Key, light);
        }
    }

    public void OnRemotePlayerRemoved(int playerId)
    {
        if (_lights.TryGetValue(playerId, out var light))
            DestroyEmitter(light);
        _lights.Remove(playerId);
    }

    public void Reset()
    {
        foreach (var light in _lights.Values)
            DestroyEmitter(light);
        _lights.Clear();

        foreach (var rf in _remoteFlares.Values)
            if (rf.Obj != null) UnityEngine.Object.Destroy(rf.Obj);
        _remoteFlares.Clear();
        Patches.Flare_Patch.Active.Clear();
    }

    private void TrySpawn(int playerId, RemoteLight light)
    {
        try
        {
            var playerObj = NetworkManager.Instance?.GetRemotePlayer(playerId);
            if (playerObj == null)
            {
                ModLogger.Msg($"[HeldLightSync] Player {playerId} object not ready - retrying");
                return;
            }

            var db = UnityEngine.Object.FindObjectOfType(typeof(ItemsDatabase)) as ItemsDatabase;
            var invItem = db != null ? db.getItem(light.ItemType, false) : null;

            GameObject go = null;
            if (invItem != null && invItem.lightEmitter != null)
            {
                var instance = UnityEngine.Object.Instantiate(invItem.lightEmitter);
                go = instance as GameObject ?? (instance as Component)?.gameObject;
                if (go == null && instance != null)
                    UnityEngine.Object.Destroy(instance);
            }

            // Fallback: the item has no emitter prefab (or the database lookup
            // failed) - clone the local player's own light-dot and widen it to
            // the item's light radius, so the remote player at least glows
            if (go == null)
                go = CreateFallbackLight(light.ItemType, invItem);

            if (go == null)
            {
                ModLogger.Warning($"[HeldLightSync] No light source for '{light.ItemType}' - giving up");
                light.Active = false;
                return;
            }

            // Attach where the game would: the player's item light emitter
            // socket. The clone kept that child; fall back to the root.
            var socket = FindEmitterSocket(playerObj);
            go.transform.SetParent(socket != null ? socket : playerObj.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.name = $"DWMP_HeldLight_{light.ItemType}";

            light.Emitter = go;
            ModLogger.Msg($"[HeldLightSync] Player {playerId}: '{light.ItemType}' light on (socket={(socket != null ? socket.name : "root")})");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[HeldLightSync] Failed to spawn light '{light.ItemType}': {ex.Message}");
            light.Active = false; // don't retry a failing spawn every second
        }
    }

    private static System.Reflection.FieldInfo _lightRadiusField;

    /// <summary>Clone the local player's light-dot Light2D and widen it.</summary>
    private static GameObject CreateFallbackLight(string itemType, InvItem invItem)
    {
        try
        {
            var localPlayer = Player.Instance;
            if (localPlayer == null || localPlayer.lightDot == null) return null;

            var go = UnityEngine.Object.Instantiate(localPlayer.lightDot.gameObject);
            var light2d = go.GetComponent<Light2D>();
            if (light2d != null)
            {
                var radius = invItem != null && invItem.lightRadius > 0f ? invItem.lightRadius : 6f;
                _lightRadiusField ??= typeof(Light2D).GetField("lightRadius",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                _lightRadiusField?.SetValue(light2d, radius);
                light2d.enabled = true;
            }
            ModLogger.Msg($"[HeldLightSync] Using fallback light-dot for '{itemType}'");
            return go;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[HeldLightSync] Fallback light failed: {ex.Message}");
            return null;
        }
    }

    private static Transform FindEmitterSocket(GameObject remotePlayer)
    {
        try
        {
            var localPlayer = Player.Instance;
            var socketName = localPlayer != null && localPlayer.itemLightEmitter != null
                ? localPlayer.itemLightEmitter.gameObject.name
                : null;
            if (string.IsNullOrEmpty(socketName)) return null;

            foreach (var t in remotePlayer.GetComponentsInChildren<Transform>(true))
            {
                if (t.gameObject.name == socketName)
                    return t;
            }
        }
        catch { /* socket lookup is best-effort */ }
        return null;
    }

    private static void DestroyEmitter(RemoteLight light)
    {
        if (light.Emitter != null)
            UnityEngine.Object.Destroy(light.Emitter);
        light.Emitter = null;
    }
}
