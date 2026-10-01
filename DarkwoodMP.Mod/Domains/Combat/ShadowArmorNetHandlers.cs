using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// ShadowArmor mid-fight health sync (pos-keyed absolute state).
    /// Covers damageMe (melee + light), die, and late-join damaged armor.
    /// </summary>
    internal sealed partial class ShadowArmorNetHandlers
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
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
            {
                // Clients never set armor state (last writer won, a client could force a break);
                // they ask for damage, the host applies it and broadcasts the absolute result.
                _net._suppressForwardThisMessage = true;
                if (msg.Destroyed == ShadowArmorSyncHelpers.ModeDamageRequest)
                    HostApplyDamageRequest(msg, _net.CurrentReceivePlayerId);
                else
                    ModLog.WarnRate(LogCat.Combat, "shadowarmor-client-abs:" + _net.CurrentReceivePlayerId,
                        "[ShadowArmorSync] ignored absolute armor state from client p" + _net.CurrentReceivePlayerId);
                return;
            }
            // Peers apply only the host's absolute state.
            if (msg.Destroyed == ShadowArmorSyncHelpers.ModeDamageRequest)
                return;
            ApplyShadowArmorState(msg, queueIfMissing: true);
        }

        /// <summary>
        /// Host: apply a client's armor damage to the host's own armor and broadcast the absolute
        /// state (everyone, requester included). The armor breaks only when the host's own
        /// health reaches 0.
        /// </summary>
        private void HostApplyDamageRequest(ShadowArmorStateMessage msg, int playerId)
        {
            if (!CombatAuthorityPolicy.IsFinitePosition(msg.PosX, msg.PosY, msg.PosZ)
                || !CombatAuthorityPolicy.IsFinite(msg.Health) || msg.Health <= 0f)
                return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null || DeathStateTracker.IsRemoteNightDead(playerId))
                return;
            float maxRange = GameplayConstants.MaxPlayerRangedAttackRange;
            if (Vector3.SqrMagnitude(proxy.transform.position - pos) > maxRange * maxRange)
            {
                ModLog.WarnRate(LogCat.Combat, "shadowarmor-far:" + playerId,
                    "[ShadowArmorSync] rejected damage request from p" + playerId + " — armor out of range");
                return;
            }

            ShadowArmor armor = FindShadowArmor(pos);
            if (armor == null)
                return;

            int maxDmg = Config.ModConfig.MaxPeerDamage != null ? Config.ModConfig.MaxPeerDamage.Value : 200;
            float damage = Mathf.Min(msg.Health, Mathf.Max(1, maxDmg));

            float dest;
            try { dest = ShadowArmorSyncHelpers.ReadDestHealth(armor); }
            catch { return; }
            if (dest <= 0f)
                return;
            float hp = Mathf.Max(0f, dest - damage);

            ShadowArmorStateMessage state;
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (hp <= 0f)
                {
                    state = ShadowArmorSyncHelpers.BuildMessage(armor, destroyed: true);
                    armor.die(instant: false);
                }
                else
                {
                    Traverse.Create(armor).Field("destHealth").SetValue(hp);
                    state = ShadowArmorSyncHelpers.BuildMessage(armor, destroyed: false);
                    if (Singleton<UI>.Instance?.enemyHealthBar != null &&
                        Singleton<UI>.Instance.enemyHealthBar.currentObj == armor.gameObject)
                    {
                        Singleton<UI>.Instance.enemyHealthBar.show(armor.gameObject, onlyRefresh: true);
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ShadowArmorSync] host damage apply: " + ex.Message);
                return;
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prevHack);
            }

            _net.Broadcast(NetMessageType.ShadowArmorState, w => state.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ShadowArmorSync] p{playerId} dealt {damage:F1} at {pos} → {hp:F1} destroyed={hp <= 0f}");
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
                    {
                        ModLog.WarnRate(LogCat.Combat, "shadowarmor-pending-overflow",
                            "[ShadowArmorSync] pending queue full (" + MaxPendingShadowArmorStates
                            + ") — dropped oldest unmatched armor state");
                        _pending.RemoveAt(0);
                    }
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

        /// <summary>Wire positions are rounded to 0.1 m; a moving Character-owned armor drifts a little more.</summary>
        private const float ArmorMatchRadius = 2.5f;

        /// <summary>
        /// The message carries the armor's own position, so match on position only: the
        /// nearest ShadowArmor within <see cref="ArmorMatchRadius"/>, else the armor of a
        /// destructible Item standing there (world chests). No wider fallback: an armor
        /// that is not here yet stays queued rather than damaging an unrelated neighbour.
        /// </summary>
        private static ShadowArmor FindShadowArmor(Vector3 pos)
        {
            ShadowArmor near = WorldQueryHelper.FindNearest<ShadowArmor>(pos, ArmorMatchRadius);
            if (near != null)
                return near;

            Item item = WorldQueryHelper.FindDestructibleItemXz(pos, ArmorMatchRadius);
            if (item != null)
            {
                ShadowArmor onItem = item.shadowArmor;
                if (onItem == null)
                    onItem = item.GetComponent<ShadowArmor>();
                if (onItem != null)
                    return onItem;
            }
            return null;
        }
    }
}
