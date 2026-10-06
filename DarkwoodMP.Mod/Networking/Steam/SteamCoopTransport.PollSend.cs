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
            ReportStalledConnections();

            if (_hosting)
            {
                if (_pendingHostConnects.Count > 0)
                    ResolvePendingHostConnects();

                if (_pollGroup != HSteamNetPollGroup.Invalid)
                    DrainMessages(_receiveOnPollGroup ?? (_receiveOnPollGroup = ReceiveOnPollGroup));
            }
            else if (_serverConn != HSteamNetConnection.Invalid)
            {
                DrainMessages(_receiveOnServer ?? (_receiveOnServer = ReceiveOnServer));
            }
        }

        // Cached so the per-frame poll does not allocate a delegate.
        private Func<int> _receiveOnPollGroup;
        private Func<int> _receiveOnServer;

        private int ReceiveOnPollGroup()
            => SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _recvBuffer, MaxMessagesPerPoll);

        private int ReceiveOnServer()
            => SteamNetworkingSockets.ReceiveMessagesOnConnection(_serverConn, _recvBuffer, MaxMessagesPerPoll);

        /// <summary>Stalled peers dropped during a send; reported from Poll, never mid-send.</summary>
        private readonly List<ulong> _stalledSteamIds = new List<ulong>();

        /// <summary>
        /// The owner's failure handling rewrites the peer tables, so it cannot run inside a send that
        /// may itself be iterating them.
        /// </summary>
        private void ReportStalledConnections()
        {
            if (_stalledSteamIds.Count == 0)
                return;
            ulong[] ids = _stalledSteamIds.ToArray();
            _stalledSteamIds.Clear();
            foreach (ulong id in ids)
                _owner.OnSteamSessionFailed(new CSteamID(id));
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

                // Frame = type byte + body: copy the body once, straight out of the Steam buffer.
                byte type = Marshal.ReadByte(msg.m_pData);
                byte[] body = new byte[msg.m_cbSize - 1];
                if (body.Length > 0)
                    Marshal.Copy(IntPtr.Add(msg.m_pData, 1), body, 0, body.Length);
                bool reliable = (msg.m_nFlags & SendReliable) != 0;

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
                _owner.OnSteamPacket(remote, type, body, reliable);
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

        /// <summary>
        /// Forget one connection handle. The steam id mapping is only removed when it still points at
        /// this handle: a peer that reconnected before its old connection timed out already maps to
        /// the new one. Returns whether the steam id's current connection was this one.
        /// </summary>
        private bool UntrackConn(HSteamNetConnection conn, ulong steamId)
        {
            bool current = !_connBySteamId.TryGetValue(steamId, out HSteamNetConnection mapped)
                || mapped == conn;
            if (current)
                _connBySteamId.Remove(steamId);
            _steamIdByConn.Remove(conn.m_HSteamNetConnection);
            _reliableOutbox.Remove(conn.m_HSteamNetConnection);
            return current;
        }

        private bool SendRaw(HSteamNetConnection conn, byte[] data, int length, bool reliable)
        {
            if (conn == HSteamNetConnection.Invalid)
                return false;

            uint key = conn.m_HSteamNetConnection;
            if (reliable && _reliableOutbox.TryGetValue(key, out ReliableOutbox box) && box.Queue.Count > 0)
                return Park(conn, box, data, length);

            EResult result = SendNow(conn, data, length, reliable);
            if (result == EResult.k_EResultLimitExceeded && reliable)
            {
                if (!_reliableOutbox.TryGetValue(key, out box))
                    _reliableOutbox[key] = box = new ReliableOutbox();
                return Park(conn, box, data, length);
            }
            if (result != EResult.k_EResultOK && reliable)
            {
                ModLog.Trace(LogCat.Network,
                    () => "Steam SNS reliable send failed: " + result + " len=" + length);
                return false;
            }
            return result == EResult.k_EResultOK;
        }

        /// <summary>Queue a reliable send behind the backlog; drop the connection once it is stalled.</summary>
        private bool Park(HSteamNetConnection conn, ReliableOutbox box, byte[] data, int length)
        {
            if (box.Bytes + length > MaxReliableOutboxBytes)
            {
                DropStalledConnection(conn, box.Bytes);
                return false;
            }
            box.Queue.Enqueue(Slice(data, length));
            box.Bytes += length;
            return true;
        }

        private void DropStalledConnection(HSteamNetConnection conn, long parkedBytes)
        {
            ulong steamId;
            if (!_steamIdByConn.TryGetValue(conn.m_HSteamNetConnection, out steamId))
                steamId = conn == _serverConn ? _hostSteamId.m_SteamID : 0UL;
            ModLog.Error(LogCat.Network,
                "Steam SNS peer " + steamId + " stalled with " + (parkedBytes / 1024)
                + " KB of reliable sends parked — dropping the connection");
            try
            {
                SteamNetworkingSockets.CloseConnection(conn, CloseReasonGeneric, "send backlog", false);
            }
            catch { /* tear */ }
            bool current = UntrackConn(conn, steamId);
            if (!_hosting && conn == _serverConn)
                _serverConn = HSteamNetConnection.Invalid;
            if (steamId != 0 && current && !_stalledSteamIds.Contains(steamId))
                _stalledSteamIds.Add(steamId);
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
                ReliableOutbox box = item.Value;
                bool known = item.Key == _serverConn.m_HSteamNetConnection
                    || _steamIdByConn.ContainsKey(item.Key);
                if (!known || box.Queue.Count == 0)
                {
                    (remove ?? (remove = new List<uint>())).Add(item.Key);
                    continue;
                }

                while (box.Queue.Count > 0)
                {
                    byte[] payload = box.Queue.Peek();
                    EResult result = SendNow(conn, payload, payload.Length, reliable: true);
                    if (result == EResult.k_EResultLimitExceeded)
                        break;
                    box.Queue.Dequeue();
                    box.Bytes -= payload.Length;
                    if (result != EResult.k_EResultOK)
                    {
                        (remove ?? (remove = new List<uint>())).Add(item.Key);
                        break;
                    }
                }
                if (box.Queue.Count == 0)
                    (remove ?? (remove = new List<uint>())).Add(item.Key);
            }

            if (remove == null)
                return;
            foreach (uint key in remove)
                _reliableOutbox.Remove(key);
        }

    }
}
