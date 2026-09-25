using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host-authoritative attack / damage / friendly-fire message handlers.
    /// </summary>
    internal sealed class CombatAttackNetHandlers
    {
        private readonly LanNetworkManager _net;

        // Same-frame double delivery from ProxyDamage and collision is not a multi-pellet window.
        // Keep this debounce short enough to preserve rapid shotgun pellets.
        private readonly Dictionary<string, float> _ffDebounce = new Dictionary<string, float>();
        private const float FriendlyFireDebounceSec = 0.02f;

        internal CombatAttackNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void Reset()
        {
            _ffDebounce.Clear();
        }

        /// <summary>
        /// Per-message anti-grief clamp only. Do not rate-limit peer attacks:
        /// shotguns / multi-ray hitscan send legitimate bursts that a 50ms gate
        /// would drop (under-damage for client FF and PvE).
        /// </summary>
        internal int SanitizePeerDamage(int reported, string context)
        {
            int max = Config.ModConfig.MaxPeerDamage != null ? Config.ModConfig.MaxPeerDamage.Value : 200;
            if (max < 1) max = 1;
            if (reported < 0) reported = 0;
            if (reported > max)
            {
                EntitySyncLog.Damage("[DmgClamp] " + context + " " + reported + " → " + max);
                return max;
            }
            return reported;
        }

        internal void HandlePlayerAttack(PlayerAttackMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
            {
                EntitySyncLog.CombatTrace("atk:role",
                    "[Attack] rejected: not host role=" + _net.Role, 5f);
                return;
            }

            int playerId = _net.CurrentReceivePlayerId;
            if (!CombatAuthorityPolicy.IsFinitePosition(
                    msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ)
                || !CombatAuthorityPolicy.IsFinitePosition(
                    msg.TargetPosX, msg.TargetPosY, msg.TargetPosZ)
                || string.IsNullOrEmpty(msg.TargetName))
            {
                ModRuntime.Log?.LogWarning("[HandlePlayerAttack] rejected malformed position/target");
                return;
            }

            RemotePlayerProxy attackingProxy = _net.GetProxy(playerId);
            if (attackingProxy == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[HandlePlayerAttack] rejected: no authoritative proxy for player " + playerId);
                return;
            }

            // The host proxy is authoritative; do not use a client-supplied
            // origin for target range checks or damage attribution.
            Vector3 attackPos = attackingProxy.transform.position;
            Vector3 targetPos = new Vector3(msg.TargetPosX, msg.TargetPosY, msg.TargetPosZ);
            if (!CombatAuthorityPolicy.IsWithinRange(
                    msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ,
                    attackPos.x, attackPos.y, attackPos.z,
                    GameplayConstants.MaxPlayerAttackRange))
            {
                ModRuntime.Log?.LogWarning(
                    "[HandlePlayerAttack] rejected attacker position outside authoritative range for p"
                    + playerId);
                return;
            }

            Character target = ResolvePlayerAttackTarget(msg, attackPos, targetPos);
            if (target == null)
            {
                EntitySyncLog.Damage(
                    "[Attack] target null nameHash=" + msg.TargetNameHash
                    + " name='" + msg.TargetName + "' tPos=" + targetPos);
                return;
            }

            if (!target.alive)
            {
                EntitySyncLog.CombatTrace("atk:dead",
                    "[Attack] target already dead " + target.name
                    + "(id=" + CharacterTracker.GetStableId(target) + ")", 1f);
                return;
            }

            float maxRange = GameplayConstants.MaxPlayerAttackRange;
            float distSq = Vector3.SqrMagnitude(target.transform.position - attackPos);
            if (distSq > maxRange * maxRange)
            {
                EntitySyncLog.Damage(
                    "[Attack] too far dist=" + Mathf.Sqrt(distSq).ToString("F1")
                    + " > " + maxRange + " target=" + target.name);
                return;
            }

            if (!target.gameObject.activeSelf)
                target.gameObject.SetActive(true);
            if (!target.enabled)
                target.enabled = true;

            int damage = SanitizePeerDamage(msg.Damage, "HandlePlayerAttack");
            if (damage <= 0) return;
            Transform attackerT = attackingProxy.transform;

            float hpBefore = target.Health;
            target.getHit(damage, attackerT, msg.CanCutInHalf, byPlayer: true, canInterrupt: true);

            EntitySyncLog.Damage(
                "[Attack] p" + playerId + " → " + target.name
                + "(id=" + CharacterTracker.GetStableId(target) + ") dmg=" + damage
                + " hp " + hpBefore.ToString("F0") + "→" + target.Health.ToString("F0")
                + " alive=" + target.alive
                + " clip=" + (target.clipToPlay ?? ""));
        }

        /// <summary>
        /// Resolve client hit to a host Character: stable id first, then position+name
        /// (unsynced phantoms), then closest-by-name capped to match radius.
        /// </summary>
        private static Character ResolvePlayerAttackTarget(PlayerAttackMessage msg, Vector3 attackPos, Vector3 targetPos)
        {
            Character target = null;

            if (msg.TargetNameHash != 0)
            {
                target = CharacterTracker.FindByStableId(msg.TargetNameHash);
                if (target != null && AttackTargetNameMatches(target, msg.TargetName))
                    return target;
            }

            if (string.IsNullOrEmpty(msg.TargetName))
                return null;

            // A stale id must not leap to another wolf tens of meters away.
            // Unsynced hits (id 0) still use the wider name match.
            bool staleId = msg.TargetNameHash != 0;
            float matchR = staleId ? 12f : GameplayConstants.PlayerAttackNameMatchRadius;
            target = CharacterTracker.FindByPositionAndName(targetPos, msg.TargetName, matchR);
            if (target != null)
                return target;
            if (staleId)
                return null;

            // Wider ring still anchored at targetPos (not map-wide closest name).
            target = CharacterTracker.FindByPositionAndName(targetPos, msg.TargetName, matchR * 2.5f);
            if (target != null)
                return target;

            // Last resort: closest by name, but only if within match radius of reported pos.
            Character loose = CharacterTracker.FindClosestByName(msg.TargetName, targetPos);
            if (loose != null)
            {
                float dx = loose.transform.position.x - targetPos.x;
                float dz = loose.transform.position.z - targetPos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq <= (matchR * 3f) * (matchR * 3f))
                    return loose;
            }

            return null;
        }

        private static bool AttackTargetNameMatches(Character target, string reportedName)
        {
            if (target == null || string.IsNullOrEmpty(reportedName))
                return false;
            string have = target.name ?? "";
            if (have.EndsWith("(Clone)", StringComparison.Ordinal))
                have = have.Substring(0, have.Length - 7);
            string want = reportedName;
            if (want.EndsWith("(Clone)", StringComparison.Ordinal))
                want = want.Substring(0, want.Length - 7);
            return string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
        }

        internal void HandleDamagePlayer(DamagePlayerMessage msg)
        {
            // Targeted delivery only (SendToPlayer). FF / environmental gates are
            // enforced by senders (ProxyDamagePatch vs ExplosionFriendlyFirePatch).
            if (_net.Role != NetworkRole.Client) return;
            if (DeathStateTracker.LocalNightDeath) return; // already dead — ignore late hits
            Player local = Player.Instance;
            if (local == null || !local.alive) return;

            int damage = SanitizePeerDamage(msg.Damage, "DamagePlayer");
            if (damage <= 0) return;

            EntitySyncLog.Damage(
                "[DamagePlayer] local took " + damage
                + " cut=" + msg.CanCutInHalf + " interrupt=" + msg.CanInterrupt);
            local.getHit(
                damage,
                null,
                msg.CanCutInHalf,
                byPlayer: false,
                canInterrupt: msg.CanInterrupt,
                normalHit: msg.NormalHit,
                showRedScreen: msg.ShowRedScreen);
        }

        internal void HandleFriendlyFire(FriendlyFireMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;
            if (!Config.ModConfig.FriendlyFireEnabled.Value) return;

            int atkPlayerId = _net.CurrentReceivePlayerId;
            if (!CombatAuthorityPolicy.IsValidPlayerId(atkPlayerId)
                || (msg.AttackerPlayerId > 0 && msg.AttackerPlayerId != atkPlayerId))
            {
                ModRuntime.Log?.LogWarning(
                    "[FriendlyFire] rejected spoofed attacker id claimed="
                    + msg.AttackerPlayerId + " received=" + atkPlayerId);
                return;
            }

            int victimPlayerId = msg.VictimPlayerId;
            if (!CombatAuthorityPolicy.IsValidPlayerId(victimPlayerId)
                || !CombatAuthorityPolicy.IsFinitePosition(
                    msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ))
            {
                ModRuntime.Log?.LogWarning("[FriendlyFire] rejected malformed victim/position");
                return;
            }

            RemotePlayerProxy attackingProxy = _net.GetProxy(atkPlayerId);
            if (attackingProxy == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[FriendlyFire] rejected: no authoritative attacker proxy for p" + atkPlayerId);
                return;
            }

            // Use the host proxy for the actual attacker position. The reported
            // origin is only accepted as a bounded sanity check.
            Vector3 atkPos = attackingProxy.transform.position;
            if (!CombatAuthorityPolicy.IsWithinRange(
                    msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ,
                    atkPos.x, atkPos.y, atkPos.z,
                    GameplayConstants.MaxPlayerAttackRange))
            {
                ModRuntime.Log?.LogWarning(
                    "[FriendlyFire] rejected attacker position outside authoritative range for p"
                    + atkPlayerId);
                return;
            }

            bool victimIsHost = victimPlayerId == _net.LocalPlayerId;
            if (!victimIsHost && _net.GetProxy(victimPlayerId) == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[FriendlyFire] rejected unknown victim player " + victimPlayerId);
                return;
            }

            // Never apply FF to self (attacker == victim) from a bad packet.
            if (victimPlayerId == atkPlayerId)
                return;

            // Night-dead victim: ignore further FF.
            if (victimPlayerId > 0 && DeathStateTracker.IsRemoteNightDead(victimPlayerId))
                return;
            if ((victimPlayerId == _net.LocalPlayerId || victimPlayerId == 0) && DeathStateTracker.LocalNightDeath)
                return;

            int damage = SanitizePeerDamage(msg.Damage, "FriendlyFire");
            if (damage <= 0) return;

            // Debounce only identical same-frame doubles (key includes damage).
            string debounceKey = atkPlayerId + "_" + victimPlayerId + "_" + damage;
            float now = Time.time;
            if (_ffDebounce.TryGetValue(debounceKey, out float last) && now - last < FriendlyFireDebounceSec)
            {
                EntitySyncLog.CombatTrace("ff:deb",
                    "[FriendlyFire] debounced atk=" + atkPlayerId + "→vic=" + victimPlayerId
                    + " dmg=" + damage, 1f);
                return;
            }
            _ffDebounce[debounceKey] = now;

            Transform atkTransform = attackingProxy.transform;

            if (victimIsHost)
            {
                Player host = Player.Instance;
                if (host == null) return;
                host.getHit(damage, atkTransform, msg.CanCutInHalf, byPlayer: true, canInterrupt: true);
                EntitySyncLog.Damage(
                    "[FriendlyFire] host took " + damage + " from p" + atkPlayerId);

                Vector3 hitPoint = host.transform.position;
                Vector3 toHost = (host.transform.position - atkPos).normalized;
                float dist = Vector3.Distance(atkPos, host.transform.position) + 0.5f;
                if (Physics.Raycast(atkPos, toHost, out RaycastHit hostHit, dist, GameplayConstants.HitscanLayerMask))
                    hitPoint = hostHit.point;

                BroadcastFriendlyFireBlood(hitPoint, host.inWater, host.transform.eulerAngles.y);
            }
            else
            {
                EntitySyncLog.Damage(
                    "[FriendlyFire] forward " + damage + " from p" + atkPlayerId
                    + " → victim p" + victimPlayerId);
                _net.SendToPlayer(victimPlayerId, NetMessageType.DamagePlayer, w =>
                {
                    new DamagePlayerMessage
                    {
                        Damage = damage,
                        AttackerPosX = atkPos.x,
                        AttackerPosY = atkPos.y,
                        AttackerPosZ = atkPos.z,
                        CanCutInHalf = msg.CanCutInHalf,
                        ShowRedScreen = true,
                        NormalHit = true,
                        CanInterrupt = true
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                // Blood on victim proxy so all peers see the hit (not only host self-hit path).
                RemotePlayerProxy victimProxy = _net.GetProxy(victimPlayerId);
                if (victimProxy != null)
                {
                    CharBase vicCb = victimProxy.CachedCharBase;
                    bool inWater = vicCb != null && vicCb.inWater;
                    BroadcastFriendlyFireBlood(victimProxy.transform.position, inWater, victimProxy.transform.eulerAngles.y);
                }
            }
        }

        private void BroadcastFriendlyFireBlood(Vector3 hitPoint, bool inWater, float baseRotY)
        {
            string bloodPrefab = inWater ? "FX/Bloodsplats/Shotsplat" : "FX/Bloodsplats/Shotsplat_stay";
            float rotY = baseRotY + UnityEngine.Random.Range(-40f, 40f);
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try { Core.AddPrefab(bloodPrefab, hitPoint, Quaternion.Euler(90f, rotY, 0f), null); }
            finally { TraverseHack.SetExplicitFlag(prevHack); }

            _net.Broadcast(NetMessageType.BulletImpact, w => new BulletImpactMessage
            {
                PrefabName = bloodPrefab,
                PoolName = "",
                PosX = hitPoint.x,
                PosY = hitPoint.y,
                PosZ = hitPoint.z,
                RotX = 90f,
                RotY = rotY,
                RotZ = 0f
            }.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
