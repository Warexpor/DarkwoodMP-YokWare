using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Story flag delta + bulk handlers composed for 0.8.</summary>
    internal sealed class FlagNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingFlagDeltas = 256;
        private bool _hasPendingFlagBulk;
        private FlagBulkSyncMessage _pendingFlagBulk;

        private struct PendingDelta
        {
            public FlagSyncMessage Msg;
            /// <summary>Host: the client that sent it (excluded from the rebroadcast); 0 otherwise.</summary>
            public int From;
        }

        private readonly List<PendingDelta> _pendingFlagDeltas = new List<PendingDelta>();

        internal FlagNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void ClearPendingFlags()
        {
            _hasPendingFlagBulk = false;
            _pendingFlagBulk = default;
            _pendingFlagDeltas.Clear();
        }

        internal void HandleFlagSync(FlagSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Name))
                return;

            // Per-player flags never travel; drop one an older peer still sends.
            if (FlagSyncBoolPatch.IsLocalOnlyFlag(msg.Name))
                return;

            // The host applies client story-flag deltas and rebroadcasts them.
            if (_net.Role == NetworkRole.Host)
            {
                int from = _net.CurrentReceivePlayerId;
                if (Singleton<Flags>.Instance == null)
                {
                    // Applied and fanned out by TryFlushPendingFlags once Flags exists.
                    QueuePendingFlagDelta(msg, from);
                    return;
                }
                HostApplyAndFanOut(msg, from);
                return;
            }

            if (_net.Role != NetworkRole.Client)
            {
                ModLog.WarnRate(LogCat.Network, "flagsync-role", "[FlagSync] unexpected role for flag sync");
                return;
            }

            if (Singleton<Flags>.Instance == null)
            {
                // Client may still be on main menu / loading when host sends deltas.
                QueuePendingFlagDelta(msg, 0);
                return;
            }

            ApplyFlagSyncMessage(msg);
        }

        internal void ApplyFlagSyncMessage(FlagSyncMessage msg)
        {
            // Always apply under NetworkApplyGuard so FlagSyncPatches Postfix does not
            // re-Send/Broadcast (client echo / double fan-out). Intentional host fan-out
            // in HandleFlagSync runs *after* this method returns.
            using (new NetworkApplyGuard())
            {
                if (msg.IsInt)
                    Singleton<Flags>.Instance.setFlag(msg.Name, msg.IntValue);
                else
                    Singleton<Flags>.Instance.setFlag(msg.Name, msg.BoolValue);
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[FlagSync] applied flag '{msg.Name}' isInt={msg.IsInt} boolVal={msg.BoolValue} intVal={msg.IntValue}");
        }

        /// <summary>Host: apply a client's flag delta and rebroadcast it (originator excluded when known).</summary>
        private void HostApplyAndFanOut(FlagSyncMessage msg, int from)
        {
            ApplyFlagSyncMessage(msg);
            if (from > 0)
                _net.SendToAllExcept(from, NetMessageType.FlagSync, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            else
                _net.Broadcast(NetMessageType.FlagSync, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
        }

        internal void QueuePendingFlagDelta(FlagSyncMessage msg, int from)
        {
            // Keep latest value per flag name (bool and int share the same name key via IsInt)
            for (int i = _pendingFlagDeltas.Count - 1; i >= 0; i--)
            {
                if (_pendingFlagDeltas[i].Msg.Name == msg.Name && _pendingFlagDeltas[i].Msg.IsInt == msg.IsInt)
                    _pendingFlagDeltas.RemoveAt(i);
            }
            if (_pendingFlagDeltas.Count >= MaxPendingFlagDeltas)
                _pendingFlagDeltas.RemoveAt(0);
            _pendingFlagDeltas.Add(new PendingDelta { Msg = msg, From = from });
        }

        /// <summary>Send all current game flags to all clients.</summary>
        internal void SendFlagBulkSync() => SendFlagBulkSyncTo(-1);

        internal void SendFlagBulkSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            // Per-player flags are the host's own (where it stands, its popups, its night): never
            // handed to a joiner, which keeps its own.
            var shared = new List<KeyValuePair<string, Flags.Flag>>(flags.flagsDict.Count);
            foreach (var kvp in flags.flagsDict)
            {
                if (kvp.Value == null || FlagSyncBoolPatch.IsLocalOnlyFlag(kvp.Key))
                    continue;
                shared.Add(kvp);
                if (shared.Count >= 4096) break;
            }
            int count = shared.Count;
            var msg = new FlagBulkSyncMessage
            {
                FlagCount = count,
                FlagNames = new string[count],
                FlagIsTrue = new bool[count],
                FlagAmounts = new int[count]
            };
            for (int i = 0; i < count; i++)
            {
                msg.FlagNames[i] = shared[i].Key;
                msg.FlagIsTrue[i] = shared[i].Value.isTrue;
                msg.FlagAmounts[i] = shared[i].Value.amount;
            }
            _net.SendBulkOrAll(NetMessageType.FlagBulkSync, w => msg.Serialize(w), targetPlayerId);
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {count} flags to player {targetPlayerId}"
                : $"[BulkSync] Sent {count} flags to all clients");
        }

        internal void HandleFlagBulkSync(FlagBulkSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;

            // Deltas queued before this bulk are older than it (same reliable-ordered channel):
            // applying them after the bulk would revert newer values. Deltas arriving after it
            // stay queued behind the pending bulk.
            if (_pendingFlagDeltas.Count > 0)
            {
                ModLog.Event(LogCat.Session,
                    $"[BulkSync] Dropped {_pendingFlagDeltas.Count} flag delta(s) superseded by the bulk");
                _pendingFlagDeltas.Clear();
            }

            if (!LanNetworkManager.ClientCanApplyWorldBulk() || Singleton<Flags>.Instance == null)
            {
                // Join bulk often arrives before the client has loaded into a world.
                _hasPendingFlagBulk = true;
                _pendingFlagBulk = msg;
                ModLog.Event(LogCat.Session, $"[BulkSync] Flags queued (in-world=" + LanNetworkManager.ClientCanApplyWorldBulk() + ")");
                return;
            }

            ApplyFlagBulkSync(msg);
        }

        internal void ApplyFlagBulkSync(FlagBulkSyncMessage msg)
        {
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            int skipped = 0;
            for (int i = 0; i < msg.FlagCount; i++)
            {
                string name = msg.FlagNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                // This player's own flags stay as they are (an older host still sends them).
                if (FlagSyncBoolPatch.IsLocalOnlyFlag(name))
                {
                    skipped++;
                    continue;
                }
                if (flags.flagsDict.TryGetValue(name, out var flag))
                {
                    flag.isTrue = msg.FlagIsTrue[i];
                    flag.amount = msg.FlagAmounts[i];
                }
                else
                {
                    flags.setFlag(name, msg.FlagIsTrue[i]);
                    flags.setFlag(name, msg.FlagAmounts[i]);
                }
            }
            ModLog.Event(LogCat.Session,
                $"[BulkSync] Applied {msg.FlagCount - skipped} flags ({skipped} per-player kept local)");
        }

        /// <summary>
        /// Apply flag bulk/deltas that arrived before <see cref="Flags"/> existed (menu/load).
        /// Called every frame after network poll. Gate: not on title menu.
        /// </summary>
        internal void TryFlushPendingFlags()
        {
            if (_net.Role == NetworkRole.Host)
            {
                // Client deltas that arrived while the host's Flags did not exist yet.
                if (_pendingFlagDeltas.Count == 0 || Singleton<Flags>.Instance == null)
                    return;
                var drain = _pendingFlagDeltas.ToArray();
                _pendingFlagDeltas.Clear();
                for (int i = 0; i < drain.Length; i++)
                    HostApplyAndFanOut(drain[i].Msg, drain[i].From);
                return;
            }
            if (_net.Role != NetworkRole.Client)
                return;
            // Same gate as the journal; Flags may exist on the title screen while the world is not ready.
            if (!LanNetworkManager.ClientCanApplyWorldBulk())
                return;
            if (Singleton<Flags>.Instance == null)
                return;

            if (_hasPendingFlagBulk)
            {
                _hasPendingFlagBulk = false;
                try
                {
                    ApplyFlagBulkSync(_pendingFlagBulk);
                }
                catch (Exception ex)
                {
                    _hasPendingFlagBulk = true;
                    ModLog.Warn(LogCat.Session, "Flag bulk flush retry later: " + ex.Message);
                    return;
                }
                _pendingFlagBulk = default;
            }

            if (_pendingFlagDeltas.Count > 0)
            {
                for (int i = 0; i < _pendingFlagDeltas.Count; i++)
                    ApplyFlagSyncMessage(_pendingFlagDeltas[i].Msg);
                _pendingFlagDeltas.Clear();
            }
        }
    }
}
