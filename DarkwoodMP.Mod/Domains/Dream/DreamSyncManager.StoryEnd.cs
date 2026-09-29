using System;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static partial class DreamSyncManager
    {
        /// <summary>Host chain: allow the next pocket's initiateEndDreaming to fan DreamEnded.</summary>
        public static void ClearDreamEndBroadcastLatch()
        {
            _dreamEndBroadcastSent = false;
        }

        /// <summary>
        /// True when the named outcome's effects include transferToDream (next pocket).
        /// Used so initiateEndDreaming does not DreamSession.End before the chain —
        /// that Idle+TryBegin path wiped the death roster via OnDreamStarted.
        /// </summary>
        public static bool OutcomeChainsToNextDream(Dreams dreams, string outcomeName)
        {
            if (dreams?.preset?.outcomes == null) return false;
            DreamPreset.Outcome match = null;
            string want = outcomeName ?? "";
            for (int i = 0; i < dreams.preset.outcomes.Count; i++)
            {
                var oc = dreams.preset.outcomes[i];
                if (oc != null && oc.name == want)
                {
                    match = oc;
                    break;
                }
            }
            if (match == null)
            {
                for (int i = 0; i < dreams.preset.outcomes.Count; i++)
                {
                    var oc = dreams.preset.outcomes[i];
                    if (oc != null && oc.name == "default")
                    {
                        match = oc;
                        break;
                    }
                }
            }
            if (match?.effects == null) return false;
            for (int i = 0; i < match.effects.Count; i++)
            {
                var e = match.effects[i];
                if (e != null && e.type == DreamPreset.Outcome.Effect.Type.transferToDream
                    && e.destPrefab != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Host story exit: notify peers at initiateEndDreaming so they play the same
        /// outcome video in parallel (DreamEnded used to arrive only after the video).
        /// </summary>
        public static void NotifyPeersStoryEndBeginning(string presetName, string outcomeName)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (_dreamEndBroadcastSent)
                return;
            if (string.IsNullOrEmpty(outcomeName) || outcomeName == "playerDeath")
                return;
            if (DreamSession.IsRejectedOutcome(outcomeName))
                return;

            bool chains = OutcomeChainsToNextDream(Dreams.Instance, outcomeName);
            // Dedupe this initiateEndDreaming fan-out. Chain clears the flag on SetChainedPreset
            // so the next pocket's real exit can broadcast again.
            _dreamEndBroadcastSent = true;
            // Chain: session stays Active so SetChainedPreset keeps the death roster.
            // Peers still get DreamEnded to play the same outcome transition + wantToSwitchDream.
            if (DreamSession.IsActive && !chains)
                DreamSession.End(outcomeName);

            string resolved = !string.IsNullOrEmpty(presetName)
                ? presetName
                : ResolveActivePresetName();
            var ended = DreamEndedMessage.Build(resolved ?? "", outcomeName);
            net.Broadcast(NetMessageType.DreamEnded,
                w => ended.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                "[DreamSync] Host broadcast DreamEnded at initiateEndDreaming outcome="
                + outcomeName + (chains ? " (chain — session kept)" : ""));
        }

        /// <summary>
        /// Client: the host ordered a story exit. Play the vanilla outcome transition, then endDreaming.
        /// </summary>
        public static bool TryBeginHostOrderedStoryEnd(string outcomeName)
        {
            if (string.IsNullOrEmpty(outcomeName) || outcomeName == "playerDeath")
                return false;
            if (DreamSession.IsRejectedOutcome(outcomeName))
                return false;
            var dreams = Dreams.Instance;
            if (dreams == null || !dreams.dreaming)
                return false;

            ClearStoryEndDefer();
            _hostOrderedDreamEnd = true;
            dreams.outcome = outcomeName;
            ModRuntime.LegacyInfo(
                "[DreamSync] Host-ordered story end — playing exit transition outcome="
                + outcomeName);
            try
            {
                dreams.initiateEndDreaming();
                return true;
            }
            catch (Exception ex)
            {
                _hostOrderedDreamEnd = false;
                ModRuntime.Log?.LogWarning(
                    "[DreamSync] Host-ordered initiateEndDreaming failed: " + ex.Message);
                return false;
            }
        }
    }
}
