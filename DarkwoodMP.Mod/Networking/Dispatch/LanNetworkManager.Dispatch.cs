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
            NetMessageType.PlayerState,
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

        /// <summary>
        /// Host: the generic relay forwards this re-serialised, sender-stamped body instead of the raw
        /// client payload (which could claim another player's id). The relay still follows the
        /// normal rules (no relay if the handler threw, forward kind unchanged).
        /// </summary>
        private void RelayStamped(NetMessageType type, Action<NetWriter> write)
        {
            var w = new NetWriter();
            write(w);
            _relayPayloadOverride = w.CopyData();
        }

        /// <summary>Shared LAN/Steam inbound dispatch (type + body after framing byte).</summary>
        private void ProcessInboundMessage(NetMessageType type, byte[] payload)
        {
            // Host relay of a client message is independent of the host's local apply only for
            // sender-authoritative presence. For everything else a handler that threw means the host
            // never applied the mutation, so fanning it out would desync the clients from the host
            // (the host is the authority). A packet we could not parse (InvalidDataException) is never
            // relayed, and a handler that asked to swallow the forward (_suppressForwardThisMessage)
            // is honored.
            bool relay = true;
            _relayPayloadOverride = null;
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
                finally
                {
                    // Per-message flags never leak into the next message, whatever happened.
                    _isForwardedMessage = false;
                    if (_suppressForwardThisMessage)
                    {
                        _suppressForwardThisMessage = false;
                        relay = false;
                    }
                }
            }

            if (_relayPayloadOverride != null)
            {
                payload = _relayPayloadOverride;
                _relayPayloadOverride = null;
            }
            if (!relay)
                return;

            // Sticky per-client presentation state, replayed to later joiners (late-join bulk only
            // carried the host's own light / anim library, so a client who joined after another
            // client lit a lantern or equipped a weapon saw that player bare).
            if (_role == NetworkRole.Host && _currentReceivePlayerId > 1
                && (type == NetMessageType.PlayerLightState || type == NetMessageType.PlayerAnimLibrary))
                _stickyPlayerPayloads[StickyKey(_currentReceivePlayerId, type)] = payload;

            // === Forward client messages to other clients (3+ support) ===
            if (!_isForwardedMessage && _role == NetworkRole.Host && _currentReceivePlayerId > 0)
            {
                if (_forwardableMap.TryGetValue(type, out var fwdKind))
                {
                    if (fwdKind == ForwardableKind.Direct)
                    {
                        // Direct rebroadcast must be reliable (default SendToAllExcept is Unreliable).
                        // PutRaw is already the message body; adding a length
                        // prefix would break deserializers.
                        SendToAllExcept(_currentReceivePlayerId, type, w => w.PutRaw(payload),
                            DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        var fwd = new RemotePlayerForwardMessage
                        {
                            OriginalPlayerId = _currentReceivePlayerId,
                            InnerType = (byte)type,
                            InnerPayload = payload
                        };
                        SendToAllExcept(_currentReceivePlayerId, NetMessageType.RemotePlayerForward,
                            w => fwd.Serialize(w), DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }
    }
}
