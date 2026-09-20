using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Constructible / padlock / locked / interactive bulk composed for 0.8.</summary>
    internal sealed class LockNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingConstructibles = 64;
        private readonly Dictionary<string, ConstructibleMessage> _constructedSites =
            new Dictionary<string, ConstructibleMessage>();
        private readonly List<ConstructibleMessage> _pendingConstructibles = new List<ConstructibleMessage>();

        internal int PendingConstructibleCount => _pendingConstructibles.Count;

        internal LockNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingLocks()
        {
            _pendingInteractive.Clear();
            _pendingPadlocks.Clear();
            _pendingLocked.Clear();
        }

        internal void ClearConstructibleState()
        {
            _constructedSites.Clear();
            _pendingConstructibles.Clear();
        }


        internal void HandleConstructible(ConstructibleMessage msg)
        {
            RegisterConstructedSite(new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.OptionIndex);
            ApplyConstructible(msg, queueIfMissing: true);
        }

        internal void ApplyConstructible(ConstructibleMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Constructible best = WorldQueryHelper.FindNearest<Constructible>(pos, 0.75f);
            if (best == null)
            {
                if (queueIfMissing)
                {
                    if (_pendingConstructibles.Count >= MaxPendingConstructibles)
                        _pendingConstructibles.RemoveAt(0);
                    _pendingConstructibles.Add(msg);
                    ModRuntime.LegacyInfo("[ConstructibleSync] queued (not loaded yet) at " + pos);
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[ConstructibleSync] no Constructible found near " + pos);
                }
                return;
            }

            // Already built locally; do not re-fire the game event or construct twice.
            if (best.constructed)
            {
                ModRuntime.LegacyInfo("[ConstructibleSync] already constructed " + best.name + " at " + pos);
                return;
            }

            ModRuntime.LegacyInfo("[ConstructibleSync] constructing " + best.name + " at " + pos);
            // Always pass manual=false on the receiving side; the
            // constructing player already consumed ingredients locally.
            // Using manual=true would crash (ConstructionMenu.Instance.
            // selectedIcon is null when the menu isn't open).
            int option = msg.OptionIndex >= 0 ? msg.OptionIndex : best.chosenOption;
            best.construct(false, option);
        }

        /// <summary>Host registry of constructed sites for late-join bulk.</summary>
        internal void RegisterConstructedSite(Vector3 key, int optionIndex)
        {
            string id = ConstructibleSiteKey(key);
            _constructedSites[id] = new ConstructibleMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                UseIngredients = false,
                OptionIndex = optionIndex
            };
        }

        private static string ConstructibleSiteKey(Vector3 key)
        {
            return $"{key.x:F1}_{key.y:F1}_{key.z:F1}";
        }

        /// <summary>Host: push known constructed sites to a joiner (or all if target &lt;= 0).</summary>
        internal void SendConstructedSitesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            // Live registry from this session
            int sent = 0;
            foreach (var kvp in _constructedSites)
            {
                var msg = kvp.Value;
                _net.SendBulkOrAll(NetMessageType.ConstructibleConstruction, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            // Also any Constructible still in scene with constructed==true (save-loaded)
            Constructible[] all = WorldQueryHelper.GetCachedSceneComponents<Constructible>();
            for (int i = 0; i < all.Length; i++)
            {
                Constructible c = all[i];
                if (c == null || !c.constructed) continue;
                Vector3 p = c.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                string id = ConstructibleSiteKey(key);
                if (_constructedSites.ContainsKey(id)) continue;
                var msg = new ConstructibleMessage
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    UseIngredients = false,
                    OptionIndex = c.chosenOption
                };
                _constructedSites[id] = msg;
                _net.SendBulkOrAll(NetMessageType.ConstructibleConstruction, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} constructible sites to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} constructible sites to all clients");
        }

        private float _nextPendingConstructibleFlushTime;

        internal void TryFlushPendingConstructibles()
        {
            if (_pendingConstructibles.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextPendingConstructibleFlushTime) return;
            _nextPendingConstructibleFlushTime = now + PendingLockFlushInterval;
            for (int i = _pendingConstructibles.Count - 1; i >= 0; i--)
            {
                var msg = _pendingConstructibles[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Constructible best = WorldQueryHelper.FindNearest<Constructible>(pos, 0.75f);
                if (best == null) continue;
                _pendingConstructibles.RemoveAt(i);
                ApplyConstructible(msg, queueIfMissing: false);
            }
        }

        // Late-join / not-yet-loaded: retry unlock/switch when world objects appear.
        private readonly System.Collections.Generic.List<InteractiveItemSwitchMessage> _pendingInteractive =
            new System.Collections.Generic.List<InteractiveItemSwitchMessage>();
        private readonly System.Collections.Generic.List<PadlockUnlockMessage> _pendingPadlocks =
            new System.Collections.Generic.List<PadlockUnlockMessage>();
        private readonly System.Collections.Generic.List<LockedUnlockMessage> _pendingLocked =
            new System.Collections.Generic.List<LockedUnlockMessage>();
        private const float LockFindRadius = 2.5f;
        private const int MaxPendingLocks = 64;

        /// <summary>Pending padlock+locked+interactive counts for CoopPerfProbe.</summary>
        internal int PendingLockCount =>
            _pendingPadlocks.Count + _pendingLocked.Count + _pendingInteractive.Count;

        internal void HandleInteractiveItemSwitch(InteractiveItemSwitchMessage msg)
        {
            ApplyInteractiveItemSwitch(msg, queueIfMissing: true);
        }

        internal void ApplyInteractiveItemSwitch(InteractiveItemSwitchMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            InteractiveItem best = WorldQueryHelper.FindNearest<InteractiveItem>(pos, LockFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                    QueuePendingLock(_pendingInteractive, msg, MaxPendingLocks);
                else
                    ModRuntime.Log?.LogWarning("[InteractiveItemSync] no InteractiveItem found near " + pos);
                return;
            }

            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                if (msg.IsOn && !best.isOn)
                {
                    if (best.onTrigger == null)
                        best.isOn = true;
                    else
                        best.switchOn();
                }
                else if (!msg.IsOn && best.isOn)
                {
                    if (best.offTrigger == null)
                        best.isOn = false;
                    else
                        best.switchOff();
                }
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }
        }

        internal void HandlePadlockUnlock(PadlockUnlockMessage msg)
        {
            ApplyPadlockUnlock(msg, queueIfMissing: true);
        }

        internal void ApplyPadlockUnlock(PadlockUnlockMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Padlock best = WorldQueryHelper.FindNearest<Padlock>(pos, LockFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                    QueuePendingLock(_pendingPadlocks, msg, MaxPendingLocks);
                else
                    ModRuntime.Log?.LogWarning("[PadlockSync] no Padlock found near " + pos);
                return;
            }

            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                // manually=false: set locked=false without UI / double triggers
                if (best.locked)
                    best.unlock(false);
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }
        }

        internal void HandleLockedUnlock(LockedUnlockMessage msg)
        {
            ApplyLockedUnlock(msg, queueIfMissing: true);
        }

        internal void ApplyLockedUnlock(LockedUnlockMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Locked best = WorldQueryHelper.FindNearest<Locked>(pos, LockFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                    QueuePendingLock(_pendingLocked, msg, MaxPendingLocks);
                else
                    ModRuntime.Log?.LogWarning("[LockedSync] no Locked found near " + pos);
                return;
            }

            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                if (best.locked)
                    best.unlock();
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }
        }

        private static void QueuePendingLock<T>(System.Collections.Generic.List<T> list, T msg, int max)
        {
            if (list.Count >= max)
                list.RemoveAt(0);
            list.Add(msg);
        }

        private float _nextPendingLockFlushTime;
        private const float PendingLockFlushInterval = 1f;

        /// <summary>Flush padlock/locked/interactive pending when world objects appear.</summary>
        internal void TryFlushPendingLocks()
        {
            if (_pendingPadlocks.Count == 0 && _pendingLocked.Count == 0 && _pendingInteractive.Count == 0)
                return;
            // Unloaded locks used to FindNearest every frame → sustained poll/upd hitches.
            float now = Time.unscaledTime;
            if (now < _nextPendingLockFlushTime) return;
            _nextPendingLockFlushTime = now + PendingLockFlushInterval;

            for (int i = _pendingPadlocks.Count - 1; i >= 0; i--)
            {
                var msg = _pendingPadlocks[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<Padlock>(pos, LockFindRadius) == null)
                    continue;
                _pendingPadlocks.RemoveAt(i);
                ApplyPadlockUnlock(msg, queueIfMissing: false);
            }
            for (int i = _pendingLocked.Count - 1; i >= 0; i--)
            {
                var msg = _pendingLocked[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<Locked>(pos, LockFindRadius) == null)
                    continue;
                _pendingLocked.RemoveAt(i);
                ApplyLockedUnlock(msg, queueIfMissing: false);
            }
            for (int i = _pendingInteractive.Count - 1; i >= 0; i--)
            {
                var msg = _pendingInteractive[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<InteractiveItem>(pos, LockFindRadius) == null)
                    continue;
                _pendingInteractive.RemoveAt(i);
                ApplyInteractiveItemSwitch(msg, queueIfMissing: false);
            }
        }

        /// <summary>
        /// Host join bulk: unlocked padlocks for late joiners.
        /// </summary>
        internal void SyncExistingPadlocksTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            int padlocks = 0;
            Padlock[] pads = WorldQueryHelper.GetCachedSceneComponents<Padlock>();
            for (int i = 0; i < pads.Length; i++)
            {
                Padlock p = pads[i];
                if (p == null || p.locked || !p.gameObject.scene.IsValid()) continue;
                Vector3 pos = p.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                _net.SendToPlayer(targetPlayerId, NetMessageType.PadlockUnlock,
                    w => new PadlockUnlockMessage { PosX = key.x, PosY = key.y, PosZ = key.z }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                padlocks++;
            }
            if (padlocks > 0)
                ModRuntime.LegacyInfo("[BulkSync] Padlocks → p" + targetPlayerId + ": " + padlocks);
        }

        /// <summary>Host join bulk: unlocked Locked components.</summary>
        internal void SyncExistingLockedsTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            int locked = 0;
            Locked[] locks = WorldQueryHelper.GetCachedSceneComponents<Locked>();
            for (int i = 0; i < locks.Length; i++)
            {
                Locked l = locks[i];
                if (l == null || l.locked || !l.gameObject.scene.IsValid()) continue;
                Vector3 pos = l.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                _net.SendToPlayer(targetPlayerId, NetMessageType.LockedUnlock,
                    w => new LockedUnlockMessage { PosX = key.x, PosY = key.y, PosZ = key.z }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                locked++;
            }
            if (locked > 0)
                ModRuntime.LegacyInfo("[BulkSync] Lockeds → p" + targetPlayerId + ": " + locked);
        }

        /// <summary>Host join bulk: InteractiveItem isOn.</summary>
        internal void SyncExistingInteractivesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            int interactive = 0;
            InteractiveItem[] items = WorldQueryHelper.GetCachedSceneComponents<InteractiveItem>();
            for (int i = 0; i < items.Length; i++)
            {
                InteractiveItem ii = items[i];
                if (ii == null || !ii.isOn || !ii.gameObject.scene.IsValid()) continue;
                Vector3 pos = ii.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                _net.SendToPlayer(targetPlayerId, NetMessageType.InteractiveItemSwitch,
                    w => new InteractiveItemSwitchMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z, IsOn = true
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                interactive++;
            }
            if (interactive > 0)
                ModRuntime.LegacyInfo("[BulkSync] Interactives → p" + targetPlayerId + ": " + interactive);
        }

        /// <summary>
        /// Host join bulk: unlocked padlocks, doors, and interactive isOn.
        /// Prefer staggered TickHeavyLateJoinBulk phases; kept for any direct callers.
        /// </summary>
        internal void SyncExistingLocksAndInteractives(int targetPlayerId)
        {
            SyncExistingPadlocksTo(targetPlayerId);
            SyncExistingLockedsTo(targetPlayerId);
            SyncExistingInteractivesTo(targetPlayerId);
        }
    }
}
