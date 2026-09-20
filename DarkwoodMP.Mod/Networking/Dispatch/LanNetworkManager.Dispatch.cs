using System;
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
        /// <summary>Shared LAN/Steam inbound dispatch (type + body after framing byte).</summary>
        private void ProcessInboundMessage(NetMessageType type, byte[] payload)
        {
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
                        ModRuntime.Log?.LogWarning($"[Network] Unhandled message type: {type}");
                    }
                }
                catch (InvalidDataException ex)
                {
                    ModLog.Warn(
                        LogCat.Network,
                        "Rejected malformed " + type + " packet from p"
                        + _currentReceivePlayerId + " (" + (payload != null ? payload.Length : 0)
                        + " bytes): " + ex.Message);
                    return;
                }
                finally
                {
                    _isForwardedMessage = false;
                }
            }

            // === Forward client messages to other clients (3+ support) ===
            if (_suppressForwardThisMessage)
            {
                _suppressForwardThisMessage = false;
                return;
            }

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
