using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies remote player-built world changes (send side: Build_Patch).
///
/// Placed items: replays the game's own placement - the item's world prefab
/// (InvItem.item from the ItemsDatabase) is instantiated at the placement
/// spot, marked as player-set and registered as saveable, exactly like
/// Player.progressBarCompleted does locally.
///
/// Constructions: replays Constructible.construct(manual=false, option) - the
/// same ingredient-free path save-loading uses.
///
/// Placements made during the session are remembered and re-sent in the
/// host's join snapshot, so late joiners see traps placed before they joined
/// (receive side dedupes by name+position).
/// </summary>
public class BuildSync
{
    private readonly NetworkLayer _network;

    private class Placement
    {
        public string ItemType;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    // All placements this session (local + applied remote) for the join snapshot
    private readonly List<Placement> _placements = new();

    // Constructions that could not be applied yet (area not loaded)
    private readonly Dictionary<string, int> _pendingConstructs = new();
    private const float PendingRetryInterval = 5f;
    private float _lastPendingRetry;

    public BuildSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnUpdate()
    {
        if (_pendingConstructs.Count == 0 && _pendingBurns.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;

        if (_pendingConstructs.Count > 0)
        {
            List<string> applied = null;
            foreach (var kvp in _pendingConstructs)
            {
                if (TryApplyConstruct(kvp.Key, kvp.Value))
                    (applied ??= new List<string>()).Add(kvp.Key);
            }
            if (applied != null)
                foreach (var id in applied)
                    _pendingConstructs.Remove(id);
        }

        for (var i = _pendingBurns.Count - 1; i >= 0; i--)
        {
            if (Time.time > _pendingBurns[i].Expires || TryBurnLiquidAt(_pendingBurns[i].Position))
                _pendingBurns.RemoveAt(i);
        }
    }

    public void Reset()
    {
        _placements.Clear();
        _pendingConstructs.Clear();
        _pendingBurns.Clear();
        _sessionConstructs.Clear();
    }

    // ------------------------------------------------------------------
    // Placed items
    // ------------------------------------------------------------------

    /// <summary>Remember a placement we made ourselves (for the join snapshot).</summary>
    public void RecordLocalPlacement(string itemType, Vector3 pos, Quaternion rot)
    {
        _placements.Add(new Placement { ItemType = itemType, Position = pos, Rotation = rot });
    }

    public void OnRemotePlaced(string itemType, Vector3 pos, Quaternion rot)
    {
        try
        {
            var db = UnityEngine.Object.FindObjectOfType(typeof(ItemsDatabase)) as ItemsDatabase;
            var invItem = db != null ? db.getItem(itemType, false) : null;
            if (invItem == null || invItem.item == null)
            {
                ModLogger.Warning($"[BuildSync] No world prefab for placed item '{itemType}'");
                return;
            }

            // Duplicate placement (re-sent snapshot after a rejoin)?
            var prefabName = invItem.item.name;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Item)))
            {
                if (obj is not Component existing) continue;
                if ((existing.transform.position - pos).sqrMagnitude > 0.5f * 0.5f) continue;
                var existingName = existing.gameObject.name;
                if (existingName.StartsWith(prefabName))
                {
                    ModLogger.Msg($"[BuildSync] Placement '{itemType}' already present - skipped");
                    return;
                }
            }

            RemoteApply.Active = true;
            try
            {
                // Mirror Player.progressBarCompleted's placement sequence
                var go = Core.AddPrefab(invItem.item, pos, rot, null, false);
                if (go == null) return;

                var trigger = go.GetComponent<Trigger>();
                if (trigger != null)
                    trigger.setByPlayer = true;

                Core.addToSaveable(go, true, true);
                WorldGrid.Instance.registerToNode(go, (WorldGrid.Cullable.Type)0);
            }
            finally
            {
                RemoteApply.Active = false;
            }

