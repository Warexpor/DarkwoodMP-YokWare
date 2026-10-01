using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        /// <summary>
        /// Message types whose content is the sender's own presence / presentation, not something the
        /// host validates. If the host's local apply of these throws (proxy not built yet, scene
        /// mid-load) the other peers should still get them. Every other forwardable type is a world
        /// mutation the host must have applied before telling anyone else.
        /// </summary>
        private static readonly HashSet<NetMessageType> _senderAuthoritativeRelay = new HashSet<NetMessageType>
        {
            NetMessageType.PlayerLightState,
            NetMessageType.PlayerAnimation,
            NetMessageType.PlayerAnimLibrary,
            NetMessageType.PlayerEffectSync,
            NetMessageType.PlayerAudio,
            NetMessageType.PlayerFiredWeapon,
            NetMessageType.PlayerBurning,
            NetMessageType.BulletImpact,
            NetMessageType.GasTrailSpawn,
        };

        /// <summary>Host: last PlayerLightState / PlayerAnimLibrary body per client (key = playerId, type).</summary>
        private readonly Dictionary<long, byte[]> _stickyPlayerPayloads = new Dictionary<long, byte[]>();

        private static long StickyKey(int playerId, NetMessageType type)
            => ((long)playerId << 8) | (byte)type;

        /// <summary>Host: forget a leaver's cached presentation state.</summary>
        private void ClearStickyPlayerPayloads(int playerId)
        {
            _stickyPlayerPayloads.Remove(StickyKey(playerId, NetMessageType.PlayerLightState));
            _stickyPlayerPayloads.Remove(StickyKey(playerId, NetMessageType.PlayerAnimLibrary));
        }

        /// <summary>
        /// Host → late joiner: every other client's last light state and anim library, wrapped the same
        /// way live relays are (RemotePlayerForward) so the joiner attributes them to the right proxy.
        /// </summary>
        private void ReplayStickyPlayerStateTo(int joinerId)
        {
            if (_stickyPlayerPayloads.Count == 0)
                return;
            foreach (var kv in _stickyPlayerPayloads)
            {
                int origin = (int)(kv.Key >> 8);
                var innerType = (NetMessageType)(byte)(kv.Key & 0xFF);
                if (origin == joinerId || origin <= 1 || !HasPeer(origin) || kv.Value == null)
                    continue;
                var fwd = new RemotePlayerForwardMessage
                {
                    OriginalPlayerId = origin,
                    InnerType = (byte)innerType,
                    InnerPayload = kv.Value
                };
                SendToPlayer(joinerId, NetMessageType.RemotePlayerForward,
                    w => fwd.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>Host: body the generic Forwardable relay sends instead of the raw inbound payload.</summary>
        private byte[] _relayPayloadOverride;

        /// <summary>Delivery method of the message being dispatched (relays keep it).</summary>
        private DeliveryMethod _currentReceiveMethod = DeliveryMethod.ReliableOrdered;

        /// <summary>Delivery method of the message being dispatched; ReliableOrdered outside dispatch.</summary>
        internal DeliveryMethod CurrentReceiveMethod => _currentReceiveMethod;

        /// <summary>
        /// Host: the generic relay forwards this re-serialised, sender-stamped body instead of the raw
        /// client payload (which could claim another player's id). The relay still follows the
        /// normal rules (no relay if the handler threw, forward kind unchanged).
        /// </summary>
        private void RelayStamped(Action<NetWriter> write)
        {
            var w = new NetWriter();
            write(w);
            _relayPayloadOverride = w.CopyData();
        }

        /// <summary>
        /// Relays keep unreliable streams unreliable: re-sending a 30 Hz sample stream reliably would
        /// queue every reliable event behind stale samples on a lossy link.
        /// </summary>
        internal static DeliveryMethod RelayMethodFor(DeliveryMethod inbound)
            => inbound == DeliveryMethod.Unreliable || inbound == DeliveryMethod.Sequenced
                ? inbound
                : DeliveryMethod.ReliableOrdered;

        /// <summary>
        /// Host gate run before any handler: traffic from a refused peer, gameplay traffic from a peer
        /// that has not completed its Handshake, and host-only types sent by a client are dropped.
        /// </summary>
        private bool HostAcceptsInbound(NetMessageType type)
        {
            if (_role != NetworkRole.Host || _currentReceivePlayerId <= 0)
                return true;
            string why = null;
            if (_rejectedPeers.Contains(_currentReceivePlayerId))
                why = "refused peer";
            else if (type != NetMessageType.Handshake && !_handshakedPeers.Contains(_currentReceivePlayerId))
                why = "no handshake yet";
            else if (_hostOnlyTypes.Contains(type))
                why = "host-only type";
            if (why == null)
                return true;
            if (NetLogThrottle.ShouldLog("inbound-gate:" + (int)type + ":" + _currentReceivePlayerId, 10f, out int dropped))
                ModLog.Warn(LogCat.Network,
                    "Dropping " + type + " from p" + _currentReceivePlayerId + " (" + why + ")"
                    + NetLogThrottle.SuppressedSuffix(dropped));
            return false;
        }

        /// <summary>Shared LAN/Steam inbound dispatch (type + body after framing byte).</summary>
        private void ProcessInboundMessage(NetMessageType type, byte[] payload, DeliveryMethod inboundMethod)
        {
            // Per-message state is set here and cleared on the way out, whatever happens, so nothing a
            // handler (or code running outside dispatch) leaves behind applies to the next packet, and
            // CurrentReceivePlayerId is never a stale sender outside dispatch.
            _suppressForwardThisMessage = false;
            _relayPayloadOverride = null;
            _currentReceiveMethod = inboundMethod;
            try
            {
                if (!HostAcceptsInbound(type))
                    return;
                if (!DispatchAndDecideRelay(type, payload, out byte[] relayBody))
                    return;
                RelayToOtherClients(type, relayBody, inboundMethod);
            }
            finally
            {
                _suppressForwardThisMessage = false;
                _relayPayloadOverride = null;
                _currentReceiveMethod = DeliveryMethod.ReliableOrdered;
                _currentReceivePlayerId = -1;
                _currentReceivePeer = null;
            }
        }

        /// <summary>
        /// Runs the handler and decides the relay. Host relay of a client message is independent of the
        /// host's local apply only for sender-authoritative presence; for everything else a handler
        /// that threw means the host never applied the mutation, so fanning it out would desync the
        /// clients from the host. A packet we could not parse (InvalidDataException) or no handler
        /// claimed is never relayed, and a handler that asked to swallow the forward
        /// (_suppressForwardThisMessage) is honored.
        /// </summary>
        private bool DispatchAndDecideRelay(NetMessageType type, byte[] payload, out byte[] relayBody)
        {
            bool relay = true;
            relayBody = payload;
            using (new NetworkApplyGuard())
            {
                try
                {
                    if (!(TryDispatchSession(type, payload)
                        || TryDispatchCombat(type, payload)
                        || TryDispatchPlayers(type, payload)
                        || TryDispatchWorld(type, payload)
                        || TryDispatchDialogueDream(type, payload)))
                    {
                        relay = false;
                        if (NetLogThrottle.ShouldLog("unhandled:" + (int)type, 10f, out int dropped))
                            ModLog.Warn(LogCat.Network,
                                "Unhandled message type: " + type + " (" + (int)type + ")"
                                + NetLogThrottle.SuppressedSuffix(dropped));
                    }
                }
                catch (InvalidDataException ex)
                {
                    relay = false;
                    if (NetLogThrottle.ShouldLog("malformed:" + (int)type + ":" + _currentReceivePlayerId, 5f, out int dropped))
                        ModLog.Warn(
                            LogCat.Network,
                            "Rejected malformed " + type + " packet from p"
                            + _currentReceivePlayerId + " (" + (payload != null ? payload.Length : 0)
                            + " bytes): " + ex.Message + NetLogThrottle.SuppressedSuffix(dropped));
                }
                catch (Exception ex)
                {
                    // One bad handler must not escape PollEvents (aborts the frame for every
                    // peer) or the Steam poll loop.
                    if (_role == NetworkRole.Host && !_senderAuthoritativeRelay.Contains(type))
                        relay = false;
                    // Built only when it will be logged: ex.ToString() per packet was the hot cost.
                    if (NetLogThrottle.ShouldLog("dispatch-ex:" + (int)type, 5f, out int dropped))
                        ModLog.Warn(
                            LogCat.Network,
                            "Handler for " + type + " from p" + _currentReceivePlayerId
                            + " threw (" + (payload != null ? payload.Length : 0) + " bytes)"
                            + (relay ? "" : " — not relayed") + ": " + ex
                            + NetLogThrottle.SuppressedSuffix(dropped));
                }
            }

            if (_suppressForwardThisMessage)
                relay = false;
            if (_relayPayloadOverride != null)
                relayBody = _relayPayloadOverride;
            return relay;
        }

        /// <summary>Host: fan a client message out to the other clients (3+ players).</summary>
        private void RelayToOtherClients(NetMessageType type, byte[] body, DeliveryMethod inboundMethod)
        {
            int sender = _currentReceivePlayerId;
            if (_role != NetworkRole.Host || sender <= 0)
                return;

            // Sticky per-client presentation state, replayed to later joiners (late-join bulk only
            // carried the host's own light / anim library, so a client who joined after another
            // client lit a lantern or equipped a weapon saw that player bare).
            if (sender > 1 && (type == NetMessageType.PlayerLightState || type == NetMessageType.PlayerAnimLibrary))
                _stickyPlayerPayloads[StickyKey(sender, type)] = body;

            if (!_forwardableMap.TryGetValue(type, out var fwdKind))
                return;
            DeliveryMethod method = RelayMethodFor(inboundMethod);
            if (fwdKind == ForwardableKind.Direct)
            {
                // PutRaw is already the message body; a length prefix would break deserializers.
                SendToAllExcept(sender, type, w => w.PutRaw(body), method);
            }
            else
            {
                var fwd = new RemotePlayerForwardMessage
                {
                    OriginalPlayerId = sender,
                    InnerType = (byte)type,
                    InnerPayload = body
                };
                SendToAllExcept(sender, NetMessageType.RemotePlayerForward, w => fwd.Serialize(w), method);
            }
        }
    }
}
