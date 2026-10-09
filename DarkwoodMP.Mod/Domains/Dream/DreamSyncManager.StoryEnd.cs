using System;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static partial class DreamSyncManager
    {
        private static string _hostEndClaimedPreset; // reset-in: ResetHostEndClaim (dream end, chain, network stop)

        /// <summary>
        /// Host: the one vanilla initiateEndDreaming of this pocket. A peer's story step reaches
        /// the host twice (its dialogue outcome or area fires here too, and the peer asks the host
        /// to end the dream); vanilla runs initiateEndDreaming once, and a second run after a chain
        /// ended the next pocket with this one's outcome.
        /// </summary>
        public static bool TryClaimHostEnd(string preset)
        {
            string key = preset ?? "";
            if (_hostEndClaimedPreset != null && string.Equals(_hostEndClaimedPreset, key, StringComparison.OrdinalIgnoreCase))
                return false;
            _hostEndClaimedPreset = key;
            return true;
        }

        public static void ResetHostEndClaim() => _hostEndClaimedPreset = null;

        /// <summary>Host: this pocket's story end has begun (DreamEnded went out for it).</summary>
        public static bool HostStoryEndStarted => _dreamEndBroadcastSent;

        /// <summary>Host chain: allow the next pocket's initiateEndDreaming to fan DreamEnded.</summary>
        public static void ClearDreamEndBroadcastLatch()
        {
            _dreamEndBroadcastSent = false;
        }

        /// <summary>
        /// The host-ordered exit transition finished or chained into the next pocket; the flag
        /// must not outlive it (initiateEndDreaming authority patch reads it).
        /// </summary>
        public static void ClearHostOrderedDreamEnd()
        {
            _hostOrderedDreamEnd = false;
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
        private static bool IsStoryOutcome(string outcomeName)
            => !string.IsNullOrEmpty(outcomeName) && outcomeName != "playerDeath";

        public static void NotifyPeersStoryEndBeginning(string presetName, string outcomeName)
        {
            if (!NetGuard.ConnectedHost(out var net))
                return;
            if (_dreamEndBroadcastSent)
                return;
            if (!IsStoryOutcome(outcomeName))
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
            if (!IsStoryOutcome(outcomeName))
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
                $"[DreamSync] Host-ordered story end — playing exit transition outcome={outcomeName}");
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
