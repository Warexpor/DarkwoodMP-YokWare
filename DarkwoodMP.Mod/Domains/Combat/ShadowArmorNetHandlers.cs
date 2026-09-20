using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// ShadowArmor mid-fight health sync (pos-keyed absolute state).
    /// Covers damageMe (melee + light), die, and late-join damaged armor.
    /// </summary>
    internal sealed class ShadowArmorNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingShadowArmorStates = 32;
        private readonly List<ShadowArmorStateMessage> _pending = new List<ShadowArmorStateMessage>();
        private float _nextPendingFlushTime;
        private const float PendingFlushInterval = 1f;

        internal int PendingCount => _pending.Count;

        internal ShadowArmorNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPending()
        {
            _pending.Clear();
        }

        internal void HandleShadowArmorState(ShadowArmorStateMessage msg)
        {
            ApplyShadowArmorState(msg, queueIfMissing: true);
        }

        internal void ApplyShadowArmorState(ShadowArmorStateMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ShadowArmor armor = FindShadowArmor(pos);
            if (armor == null)
            {
                if (queueIfMissing)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        var p = _pending[i];
                        if (Mathf.Abs(p.PosX - msg.PosX) < 0.5f &&
                            Mathf.Abs(p.PosY - msg.PosY) < 0.5f &&
                            Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pending.RemoveAt(i);
                    }
                    if (_pending.Count >= MaxPendingShadowArmorStates)
                        _pending.RemoveAt(0);
                    _pending.Add(msg);
                    ModRuntime.LegacyInfo("[ShadowArmorSync] queued (armor not loaded) at " + pos);
                }
                return;
            }

            using (new NetworkApplyGuard())
            {
                bool prevHack = TraverseHack.GetExplicitFlag();
                TraverseHack.SetExplicitFlag(true);
                try
                {
                    if (msg.Destroyed != 0)
                    {
                        armor.die(instant: true);
                    }
                    else
                    {
                        if (msg.MaxHealth > 0f)
                            armor.maxHealth = msg.MaxHealth;
                        float hp = Mathf.Max(0f, msg.Health);
                        armor.health = hp;
                        Traverse.Create(armor).Field("destHealth").SetValue(hp);
                        if (Singleton<UI>.Instance?.enemyHealthBar != null &&
                            Singleton<UI>.Instance.enemyHealthBar.currentObj == armor.gameObject)
                        {
                            Singleton<UI>.Instance.enemyHealthBar.show(armor.gameObject, onlyRefresh: true);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ShadowArmorSync] apply: " + ex.Message);
                }
                finally
                {
                    TraverseHack.SetExplicitFlag(prevHack);
                }
            }

            ModRuntime.LegacyInfo(
                $"[ShadowArmorSync] applied at {pos} health={msg.Health:F1}/{msg.MaxHealth:F1} " +
                $"destroyed={msg.Destroyed != 0}");
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
                if (FindShadowArmor(pos) == null)
                    continue;
                _pending.RemoveAt(i);
                ApplyShadowArmorState(msg, queueIfMissing: false);
            }
        }

        /// <summary>
        /// Host: push ShadowArmor with health &lt; maxHealth to a joiner (skip pristine).
        /// </summary>
        internal void SendShadowArmorStatesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            ShadowArmor[] all = WorldQueryHelper.GetCachedSceneComponents<ShadowArmor>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                ShadowArmor armor = all[i];
                if (armor == null) continue;

                float maxHp = armor.maxHealth > 0f ? armor.maxHealth : armor.health;
                if (!(maxHp > 0f && armor.health < maxHp))
                    continue;

                var msg = ShadowArmorSyncHelpers.BuildMessage(armor, destroyed: false);
                _net.SendBulkOrAll(NetMessageType.ShadowArmorState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} shadow-armor state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} shadow-armor state(s) to all clients");
        }

        /// <summary>
        /// Prefer Item at pos (world chests); fall back to nearest ShadowArmor
        /// (covers Character-owned armor without taking entity combat authority).
        /// </summary>
        private static ShadowArmor FindShadowArmor(Vector3 pos)
        {
            Item item = WorldQueryHelper.FindDestructibleItemXz(pos, 25f);
            if (item != null)
            {
                ShadowArmor onItem = item.shadowArmor;
                if (onItem == null)
                    onItem = item.GetComponent<ShadowArmor>();
                if (onItem != null)
                    return onItem;
            }

            ShadowArmor near = WorldQueryHelper.FindNearest<ShadowArmor>(pos, 2.5f);
            if (near != null)
                return near;
            return WorldQueryHelper.FindNearest<ShadowArmor>(pos, 8f);
        }
    }
}
