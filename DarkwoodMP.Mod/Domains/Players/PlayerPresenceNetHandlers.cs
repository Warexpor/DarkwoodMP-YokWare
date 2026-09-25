using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Trap occupancy / entity snapshot / corpse setup composed for 0.8.</summary>
    internal sealed class PlayerPresenceNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal PlayerPresenceNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>Any remote currently reports InBearTrap; prefer <see cref="IsTrapOccupied"/>.</summary>
        internal bool HasAnyTrappedPlayer => _net.RemotePlayers.Values.Any(s => s.InBearTrap);

        /// <summary>True when the remote peer has shadow protection (torch, lantern, LightArea, etc.).</summary>
        internal bool IsRemotePlayerHasLightProtection(int playerId) => _net.RemotePlayers.TryGetValue(playerId, out var state) && state.HasLightProtection;

        /// <summary>playerId → TrapNetId for remotes currently in a trap.</summary>
        public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>> EnumerateRemoteTrapOccupancy()
        {
            foreach (var kv in _net.RemotePlayers)
            {
                if (kv.Value != null && kv.Value.InBearTrap && kv.Value.TrapNetId > 0)
                    yield return new System.Collections.Generic.KeyValuePair<int, int>(kv.Key, kv.Value.TrapNetId);
            }
        }

        /// <summary>
        /// Per-trap occupancy: block loot/disarm only for the trap a player is locked in.
        /// Local + remotes; falls back to position proximity if TrapNetId missing.
        /// </summary>
        internal bool IsTrapOccupied(GameObject trapGo)
        {
            if (trapGo == null) return false;

            int trapId = Sync.TrapNetworkId.GetId(trapGo);
            Vector3 trapPos = trapGo.transform.position;

            Player local = Player.Instance;
            if (local != null && local.inBearTrap)
            {
                int localTrap = Sync.TrapNetworkId.ResolveOccupyingTrapId(local.transform.position,
                    hostMint: _net.Role == NetworkRole.Host);
                if (trapId > 0 && localTrap == trapId)
                    return true;
                if (trapId <= 0 && (local.transform.position - trapPos).sqrMagnitude < 4f)
                    return true;
            }

            foreach (var kv in _net.RemotePlayers)
            {
                var st = kv.Value;
                if (st == null || !st.InBearTrap)
                    continue;
                if (trapId > 0 && st.TrapNetId == trapId)
                    return true;
                if (st.TrapNetId <= 0 && (st.BearTrapPos - trapPos).sqrMagnitude < 6.25f)
                    return true;
            }

            return false;
        }

        internal bool IsRemotePlayerTrappedNear(Vector3 trapPos)
        {
            // Prefer GO-based occupancy when a trap exists at pos.
            GameObject go = Sync.WorldPhysicsSyncService.FindTrapByPos(trapPos);
            if (go != null)
                return IsTrapOccupied(go);

            foreach (var st in _net.RemotePlayers.Values)
            {
                if (st != null && st.InBearTrap && (st.BearTrapPos - trapPos).sqrMagnitude < 6.25f)
                    return true;
            }
            return false;
        }

        internal void HandleEntityState(EntityStateMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ClientEntityInterpolationService.ApplySnapshot(msg);
            sw.Stop();
            ClientPerfProbe.NoteEntityApply(
                ClientEntityInterpolationService.LastApplyCount,
                ClientEntityInterpolationService.LastSkippedCount,
                sw.Elapsed.TotalMilliseconds);

            // Corpse scans run from Update rather than the packet receive path.
            // See TickClientCorpseSetup from Update.
        }

        private float _nextDeadCorpseScanTime;

        /// <summary>
        /// Client: set up deathDrop Item on host-dead NPCs (AI die does not run locally).
        /// Called from Update, not from EntityState RX (avoids poll hitch every 2s).
        /// Waits for death anim (or CorpseFinalizeDelay) so pose is not frozen mid-clip.
        /// </summary>
        internal void TickClientCorpseSetup()
        {
            if (_net.Role != NetworkRole.Client) return;
            if (Time.unscaledTime < _nextDeadCorpseScanTime) return;
            _nextDeadCorpseScanTime = Time.unscaledTime + 2f;

            int n = CharacterTracker.CopyAll(out Character[] all);
            if (n == 0) return;

            for (int i = 0; i < n; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                if (c.alive || c.Health > 0f) continue;
                if (c.GetComponent<Item>() != null) continue;

                // Arm deferred corpse if the death presentation missed (late join).
                ClientEntityInterpolationService.NoteClientDeathForCorpse(c);
                if (!ClientEntityInterpolationService.ShouldFinalizeClientCorpse(c))
                    continue;

                Item item = c.gameObject.AddComponent<Item>();
                item.name = c.name.ToLower() + "_corpse";
                if (c.searched)
                    item.searched = true;

                if (c.inventory != null)
                    c.inventory.invType = Inventory.InvType.deathDrop;

                ClientEntityInterpolationService.ApplyDeathPose(c);
                c.isActive = false;
                ClientEntityInterpolationService.ClearPendingCorpse(c);

                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Death] Set up corpse for '{c.name}' at {c.transform.position}");
            }
        }
    }
}
