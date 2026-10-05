using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// World Door / Window / Item Burn sync (pos-keyed absolute state).
    /// Covers Flame/molotov ignition, Burn.stop, and late-join burning objects.
    /// </summary>
    internal sealed class WorldBurnNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingWorldBurns = 32;
        private readonly List<WorldBurnStateMessage> _pending = new List<WorldBurnStateMessage>();
        private float _nextPendingFlushTime;
        private const float PendingFlushInterval = 1f;

        internal int PendingCount => _pending.Count;

        internal WorldBurnNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPending()
        {
            _pending.Clear();
        }

        internal void HandleWorldBurnState(WorldBurnStateMessage msg)
        {
            ApplyWorldBurnState(msg, queueIfMissing: true);
        }

        internal void ApplyWorldBurnState(WorldBurnStateMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            GameObject go = FindTarget(msg.TargetType, pos);
            if (go == null)
            {
                if (queueIfMissing)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        var p = _pending[i];
                        if (p.TargetType == msg.TargetType &&
                            Mathf.Abs(p.PosX - msg.PosX) < 0.5f &&
                            Mathf.Abs(p.PosY - msg.PosY) < 0.5f &&
                            Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pending.RemoveAt(i);
                    }
                    if (_pending.Count >= MaxPendingWorldBurns)
                        _pending.RemoveAt(0);
                    _pending.Add(msg);
                    ModRuntime.LegacyInfo($"[WorldBurnSync] queued (target not loaded) at {pos}");
                }
                return;
            }

            using (new NetworkApplyGuard())
            {
                bool prevHack = TraverseHack.GetExplicitFlag();
                TraverseHack.SetExplicitFlag(true);
                try
                {
                    if (msg.Burning != 0)
                    {
                        Burn burn = go.GetComponent<Burn>();
                        if (burn == null)
                        {
                            burn = go.AddComponent<Burn>();
                            float remain = msg.HasRemainingTime && msg.RemainingTime > 0f
                                ? msg.RemainingTime
                                : WorldBurnSyncHelpers.DefaultBurnTime;
                            burn.burnTime = remain;
                            WorldBurnSyncHelpers.MarkRemoteApplied(burn);
                        }
                    }
                    else
                    {
                        Burn burn = go.GetComponent<Burn>();
                        if (burn != null)
                            burn.stop();
                    }
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[WorldBurnSync] apply: " + ex.Message);
                }
                finally
                {
                    TraverseHack.SetExplicitFlag(prevHack);
                }
            }

            ModRuntime.LegacyInfo(
                $"[WorldBurnSync] applied type={msg.TargetType} burning={msg.Burning != 0} at {pos}");
        }

        internal void TryFlushPending()
        {
            if (_pending.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextPendingFlushTime) return;
            _nextPendingFlushTime = now + PendingFlushInterval;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var msg = _pending[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (FindTarget(msg.TargetType, pos) == null)
                    continue;
                _pending.RemoveAt(i);
                ApplyWorldBurnState(msg, queueIfMissing: false);
            }
        }

        /// <summary>
        /// Host: push currently burning Door/Window/Item Burn components to a joiner.
        /// </summary>
        internal void SendWorldBurnStatesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            int sent = SendWorldBurnStatesFiltered(targetPlayerId, null, Vector3.zero, 0f, 256);
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} world-burn state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} world-burn state(s) to all clients");
        }

        /// <summary>
        /// Host→peer: burning Door/Window/Item under/near the pad. Pending burn
        /// queue is FIFO-capped at 32 and misses virgin-pad targets.
        /// </summary>
        internal int SendWorldBurnStatesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = WorldLateJoinNetHandlers.PadResyncMaxDistSqr;
            return SendWorldBurnStatesFiltered(targetPlayerId, root, anchor, maxDistSqr, 64);
        }

        private int SendWorldBurnStatesFiltered(
            int targetPlayerId, Transform root, Vector3 anchor, float maxDistSqr, int maxSend)
        {
            bool padScoped = root != null || maxDistSqr > 0f;
            // Late-join path passes maxDistSqr=0 with null root → unscoped.
            if (root == null && maxDistSqr <= 0f)
                padScoped = false;

            Burn[] all = WorldQueryHelper.GetCachedSceneComponents<Burn>();
            int sent = 0;
            for (int i = 0; i < all.Length && sent < maxSend; i++)
            {
                Burn burn = all[i];
                if (burn == null || burn.transform == null) continue;
                // The host's own prologue pads are not the world.
                if (PersonalPrologue.IsOnProloguePad(burn.transform)) continue;
                if (!WorldBurnSyncHelpers.TryResolveWorldTarget(burn, out _, out _))
                    continue;
                if (padScoped && !IsUnderOrNearLocation(burn.transform, root, anchor, maxDistSqr))
                    continue;

                var msg = WorldBurnSyncHelpers.BuildMessage(burn, burning: true);
                _net.SendBulkOrAll(NetMessageType.WorldBurnState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            return sent;
        }

        private static bool IsUnderOrNearLocation(
            Transform t, Transform root, Vector3 anchor, float maxDistSqr)
        {
            if (t == null) return false;
            if (root != null && (t == root || t.IsChildOf(root)))
                return true;
            if (root != null)
            {
                float dxRoot = t.position.x - root.position.x;
                float dzRoot = t.position.z - root.position.z;
                if (dxRoot * dxRoot + dzRoot * dzRoot <= maxDistSqr)
                    return true;
            }
            float dx = t.position.x - anchor.x;
            float dz = t.position.z - anchor.z;
            return dx * dx + dz * dz <= maxDistSqr;
        }

        private static GameObject FindTarget(byte targetType, Vector3 pos)
        {
            switch (targetType)
            {
                case WorldBurnStateMessage.TargetDoor:
                {
                    Door door = WorldQueryHelper.FindDoorByPos(pos);
                    if (door == null)
                        door = WorldQueryHelper.FindDoorByPosLoose(pos, 4f);
                    return door != null ? door.gameObject : null;
                }
                case WorldBurnStateMessage.TargetWindow:
                {
                    Window window = WorldQueryHelper.FindWindowByPos(pos);
                    if (window == null)
                        window = WorldQueryHelper.FindWindowByPosLoose(pos, 4f);
                    return window != null ? window.gameObject : null;
                }
                case WorldBurnStateMessage.TargetItem:
                {
                    // The sender sends the burning item's own position: match only that spot (a wide
                    // radius set the nearest crate on fire instead of the item that burned).
                    Item item = WorldQueryHelper.FindDestructibleItemXz(pos, BarricadeNetHandlers.ItemMatchRadius);
                    if (item == null)
                        item = WorldQueryHelper.FindNearest<Item>(pos, BarricadeNetHandlers.ItemMatchRadius);
                    return item != null ? item.gameObject : null;
                }
                default:
                    return null;
            }
        }
    }
}
