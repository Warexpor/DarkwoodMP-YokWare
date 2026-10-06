using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Client hit validation: who may fight, and the per-peer attack budget.</summary>
    internal sealed partial class CombatAttackNetHandlers
    {
        // Client authority is trust-limited: the host cannot see the client's aim, and the
        // attacker proxy position itself comes from that client's PlayerState. The checks
        // below (alive / not in another world, range from the host proxy, per-message clamp,
        // per-peer token bucket) bound what a modified client can do; they do not prove a hit.

        private sealed class PeerBudget
        {
            public float AttackTokens = GameplayConstants.PeerAttackBurst;
            public float DamageTokens = GameplayConstants.PeerDamageBurst;
            public float LastAt;
        }

        private readonly Dictionary<int, PeerBudget> _budgets = new Dictionary<int, PeerBudget>();

        /// <summary>
        /// Per-peer token bucket over hits and damage. Bursts (shotgun pellets, one explosion
        /// over a pack) fit in the burst size; sustained spam is capped at the refill rate.
        /// </summary>
        private bool TryConsumeBudget(int playerId, int damage, string context)
        {
            float now = Time.unscaledTime;
            if (!_budgets.TryGetValue(playerId, out PeerBudget b))
            {
                b = new PeerBudget { LastAt = now };
                _budgets[playerId] = b;
            }
            float elapsed = now - b.LastAt;
            b.LastAt = now;
            if (CombatAuthorityPolicy.TryConsumeAttackBudget(
                    ref b.AttackTokens, ref b.DamageTokens, elapsed, damage,
                    GameplayConstants.PeerAttackRatePerSec, GameplayConstants.PeerAttackBurst,
                    GameplayConstants.PeerDamageRatePerSec, GameplayConstants.PeerDamageBurst))
                return true;
            ModLog.WarnRate(LogCat.Combat, "atk-budget:" + playerId,
                "[" + context + "] rejected: p" + playerId + " over attack budget (dmg=" + damage + ")");
            return false;
        }

        /// <summary>
        /// A night-dead / dead / dream-dead attacker cannot hit anything, and one inside a dream
        /// the host is not in cannot hit the host's world.
        /// </summary>
        private bool AttackerCanFight(int playerId, RemotePlayerProxy proxy, out string why)
        {
            why = null;
            if (DeathStateTracker.IsRemoteNightDead(playerId))
                why = "night-dead";
            else if (proxy.CachedCharBase != null && !proxy.CachedCharBase.alive)
                why = "dead";
            else if (_net.TryGetRemoteState(playerId, out var st) && st.IsDeadInDream)
                why = "dead in dream";
            else if (DreamSyncManager.IsRemoteInDream(playerId) && !DreamSyncManager.IsLocalDreamActive)
                why = "in a dream the host is not in";
            return why == null;
        }

        /// <summary>Reject a client hit: log and never let the generic relay forward it.</summary>
        private void RejectClientHit(string rateKey, string message)
        {
            _net.SuppressRelay();
            ModLog.WarnRate(LogCat.Combat, rateKey, message);
        }
    }
}
