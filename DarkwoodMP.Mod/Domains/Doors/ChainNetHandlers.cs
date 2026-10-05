using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// ChainParent health / attached sync (pos-keyed absolute state).
    /// Covers getHit, attach, Vine latch, detach (timer / health-zero / onDie), late-join.
    /// </summary>
    internal sealed class ChainNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingChainStates = 32;
        private readonly List<ChainStateMessage> _pendingChainStates = new List<ChainStateMessage>();
        private float _nextPendingChainFlushTime;
        private const float PendingChainFlushInterval = 1f;

        internal int PendingChainCount => _pendingChainStates.Count;

        internal ChainNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingChains()
        {
            _pendingChainStates.Clear();
        }

        internal void HandleChainState(ChainStateMessage msg)
        {
            ApplyChainState(msg, queueIfMissing: true);
        }

        internal void ApplyChainState(ChainStateMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ChainParent chain = WorldQueryHelper.FindNearest<ChainParent>(pos, 2f);
            if (chain == null)
            {
                if (queueIfMissing)
                {
                    for (int i = _pendingChainStates.Count - 1; i >= 0; i--)
                    {
                        var p = _pendingChainStates[i];
                        if (Mathf.Abs(p.PosX - msg.PosX) < 0.5f &&
                            Mathf.Abs(p.PosY - msg.PosY) < 0.5f &&
                            Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pendingChainStates.RemoveAt(i);
                    }
                    if (_pendingChainStates.Count >= MaxPendingChainStates)
                        _pendingChainStates.RemoveAt(0);
                    _pendingChainStates.Add(msg);
                    ModRuntime.LegacyInfo($"[ChainSync] queued (chain not loaded) at {pos}");
                }
                return;
            }

            using (new NetworkApplyGuard())
            {
                bool prevHack = TraverseHack.GetExplicitFlag();
                TraverseHack.SetExplicitFlag(true);
                try
                {
                    if (msg.HasMaxHealth && msg.MaxHealth > 0f)
                        chain.maxHealth = msg.MaxHealth;

                    chain.health = Mathf.Max(0f, msg.Health);

                    bool wantAttached = msg.Attached != 0;
                    if (!wantAttached && chain.attached)
                        chain.detach();
                    else if (wantAttached && !chain.attached)
                        chain.attach();
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ChainSync] apply: " + ex.Message);
                }
                finally
                {
                    TraverseHack.SetExplicitFlag(prevHack);
                }
            }

            ModRuntime.LegacyInfo(
                $"[ChainSync] applied at {pos} health={msg.Health:F1} attached={msg.Attached != 0}");
        }

        internal void TryFlushPendingChainStates()
        {
            if (_pendingChainStates.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextPendingChainFlushTime) return;
            _nextPendingChainFlushTime = now + PendingChainFlushInterval;

            for (int i = _pendingChainStates.Count - 1; i >= 0; i--)
            {
                var msg = _pendingChainStates[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<ChainParent>(pos, 2f) == null)
                    continue;
                _pendingChainStates.RemoveAt(i);
                ApplyChainState(msg, queueIfMissing: false);
            }
        }

        /// <summary>
        /// Host: push damaged or detached ChainParents to a joiner (skip pristine).
        /// </summary>
        internal void SendChainStatesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            int sent = SendChainStatesFiltered(targetPlayerId, null, Vector3.zero, 0f, 256);
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} chain state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} chain state(s) to all clients");
        }

        /// <summary>
        /// Host→peer: damaged/detached chains under/near the pad. Pending chain
        /// queue is FIFO-capped at 32 before virgin-pad flush.
        /// </summary>
        internal int SendChainStatesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = WorldLateJoinNetHandlers.PadResyncMaxDistSqr;
            return SendChainStatesFiltered(targetPlayerId, root, anchor, maxDistSqr, 64);
        }

        private int SendChainStatesFiltered(
            int targetPlayerId, Transform root, Vector3 anchor, float maxDistSqr, int maxSend)
        {
            bool padScoped = root != null;

            ChainParent[] all = WorldQueryHelper.GetCachedSceneComponents<ChainParent>();
            int sent = 0;
            for (int i = 0; i < all.Length && sent < maxSend; i++)
            {
                ChainParent chain = all[i];
                if (chain == null || chain.transform == null) continue;
                // The host's own prologue pads are not the world.
                if (PersonalPrologue.IsOnProloguePad(chain.transform)) continue;

                float maxHp = chain.maxHealth > 0f ? chain.maxHealth : chain.health;
                bool damaged = maxHp > 0f && chain.health < maxHp;
                bool detached = !chain.attached;
                if (!damaged && !detached)
                    continue;
                if (padScoped && !IsUnderOrNearLocation(chain.transform, root, anchor, maxDistSqr))
                    continue;

                var msg = ChainSyncHelpers.BuildMessage(chain);
                _net.SendBulkOrAll(NetMessageType.ChainState, w => msg.Serialize(w), targetPlayerId);
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
    }
}
