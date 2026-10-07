using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Throwable / explosion / melee-world-hit combat FX handlers.</summary>
    internal sealed class CombatFxImpactNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const float MeleeHitDebounceSec = 0.2f;
        private readonly Dictionary<string, float> _meleeHitDebounce = new Dictionary<string, float>();
        private readonly List<string> _meleeDebounceStaleKeys = new List<string>(8);

        internal CombatFxImpactNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearMeleeHitDebounce()
        {
            _meleeHitDebounce.Clear();
        }

        /// <summary>Periodic cleanup of stale melee hit debounce entries.</summary>
        internal void TickMeleeHitDebounceCleanup()
        {
            if (_meleeHitDebounce.Count == 0) return;
            float now = Time.time;
            _meleeDebounceStaleKeys.Clear();
            foreach (var kvp in _meleeHitDebounce)
            {
                if (now - kvp.Value > 5f)
                    _meleeDebounceStaleKeys.Add(kvp.Key);
            }
            for (int i = 0; i < _meleeDebounceStaleKeys.Count; i++)
                _meleeHitDebounce.Remove(_meleeDebounceStaleKeys[i]);
        }

        internal void HandleThrowableSpawn(ThrowableSpawnMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            Transform sourceT = proxy != null ? proxy.transform : null;
            bool visualOnly = (_net.Role == NetworkRole.Client);

            // The held flare became this projectile: drop the held copy (no double glow), and
            // ignore the held-flare flag on PlayerState packets sent before the throw that arrive
            // after it (unreliable stream vs this reliable event).
            bool isFlare = !string.IsNullOrEmpty(msg.ItemType)
                && msg.ItemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (isFlare && playerId > 0 && _net.RemotePlayers.TryGetValue(playerId, out var rs))
            {
                rs.HeldFlareThrownAt = Time.unscaledTime;
                if (rs.FlareLight != null || rs.FlareFx != null)
                {
                    ModLog.Event(LogCat.World, $"[LightSync] throw mutex cleared held p{playerId}");
                    _net.DestroyRemoteFlareLight(playerId);
                }
            }

            Sync.WorldPhysicsSyncService.SpawnThrownItem(msg, sourceT, visualOnly);
        }

        internal void HandleExplosionTrigger(ExplosionTriggerMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (_net.Role == NetworkRole.Host)
            {
                // Pass SoundId so host still booms if Explodes is gone / explode() skips audio.
                Sync.WorldPhysicsSyncService.TriggerExplosion(pos, msg.ObjectName, msg.Flaming, msg.SoundId);
            }
            else
            {
                Sync.WorldPhysicsSyncService.SpawnExplosionVisual(pos, msg.ObjectName, msg.PrefabName, msg.SoundId);
            }
        }

        internal void HandleMeleeWorldHit(MeleeWorldHitMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            int playerId = _net.CurrentReceivePlayerId;
            if (!CombatAuthorityPolicy.IsValidPlayerId(playerId)
                || !CombatAuthorityPolicy.IsValidMeleeTargetType(msg.TargetType)
                || !CombatAuthorityPolicy.IsFinitePosition(msg.PosX, msg.PosY, msg.PosZ))
            {
                ModRuntime.Log?.LogWarning("[MeleeWorldHit] rejected malformed action");
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            RemotePlayerProxy attackingProxy = _net.GetProxy(playerId);
            if (attackingProxy == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[MeleeWorldHit] rejected: no authoritative attacker proxy for p" + playerId);
                return;
            }

            Transform attackerT = attackingProxy.transform;
            if (!CombatAuthorityPolicy.IsWithinRange(
                    attackingProxy.transform.position.x,
                    attackingProxy.transform.position.y,
                    attackingProxy.transform.position.z,
                    pos.x, pos.y, pos.z,
                    GameplayConstants.MaxPlayerAttackRange))
            {
                ModRuntime.Log?.LogWarning(
                    "[MeleeWorldHit] rejected target outside authoritative range for p" + playerId);
                return;
            }

            int damage = _net.SanitizePeerDamage(msg.Damage, "MeleeWorldHit");
            if (damage <= 0)
                return;

            // Debounce check for doors/windows: suppress FX for rapid successive
            // hits (shotgun pellets) but still apply damage (normalHit=false).
            bool suppressed = false;
            if (msg.TargetType == 0 || msg.TargetType == 1)
            {
                string key = $"{playerId}_{msg.TargetType}_{pos.x:F1}_{pos.y:F1}_{pos.z:F1}";
                float now = Time.time;
                if (_meleeHitDebounce.TryGetValue(key, out float lastTime) &&
                    (now - lastTime) < MeleeHitDebounceSec)
                    suppressed = true;
                _meleeHitDebounce[key] = now;
            }

            if (msg.TargetType == 0)
            {
                Door door = WorldQueryHelper.FindDoorByPos(pos);
                // Client hit position can drift from the host door pivot; widen once before drop.
                if (door == null)
                    door = WorldQueryHelper.FindDoorByPosLoose(pos, 3f);
                if (door == null)
                {
                    ModRuntime.LegacyInfo($"[MeleeWorldHit] door not found at {pos}");
                    return;
                }
                PlayerWorldHitScope.Run(playerId, () =>
                    door.getHit(damage, attackerT, !suppressed, false));
                return;
            }

            if (msg.TargetType == 1)
            {
                Window window = WorldQueryHelper.FindWindowByPos(pos);
                if (window == null)
                    window = WorldQueryHelper.FindWindowByPosLoose(pos, 3f);
                if (window == null)
                {
                    ModRuntime.LegacyInfo($"[MeleeWorldHit] window not found at {pos}");
                    return;
                }
                PlayerWorldHitScope.Run(playerId, () =>
                    window.getHit(damage, attackerT, !suppressed));
                return;
            }

            if (msg.TargetType == 2)
            {
                // Client hit Y often differs from the host because of body-push or location layers;
                // match on XZ only, at the item's own spot (25 m used to hit the nearest other crate).
                bool hit = false;
                PlayerWorldHitScope.Run(playerId, () =>
                    hit = TryHitDestructibleItemAt(pos, BarricadeNetHandlers.ItemMatchRadius, damage, attackerT));
                if (hit)
                    return;
                ModRuntime.LegacyInfo($"[MeleeWorldHit] destructible item not found at {pos}");
            }
        }

        internal static bool TryHitDestructibleItemAt(Vector3 pos, float radius, int damage, Transform attackerT)
        {
            Item best = WorldQueryHelper.FindDestructibleItemXz(pos, radius);
            if (best == null) return false;
            DialogHostApplyGuard.RunHostWorldFanout(() =>
                best.getHit(damage, attackerT, true));
            return true;
        }

        /// <summary>XZ distance under which an incoming puddle is one already lying here.</summary>
        private const float SameLiquidRadius = 0.5f;

        internal void HandleExplosionSpawnObject(ExplosionSpawnObjectMessage msg)
        {
            if (string.IsNullOrEmpty(msg.PrefabName)) return;
            if (_net.Role == NetworkRole.Client && !LanNetworkManager.ClientCanApplyWorldBulk()) return;
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            // Local Explodes already ran spawnObjects (stomp or SpawnExplosionVisual);
            // skip host-echoed secondaries so the stomper/remote doesn't double debris.
            if (ExplosionSpawnFlagTracker.ShouldSkipExplosionSpawnObject(pos))
            {
                ModRuntime.LegacyInfo($"[ExplosionSpawnRecv] skip (local FX recent) {msg.PrefabName} at {pos}");
                return;
            }
            // SpawnObject often arrives before ExplosionTrigger (same-frame host onActivate).
            // If a local Explodes with secondaries still exists, let SpawnExplosionVisual
            // own spawnObjects(); applying both piles would duplicate white debris on remotes.
            Explodes localExpl = null;
            int nearFxN = Physics.OverlapSphereNonAlloc(pos, 1.5f, WorldQueryHelper.SharedOverlapBuf);
            for (int i = 0; i < nearFxN; i++)
            {
                if (WorldQueryHelper.SharedOverlapBuf[i] == null) continue;
                Explodes e = WorldQueryHelper.SharedOverlapBuf[i].GetComponentInParent<Explodes>();
                if (e != null) { localExpl = e; break; }
            }
            if (localExpl != null && localExpl.spawnObject != null)
            {
                ModRuntime.LegacyInfo($"[ExplosionSpawnRecv] skip (local Explodes owns secondaries) {msg.PrefabName} at {pos}");
                return;
            }
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                string[] prefixes = { "", "Items/", "FX/", "Environment/", "Particles/", "Dummies/", "Fire/", "Weapons/" };
                UnityEngine.Object prefab = null;
                string foundPath = null;
                foreach (var prefix in prefixes)
                {
                    string path = "Prefabs/" + prefix + msg.PrefabName;
                    prefab = Resources.Load(path);
                    if (prefab != null)
                    {
                        foundPath = path;
                        ModRuntime.LegacyInfo($"[ExplosionSpawnRecv] found prefab at {path}");
                        break;
                    }
                }
                // A puddle already lying on this spot (a joiner's world-placed one, or one sent
                // with the join's gas state twice): the same puddle, not a second one.
                GameObject prefabGo = prefab as GameObject;
                if (prefabGo != null && prefabGo.GetComponent<Liquid>() != null
                    && Sync.WorldPhysicsSyncService.HasFlammableLiquidAt(pos, msg.PrefabName, SameLiquidRadius))
                {
                    ModRuntime.LegacyInfo($"[ExplosionSpawnRecv] skip {msg.PrefabName} at {pos}: same puddle already here");
                    return;
                }
                if (prefab != null)
                {
                    Core.AddPrefab(prefab, pos, rot, null, false);
                    ModRuntime.LegacyInfo($"[ExplosionSpawnRecv] spawned {msg.PrefabName} at {pos} rot={rot.eulerAngles} (loaded from {foundPath})");
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[ExplosionSpawnRecv] prefab=" + msg.PrefabName + " NOT FOUND in any prefix at " + pos + " rot=" + rot.eulerAngles + " — falling back to Core.AddPrefab(" + msg.PrefabName + ")");
                    Core.AddPrefab(msg.PrefabName, pos, rot, null, false);
                }
            }
            finally { TraverseHack.SetExplicitFlag(prevHack); }
        }
    }
}
