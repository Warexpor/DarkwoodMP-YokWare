using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DWMPHorde.Logging;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking.Steam
{
    /// <summary>
    /// SteamNetworkingSockets sends at a fixed rate: it has no bandwidth estimate, and its rate is
    /// <c>SendRateMin</c> (256 KB/s; <c>SendRateMax</c> only caps an estimate that does not exist).
    /// A world share (about 3 MB) took 11 s or more over Steam at that rate, on links that carry far
    /// more. Each connection's rate is moved here instead: doubled while reliable sends queue up
    /// behind it and the link stays clean, halved when the peer reports loss or the ping climbs
    /// (the link's own queue filling), and back to the base once the backlog has drained.
    /// </summary>
    public sealed partial class SteamCoopTransport
    {
        /// <summary>The rate SNS starts every connection at (the global SendRateMin).</summary>
        internal const int BaseSendRate = 256 * 1024;
        private const int MaxSendRate = 8 * 1024 * 1024;
        private const float RateStepSec = 0.25f;
        /// <summary>Queued reliable bytes, in seconds at the current rate, that count as held back by the rate.</summary>
        private const float BackloggedSec = 0.125f;
        /// <summary>No backlog for this long: the rate goes back to the base.</summary>
        private const float IdleResetSec = 2f;
        /// <summary>Fraction of our packets the peer got, below which the link is losing them.</summary>
        private const float LossQuality = 0.95f;
        /// <summary>After a back-off: the loss report trails the cut, so no second cut before this.</summary>
        private const float BackoffHoldSec = 1f;
        /// <summary>Past the first back-off, between quarter steps up (the loss report needs time to show one).</summary>
        private const float ProbeStepSec = 1f;
        /// <summary>k_ESteamNetworkingConfig_Connection</summary>
        private const int ConfigScopeConnection = 4;
        private const int ConfigSendRateMin = 10;
        private const int ConfigSendRateMax = 11;

        private sealed class SendRateState
        {
            public int Rate = BaseSendRate;
            public int Peak = BaseSendRate;
            public int MinPing = int.MaxValue;
            /// <summary>Past the first back-off the rate grows by a quarter, not double.</summary>
            public bool Probed;
            public float NextStep;
            public float NoCutUntil;
            public float NoRaiseUntil;
            public float LastBacklog;
        }

        private readonly Dictionary<uint, SendRateState> _sendRates = new Dictionary<uint, SendRateState>();
        private List<uint> _sendRateKeys;

        /// <summary>Reliable bytes not yet on the wire to <paramref name="remote"/> (SNS queue plus our parked backlog), or -1.</summary>
        internal long PendingReliableBytes(CSteamID remote)
        {
            if (!TryGetConn(remote, out HSteamNetConnection conn))
                return -1;
            long pending = QueryPending(conn);
            if (pending < 0)
                return -1;
            if (_reliableOutbox.TryGetValue(conn.m_HSteamNetConnection, out ReliableOutbox box))
                pending += box.Bytes;
            return pending;
        }

        /// <summary>The rate this connection sends at now (bytes a second).</summary>
        internal int SendRate(CSteamID remote)
            => TryGetConn(remote, out HSteamNetConnection conn)
                && _sendRates.TryGetValue(conn.m_HSteamNetConnection, out SendRateState s)
                ? s.Rate : BaseSendRate;

        private bool TryGetConn(CSteamID remote, out HSteamNetConnection conn)
        {
            conn = HSteamNetConnection.Invalid;
            if (!_active || !remote.IsValid())
                return false;
            if (_hosting)
                return _connBySteamId.TryGetValue(remote.m_SteamID, out conn);
            if (!_clientTransportReady || _serverConn == HSteamNetConnection.Invalid)
                return false;
            conn = _serverConn;
            return true;
        }

        private static long QueryPending(HSteamNetConnection conn)
        {
            var status = new SteamNetConnectionRealTimeStatus_t();
            var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes) != EResult.k_EResultOK)
                return -1;
            return status.m_cbPendingReliable;
        }

        /// <summary>Every poll: step each connection's rate a few times a second.</summary>
        private void TickSendRates()
        {
            float now = Time.unscaledTime;
            if (_hosting)
            {
                foreach (var kvp in _connBySteamId)
                    StepSendRate(kvp.Value, kvp.Key, now);
            }
            else if (_clientTransportReady && _serverConn != HSteamNetConnection.Invalid)
            {
                StepSendRate(_serverConn, _hostSteamId.m_SteamID, now);
            }

            if (_sendRates.Count == 0)
                return;
            // Forget closed connections.
            (_sendRateKeys ?? (_sendRateKeys = new List<uint>())).Clear();
            foreach (uint key in _sendRates.Keys)
                if (key != _serverConn.m_HSteamNetConnection && !_steamIdByConn.ContainsKey(key))
                    _sendRateKeys.Add(key);
            for (int i = 0; i < _sendRateKeys.Count; i++)
                _sendRates.Remove(_sendRateKeys[i]);
        }

        private void StepSendRate(HSteamNetConnection conn, ulong steamId, float now)
        {
            uint key = conn.m_HSteamNetConnection;
            if (!_sendRates.TryGetValue(key, out SendRateState s))
                _sendRates[key] = s = new SendRateState { NextStep = now, LastBacklog = now };
            if (now < s.NextStep)
                return;
            s.NextStep = now + RateStepSec;

            var status = new SteamNetConnectionRealTimeStatus_t();
            var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes) != EResult.k_EResultOK
                || status.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                return;

            long pending = status.m_cbPendingReliable;
            if (_reliableOutbox.TryGetValue(key, out ReliableOutbox box))
                pending += box.Bytes;
            int ping = status.m_nPing;
            if (ping > 0 && ping < s.MinPing)
                s.MinPing = ping;

            bool backlogged = pending > s.Rate * BackloggedSec;
            if (backlogged)
                s.LastBacklog = now;

            if (s.Rate == BaseSendRate)
            {
                if (!backlogged)
                    return;
            }
            else if (now - s.LastBacklog > IdleResetSec)
            {
                if (s.Peak > BaseSendRate)
                    ModLog.Event(LogCat.Network, "[SendRate] " + steamId + " backlog drained: peak "
                        + (s.Peak / 1024) + " KB/s, back to " + (BaseSendRate / 1024) + " KB/s");
                ApplyRate(conn, s, BaseSendRate);
                s.Peak = BaseSendRate;
                s.Probed = false;
                s.MinPing = int.MaxValue;
                return;
            }

            // The peer's report of our packets (-1 until it sent one), and the link's queue in the ping.
            float quality = status.m_flConnectionQualityRemote;
            bool losing = quality >= 0f && quality < LossQuality;
            bool queueing = s.MinPing != int.MaxValue && ping > s.MinPing * 2 + 50;
            if (losing || queueing)
            {
                if (now < s.NoCutUntil)
                    return;
                int down = Math.Max(BaseSendRate, s.Rate / 2);
                if (down != s.Rate)
                    ModLog.Event(LogCat.Network, "[SendRate] " + steamId + " "
                        + (losing ? "loss (quality " + quality.ToString("0.00") + ")" : "ping " + ping + " ms (min " + s.MinPing + ")")
                        + ": " + (s.Rate / 1024) + " -> " + (down / 1024) + " KB/s");
                ApplyRate(conn, s, down);
                s.Probed = true;
                s.NoCutUntil = now + BackoffHoldSec;
                s.NoRaiseUntil = now + BackoffHoldSec;
                return;
            }
            if (!backlogged || now < s.NoRaiseUntil || s.Rate >= MaxSendRate)
                return;
            int up = s.Probed ? s.Rate + s.Rate / 4 : s.Rate * 2;
            ApplyRate(conn, s, Math.Min(MaxSendRate, up));
            if (s.Probed)
                s.NoRaiseUntil = now + ProbeStepSec;
        }

        private static void ApplyRate(HSteamNetConnection conn, SendRateState s, int rate)
        {
            if (rate == s.Rate)
                return;
            IntPtr target = (IntPtr)(long)conn.m_HSteamNetConnection;
            if (!SetConnectionInt(target, ConfigSendRateMin, rate) || !SetConnectionInt(target, ConfigSendRateMax, rate))
                return;
            s.Rate = rate;
            if (rate > s.Peak)
                s.Peak = rate;
        }

        private static bool SetConnectionInt(IntPtr conn, int key, int value)
        {
            GCHandle pin = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                return SteamNetworkingUtils.SetConfigValue(
                    (ESteamNetworkingConfigValue)key,
                    (ESteamNetworkingConfigScope)ConfigScopeConnection,
                    conn,
                    (ESteamNetworkingConfigDataType)1,
                    pin.AddrOfPinnedObject());
            }
            catch (Exception ex)
            {
                if (NetLogThrottle.ShouldLog("sns-rate", 10f, out int dropped))
                    ModLog.Warn(LogCat.Network, "[SendRate] SetConfigValue failed: " + ex.Message
                        + NetLogThrottle.SuppressedSuffix(dropped));
                return false;
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
