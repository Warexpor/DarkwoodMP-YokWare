using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Logging;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>What the host decides about a client that reports AlreadyInWorld.</summary>
    internal enum PeerWorldVerdict
    {
        /// <summary>Same campaign and chapter (or the host has no world identity yet).</summary>
        Match,
        /// <summary>Same campaign, different chapter: re-share the host world to that peer.</summary>
        ResyncChapter,
        /// <summary>Different or unknown campaign, or a resync that did not converge: refuse.</summary>
        Reject
    }

    /// <summary>
    /// Handshake world identity (campaign + chapter) and stable PlayerId across a chapter
    /// resume. AlreadyInWorld is a claim; the host verifies it instead of trusting it.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>Host: seconds a rejected peer gets to leave on its own before the host drops it.</summary>
        private const float RejectedPeerDropDelaySec = 3f;

        /// <summary>Host: stable client key → PlayerId it held before the chapter teardown.</summary>
        private readonly Dictionary<string, int> _resumePlayerIdByKey = new Dictionary<string, int>(8);

        /// <summary>Host: stable keys (or "p{id}") already re-shared once this session.</summary>
        private readonly HashSet<string> _worldResyncedKeys = new HashSet<string>();

        /// <summary>
        /// Identity of the world this machine currently has loaded. <paramref name="mint"/>:
        /// host stamps a CampaignId when missing (same call the world share uses); a client
        /// must never invent one, so it reports what the profile already carries.
        /// </summary>
        internal static void GetLocalWorldIdentity(bool mint, out string campaignId, out int chapterId)
        {
            campaignId = string.Empty;
            chapterId = 0;
            try
            {
                campaignId = (mint
                    ? CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile()
                    : CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile()) ?? string.Empty;

                if (Singleton<WorldGenerator>.Instance != null)
                    chapterId = Singleton<WorldGenerator>.Instance.chapterID;
                if (chapterId <= 0 && Core.currentProfile != null)
                    chapterId = Core.currentProfile.chapter;
            }
            catch
            {
                campaignId = string.Empty;
                chapterId = 0;
            }
        }

        /// <summary>
        /// Host: verify a client's claimed world. Only called when the client says
        /// AlreadyInWorld (title joiners always get the share anyway).
        /// </summary>
        private PeerWorldVerdict VerifyPeerWorldIdentity(int playerId, HandshakeMessage handshake,
            out string reason, out int hostChapter)
        {
            reason = null;
            hostChapter = 0;
            if (Core.mainMenu || Core.currentProfile == null)
                return PeerWorldVerdict.Match; // host has no world of its own to compare against

            GetLocalWorldIdentity(mint: true, out string hostCampaign, out hostChapter);
            if (string.IsNullOrEmpty(hostCampaign))
                return PeerWorldVerdict.Match;

            if (string.IsNullOrEmpty(handshake.CampaignId))
            {
                reason = WorldSharePolicy.FormatWrongSave(
                    "your loaded world has no co-op campaign id — load your co-op copy of the host's world, "
                    + "or leave and rejoin from the title screen to download it");
                return PeerWorldVerdict.Reject;
            }

            if (!string.Equals(handshake.CampaignId, hostCampaign, System.StringComparison.OrdinalIgnoreCase))
            {
                reason = WorldSharePolicy.FormatWrongSave(
                    "your loaded world belongs to a different campaign than the host's — "
                    + "leave and rejoin from the title screen to download the host's world");
                return PeerWorldVerdict.Reject;
            }

            if (hostChapter > 0 && handshake.ChapterId > 0 && handshake.ChapterId != hostChapter)
            {
                string key = ClientStateBackup.SanitizeStableClientKey(handshake.StableClientKey)
                    ?? "p" + playerId;
                if (!_worldResyncedKeys.Add(key))
                {
                    reason = WorldSharePolicy.FormatWrongSave(
                        "your world is still chapter " + handshake.ChapterId + " after a resync; the host is in chapter "
                        + hostChapter + " — leave and rejoin from the title screen");
                    return PeerWorldVerdict.Reject;
                }
                reason = "client chapter " + handshake.ChapterId + " != host chapter " + hostChapter;
                return PeerWorldVerdict.ResyncChapter;
            }

            return PeerWorldVerdict.Match;
        }

        /// <summary>Host: tell one peer to expect a world share for the host's chapter and load it on its own.</summary>
        private void SendPeerWorldResync(int playerId, int hostChapter)
        {
            SendToPlayer(playerId, NetMessageType.ChapterTransition, w => new ChapterTransitionMessage
            {
                ChapterId = hostChapter,
                LoadChapterSave = false,
                ExpectWorldShare = true,
                AckRequired = false
            }.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Host: refuse a peer with a player-visible reason. The peer leaves on its own when it
        /// gets ChapterLoadGo(Proceed=false); the host drops it after a short grace if it does not.
        /// </summary>
        internal void RejectPeerWorld(int playerId, int chapterId, string reason)
        {
            if (_role != NetworkRole.Host || playerId <= 0 || !HasPeer(playerId))
                return;
            ModLog.Warn(LogCat.Session, "Peer " + playerId + " refused: " + reason);
            // From here on the peer is only waiting to leave: drop its traffic and stop streaming to it.
            _rejectedPeers.Add(playerId);
            if (_handshakedPeers.Remove(playerId))
                _handshakeComplete = _handshakedPeers.Count > 0;
            SendToPlayer(playerId, NetMessageType.ChapterLoadGo, w => new ChapterLoadGoMessage
            {
                ChapterId = chapterId,
                Proceed = false,
                Reason = reason
            }.Serialize(w), DeliveryMethod.ReliableOrdered);
            StartCoroutine(DropPeerAfterGrace(playerId, reason));
        }

        private IEnumerator DropPeerAfterGrace(int playerId, string reason)
        {
            float t = 0f;
            while (t < RejectedPeerDropDelaySec)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (_role != NetworkRole.Host || !HasPeer(playerId))
                yield break;
            ModLog.Warn(LogCat.Session, "Refused peer " + playerId + " still connected — dropping");
            if (IsSteamSession)
            {
                if (_steamPeers.TryGetValue(playerId, out Steamworks.CSteamID sid))
                {
                    Steam.CloseSession(sid);
                    HandleSteamPeerDisconnected(playerId, reason);
                }
            }
            else if (_peers.TryGetValue(playerId, out NetPeer peer))
            {
                peer.Disconnect();
            }
        }

        // --- stable PlayerId across a chapter resume ---

        /// <summary>Host: stable client key → current PlayerId for every keyed peer (before teardown).</summary>
        internal Dictionary<string, int> SnapshotStableKeyRoster()
        {
            var roster = new Dictionary<string, int>(_stableKeyByPlayer.Count);
            foreach (var kvp in _stableKeyByPlayer)
            {
                if (kvp.Key > 1 && !string.IsNullOrEmpty(kvp.Value) && HasPeer(kvp.Key))
                    roster[kvp.Value] = kvp.Key;
            }
            return roster;
        }

        /// <summary>
        /// Host, right after the resume rehost: remember each client's previous PlayerId by
        /// stable key and keep fresh provisional ids above them so an unrelated newcomer
        /// cannot take an id a returning client still owns.
        /// </summary>
        internal void ReservePlayerIdsForResume(Dictionary<string, int> roster)
        {
            _resumePlayerIdByKey.Clear();
            if (roster == null || roster.Count == 0)
                return;
            int max = 1;
            foreach (var kvp in roster)
            {
                if (kvp.Value <= 1 || string.IsNullOrEmpty(kvp.Key))
                    continue;
                _resumePlayerIdByKey[kvp.Key] = kvp.Value;
                if (kvp.Value > max) max = kvp.Value;
            }
            if (max >= _nextPlayerId)
                _nextPlayerId = max + 1;
            ModLog.Event(LogCat.Session,
                "[ChapterResume] reserved " + _resumePlayerIdByKey.Count + " PlayerId(s) for returning clients (next="
                + _nextPlayerId + ")");
        }

        /// <summary>Host: the PlayerId this stable key held before the chapter teardown, if still free.</summary>
        private bool TryTakeResumePlayerId(string rawStableKey, int provisionalId, out int previousId)
        {
            previousId = 0;
            if (_resumePlayerIdByKey.Count == 0)
                return false;
            string key = ClientStateBackup.SanitizeStableClientKey(rawStableKey);
            if (string.IsNullOrEmpty(key) || !_resumePlayerIdByKey.TryGetValue(key, out int wanted))
                return false;
            _resumePlayerIdByKey.Remove(key);
            if (wanted == provisionalId)
                return false;
            if (wanted <= 1 || wanted == _localPlayerId || HasPeer(wanted))
            {
                ModLog.Warn(LogCat.Session,
                    "[ChapterResume] previous PlayerId " + wanted + " unavailable — keeping p" + provisionalId);
                return false;
            }
            previousId = wanted;
            return true;
        }

        private void ResetWorldIdentityState()
        {
            _resumePlayerIdByKey.Clear();
            _worldResyncedKeys.Clear();
        }
    }
}
