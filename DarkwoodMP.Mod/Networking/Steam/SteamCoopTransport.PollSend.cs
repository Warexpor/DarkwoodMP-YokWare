using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking.Steam
{
    /// <summary>
    /// Steam lobbies + SteamNetworkingSockets P2P for YokWare co-op.
    /// Same application framing as LAN (byte type + body); no LiteNetLib on this path.
    /// Requires the game's existing SteamManager (SteamAPI already Init).
    /// </summary>
    public sealed partial class SteamCoopTransport
    {
        public void Poll()
        {
            if (!_active)
                return;

            if (!_hosting && !_clientTransportReady
                && _serverConn != HSteamNetConnection.Invalid
                && _clientConnectStartedUtc != DateTime.MinValue
                && DateTime.UtcNow - _clientConnectStartedUtc > ClientConnectTimeout)
            {
                ModLog.Warn(LogCat.Network,
                    "Steam SNS connect timeout after " + ClientConnectTimeout.TotalSeconds + "s");
                _owner.OnSteamLobbyFailed("SNS timeout");
                return;
            }

            FlushReliableOutbox();

            if (_hosting)
            {
                if (_pendingHostConnects.Count > 0)
                    ResolvePendingHostConnects();

                if (_pollGroup != HSteamNetPollGroup.Invalid)
                    DrainMessages(() => SteamNetworkingSockets.ReceiveMessagesOnPollGroup(
                        _pollGroup, _recvBuffer, MaxMessagesPerPoll));
            }
            else if (_serverConn != HSteamNetConnection.Invalid)
            {
                DrainMessages(() => SteamNetworkingSockets.ReceiveMessagesOnConnection(
                    _serverConn, _recvBuffer, MaxMessagesPerPoll));
            }
        }

        private void DrainMessages(Func<int> receive)
        {
            int n;
            do
            {
                try { n = receive(); }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Network, "Steam SNS receive: " + ex.Message);
                    break;
                }
                if (n <= 0)
                    break;
                for (int i = 0; i < n; i++)
                    HandleMessage(_recvBuffer[i]);
            } while (n >= MaxMessagesPerPoll);
        }

        private void HandleMessage(IntPtr ptr)
        {
            try
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                if (msg.m_cbSize < 1)
                    return;

                byte[] payload = new byte[msg.m_cbSize];
                Marshal.Copy(msg.m_pData, payload, 0, msg.m_cbSize);

                CSteamID remote = CSteamID.Nil;
                if (_hosting)
                {
                    if (_steamIdByConn.TryGetValue(msg.m_conn.m_HSteamNetConnection, out ulong sid))
                        remote = new CSteamID(sid);
                    else
                    {
                        SteamNetConnectionInfo_t info;
                        if (SteamNetworkingSockets.GetConnectionInfo(msg.m_conn, out info))
                            remote = info.m_identityRemote.GetSteamID();
                    }
                }
                else
                {
                    remote = _hostSteamId;
                }

                if (!remote.IsValid())
                    return;
                _owner.OnSteamPacket(remote, payload);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Steam SNS message: " + ex.Message);
            }
            finally
            {
                SteamNetworkingMessage_t.Release(ptr);
            }
        }

        public bool Send(CSteamID remote, byte[] data, DeliveryMethod method)
            => Send(remote, data, data != null ? data.Length : 0, method);

        /// <summary>Send a prefix of <paramref name="data"/> (recycled hot buffers often have spare capacity).</summary>
        public bool Send(CSteamID remote, byte[] data, int length, DeliveryMethod method)
        {
            if (!_active || !remote.IsValid() || data == null || length <= 0)
                return false;
            if (length > data.Length)
                length = data.Length;

            HSteamNetConnection conn = HSteamNetConnection.Invalid;
            if (_hosting)
            {
                if (!_connBySteamId.TryGetValue(remote.m_SteamID, out conn))
                    return false;
            }
            else
            {
                if (!_clientTransportReady || _serverConn == HSteamNetConnection.Invalid)
                    return false;
                conn = _serverConn;
            }

            bool reliable = method != DeliveryMethod.Unreliable;
            return SendRaw(conn, data, length, reliable);
        }

        /// <summary>
        /// Classic AcceptP2PSession no longer applies; SNS accept happens in status callback.
        /// Kept so call sites stay stable.
        /// </summary>
        public void AcceptSession(CSteamID remote)
        {
            // No-op; connection accepted on Connecting -> AcceptConnection.
        }

        public void CloseSession(CSteamID remote)
        {
            if (!remote.IsValid())
                return;
            if (!_connBySteamId.TryGetValue(remote.m_SteamID, out HSteamNetConnection conn))
            {
                if (!_hosting && remote == _hostSteamId && _serverConn != HSteamNetConnection.Invalid)
                    conn = _serverConn;
                else
                    return;
            }

            try
            {
                SteamNetworkingSockets.CloseConnection(conn, CloseReasonGeneric, "peer dropped", false);
            }
            catch { /* tear */ }

            UntrackConn(conn, remote.m_SteamID);
            if (!_hosting && conn == _serverConn)
                _serverConn = HSteamNetConnection.Invalid;
        }

        private void UntrackConn(HSteamNetConnection conn, ulong steamId)
        {
            _connBySteamId.Remove(steamId);
            _steamIdByConn.Remove(conn.m_HSteamNetConnection);
            _reliableOutbox.Remove(conn.m_HSteamNetConnection);
        }

        private bool SendRaw(HSteamNetConnection conn, byte[] data, int length, bool reliable)
        {
            if (conn == HSteamNetConnection.Invalid)
                return false;

            uint key = conn.m_HSteamNetConnection;
            if (reliable && _reliableOutbox.TryGetValue(key, out Queue<byte[]> q) && q.Count > 0)
            {
                q.Enqueue(Slice(data, length));
                return true;
            }

            EResult result = SendNow(conn, data, length, reliable);
            if (result == EResult.k_EResultLimitExceeded && reliable)
            {
                if (!_reliableOutbox.TryGetValue(key, out q))
                    _reliableOutbox[key] = q = new Queue<byte[]>();
                q.Enqueue(Slice(data, length));
                return true;
            }
            if (result != EResult.k_EResultOK && reliable)
            {
                ModLog.Trace(LogCat.Network,
                    () => "Steam SNS reliable send failed: " + result + " len=" + length);
                return false;
            }
            return result == EResult.k_EResultOK;
        }

        private static EResult SendNow(HSteamNetConnection conn, byte[] data, int length, bool reliable)
        {
            int flags = reliable ? SendReliable : SendUnreliableNoDelay;
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                long msgNum = 0;
                return SteamNetworkingSockets.SendMessageToConnection(
                    conn, pin.AddrOfPinnedObject(), (uint)length, flags, out msgNum);
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>
        /// Always a private copy: queued reliable sends outlive the call, and the caller's buffer is
        /// often a recycled hot buffer (or a shared packet) that gets overwritten next tick.
        /// </summary>
        private static byte[] Slice(byte[] data, int length)
        {
            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return copy;
        }

        private void FlushReliableOutbox()
        {
            if (_reliableOutbox.Count == 0)
                return;

            List<uint> remove = null;
            foreach (var item in _reliableOutbox)
            {
                var conn = new HSteamNetConnection { m_HSteamNetConnection = item.Key };
                Queue<byte[]> queue = item.Value;
                bool known = item.Key == _serverConn.m_HSteamNetConnection
                    || _steamIdByConn.ContainsKey(item.Key);
                if (!known || queue.Count == 0)
                {
                    (remove ?? (remove = new List<uint>())).Add(item.Key);
                    continue;
                }

                while (queue.Count > 0)
                {
                    byte[] payload = queue.Peek();
                    EResult result = SendNow(conn, payload, payload.Length, reliable: true);
                    if (result == EResult.k_EResultLimitExceeded)
                        break;
                    queue.Dequeue();
                    if (result != EResult.k_EResultOK)
                    {
                        (remove ?? (remove = new List<uint>())).Add(item.Key);
                        break;
                    }
                }
                if (queue.Count == 0)
                    (remove ?? (remove = new List<uint>())).Add(item.Key);
            }

            if (remove == null)
                return;
            foreach (uint key in remove)
                _reliableOutbox.Remove(key);
        }

    }
}
