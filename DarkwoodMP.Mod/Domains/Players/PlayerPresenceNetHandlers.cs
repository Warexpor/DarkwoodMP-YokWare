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

                ClientEntityInterpolationService.FinalizeClientCorpse(c);

                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Death] Set up corpse for '{c.name}' at {c.transform.position}");
            }
        }
    }
}