            _placements.Add(new Placement { ItemType = itemType, Position = pos, Rotation = rot });
            ModLogger.Msg($"[BuildSync] Remote player placed '{itemType}' at {pos:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BuildSync] Failed to place '{itemType}': {ex.Message}");
        }
    }

    // Constructions finished this session, for the join snapshot (v0.5). The
    // Constructible component destroys itself after building, so a live scan
    // is impossible - this history is the only record. Receive side is
    // idempotent (constructed check) and pends until the area loads.
    private readonly Dictionary<string, int> _sessionConstructs = new();

    /// <summary>Remember a finished construction (local builds and applied remote ones).</summary>
    public void RecordConstruct(string objectId, int option)
    {
        _sessionConstructs[objectId] = option;
    }

    /// <summary>
    /// Order-independent digest of the session build history for the desync
    /// check (v0.6). Constructs hash their full id; placements only their
    /// item type - the recorded positions differ in float noise between the
    /// original float and the F2-serialized copy, which would produce
    /// permanent false positives.
    /// </summary>
    public void GetDigest(out int count, out uint hash)
    {
        count = _sessionConstructs.Count + _placements.Count;
        hash = 0;
        unchecked
        {
            foreach (var kvp in _sessionConstructs) hash += SyncCheck.Fnv1a("c:" + kvp.Key + "=" + kvp.Value);
            foreach (var p in _placements) hash += SyncCheck.Fnv1a("pl:" + p.ItemType);
        }
    }

    /// <summary>Host: re-announce all session placements for a new joiner.</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        var playerId = Math.Max(_network.LocalClientId, 0);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var p in _placements)
        {
            into.Add(new BuildPlacedPacket
            {
                PlayerId = playerId,
                ItemType = p.ItemType,
                X = p.Position.x, Y = p.Position.y, Z = p.Position.z,
                Rx = p.Rotation.x, Ry = p.Rotation.y, Rz = p.Rotation.z, Rw = p.Rotation.w
            });
        }
        foreach (var kvp in _sessionConstructs)
        {
            into.Add(new BuildConstructPacket
            {
                PlayerId = playerId,
                ObjectId = kvp.Key,
                Option = kvp.Value
            });
        }
    }

    /// <summary>Join bulk: live gasoline liquids (Horde SendGasStateTo, cap 256).</summary>
    public void CollectGasSnapshot(List<Packets.Packet> into)
    {
        if (into == null) return;
        try
        {
            var liquids = UnityEngine.Object.FindObjectsOfType(typeof(Liquid));
            var playerId = Math.Max(_network.LocalClientId, 0);
            int n = 0;
            foreach (var obj in liquids)
            {
                if (n >= 256) break;
                if (obj is not Liquid liq) continue;
                var pos = liq.transform.position;
                var rot = liq.transform.rotation;
                into.Add(new GasTrailPacket
                {
                    PlayerId = playerId,
                    X = pos.x, Y = pos.y, Z = pos.z,
                    Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w
                });
                try
                {
                    if (liq.burning)
                    {
                        into.Add(new BurnLiquidPacket
                        {
                            PlayerId = playerId,
                            X = pos.x, Z = pos.z
                        });
                    }
                }
                catch { }
                n++;
            }
        }
        catch (Exception ex) { ModLogger.Error($"[BuildSync] CollectGasSnapshot: {ex.Message}"); }
    }

    // ------------------------------------------------------------------
    // Gasoline trails & burning liquids
    // ------------------------------------------------------------------

    // Burn events whose liquid hasn't spawned here yet (segment packet raced)
    private readonly List<PendingBurn> _pendingBurns = new();

    // One whole-scene Liquid scan per frame at most, shared by every gas
    // event in a burst (trail pours and ignitions arrive many per frame)
    private UnityEngine.Object[] _liquidSnapshot;
    private int _liquidSnapshotFrame = -1;

    private UnityEngine.Object[] LiquidSnapshot()
    {
        if (_liquidSnapshotFrame != Time.frameCount)
        {
            _liquidSnapshotFrame = Time.frameCount;
            _liquidSnapshot = UnityEngine.Object.FindObjectsOfType(typeof(Liquid));
        }
        return _liquidSnapshot;
    }

    private class PendingBurn
    {
        public Vector3 Position;
        public float Expires;
    }

    /// <summary>Remote player poured a gasoline trail segment.</summary>
    public void OnRemoteGasTrail(Vector3 pos, Quaternion rot)
    {
        try
        {
            // Duplicate segment (packet re-send)? Liquids only exist where poured.
            foreach (var obj in LiquidSnapshot())
            {
                if (obj is Component existing && (existing.transform.position - pos).sqrMagnitude < 0.15f * 0.15f)
                    return;
            }

            RemoteApply.Active = true;
            try
            {
                Core.AddPrefab("Items/GasolineTrail", pos, rot, null, false);
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BuildSync] Failed to spawn gas trail: {ex.Message}");
        }
    }

    /// <summary>Remote liquid caught fire - ignite our matching puddle.</summary>
    public void OnRemoteBurnLiquid(Vector3 pos)
    {
        // Seed the spread suppressor: everything our own fire now chains to
        // is a mirror of the partner's spread and must NOT broadcast back
        // (the echo storm was half of the "igniting gas" lag).
        Patches.Gasoline_Patch.RecordBurn(pos);
        if (TryBurnLiquidAt(pos)) return;
        _pendingBurns.Add(new PendingBurn { Position = pos, Expires = Time.time + 30f });
    }

    private bool TryBurnLiquidAt(Vector3 pos)
    {
        try
        {
            Liquid best = null;
            var bestDist = 2f * 2f;
            foreach (var obj in LiquidSnapshot())
            {
                if (obj is not Liquid liquid || liquid.burning) continue;
                var p = liquid.transform.position;
                var dx = p.x - pos.x;
                var dz = p.z - pos.z;
                var d = dx * dx + dz * dz;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = liquid;
                }
            }
            if (best == null) return false;

            RemoteApply.Active = true;
            try
            {
                best.startBurning();
            }
            finally
            {
                RemoteApply.Active = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BuildSync] Failed to ignite liquid: {ex.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------
    // Constructions
    // ------------------------------------------------------------------

    public void OnRemoteConstruct(string objectId, int option)
    {
        RecordConstruct(objectId, option); // session history for late joiners
        if (TryApplyConstruct(objectId, option))
            _pendingConstructs.Remove(objectId);
        else
            _pendingConstructs[objectId] = option;
    }

    private bool TryApplyConstruct(string objectId, int option)
    {
        try
        {
            var target = FindConstructible(objectId);
            if (target == null) return false;

            if (target.constructed) return true; // already done (idempotent)

            RemoteApply.Active = true;
            try
            {
                // manual=false: no ingredient consumption (save-load path)
                target.construct(false, option);
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[BuildSync] Applied remote construction '{objectId}' (option {option})");
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BuildSync] Failed to construct '{objectId}': {ex.Message}");
            return true; // don't retry a throwing target forever
        }
    }

    private static Constructible FindConstructible(string objectId)
    {
        var at = objectId.LastIndexOf('@');
        var name = at > 0 ? objectId.Substring(0, at) : null;
        float x = 0f, z = 0f;
        var hasCoords = false;
        if (at > 0)
        {
            var coords = objectId.Substring(at + 1).Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            hasCoords = coords.Length == 2
                && float.TryParse(coords[0], System.Globalization.NumberStyles.Float, inv, out x)
                && float.TryParse(coords[1], System.Globalization.NumberStyles.Float, inv, out z);
        }

        Constructible best = null;
        var bestDist = 10f * 10f;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Constructible)))
        {
            if (obj is not Constructible c) continue;
            if (GameIds.ForComponent(c) == objectId) return c;

            if (!hasCoords || c.gameObject.name != name) continue;
            var p = c.transform.position;
            var dx = p.x - x;
            var dz = p.z - z;
            var d = dx * dx + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }
}
