using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host broadcasts bool flag changes. Pending queue + post-send dirty tracking
    /// avoids the old bug where cooldown updated last-sent before the packet went out,
    /// permanently dropping intermediate values.
    /// </summary>
    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(bool))]
    public static class FlagSyncBoolPatch
    {
        private const float CooldownSec = 2f;

        private static readonly Dictionary<string, bool> _lastSentBoolFlags = new Dictionary<string, bool>();
        private static readonly Dictionary<string, float> _lastSendTime = new Dictionary<string, float>();
        private static readonly Dictionary<string, bool> _pendingBoolFlags = new Dictionary<string, bool>();

        /// <summary>
        /// Spatial / per-peer location flags stay local on each peer. Syncing them made
        /// host and client thrash (playtest: player_inFirstHideout true/false every second
        /// → client stutter + hideout logic churn). Story flags still sync.
        /// </summary>
        internal static bool IsLocalOnlyEphemeralFlag(string flagName)
        {
            if (string.IsNullOrEmpty(flagName))
                return false;
            // Vanilla hideout / location bookkeeping (not dialog story choices).
            if (flagName.StartsWith("player_in", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        public static void Reset()
        {
            _lastSentBoolFlags.Clear();
            _lastSendTime.Clear();
            _pendingBoolFlags.Clear();
        }

        /// <summary>Flush pending flag updates whose cooldown has expired. Call from host or client tick.</summary>
        public static void TickFlush()
        {
            if (_pendingBoolFlags.Count == 0) return;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (net.Role != NetworkRole.Host && net.Role != NetworkRole.Client) return;

            float now = UnityEngine.Time.time;
            var toSend = new List<string>();
            foreach (var kvp in _pendingBoolFlags)
            {
                if (_lastSendTime.TryGetValue(kvp.Key, out float lastSent) && now - lastSent < CooldownSec)
                    continue;
                toSend.Add(kvp.Key);
            }

            foreach (string name in toSend)
            {
                if (!_pendingBoolFlags.TryGetValue(name, out bool value))
                    continue;
                _pendingBoolFlags.Remove(name);

                if (IsLocalOnlyEphemeralFlag(name))
                    continue;

                // Skip if we already successfully sent this value
                if (_lastSentBoolFlags.TryGetValue(name, out bool last) && last == value)
                    continue;

                TrySend(net, name, value, now);
            }
        }

        private static void Postfix(object[] __args)
        {
            string flagName = (string)__args[0];
            bool newValue = (bool)__args[1];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            // Host broadcasts; client sends to host (audit H1 bidirectional story flags).
            if (net == null || (net.Role != NetworkRole.Host && net.Role != NetworkRole.Client))
                return;

            // Never network spatial/location flags (local-only on each peer).
            if (IsLocalOnlyEphemeralFlag(flagName))
                return;

            // Already successfully sent this value
            if (_lastSentBoolFlags.TryGetValue(flagName, out bool lastValue) && lastValue == newValue)
            {
                _pendingBoolFlags.Remove(flagName);
                return;
            }

            float now = UnityEngine.Time.time;
            if (_lastSendTime.TryGetValue(flagName, out float lastSent) && now - lastSent < CooldownSec)
            {
                // Queue desired value; do NOT mark as sent until flush succeeds
                _pendingBoolFlags[flagName] = newValue;
                return;
            }

            TrySend(net, flagName, newValue, now);
        }

        private static void TrySend(LanNetworkManager net, string flagName, bool newValue, float now)
        {
            if (IsLocalOnlyEphemeralFlag(flagName))
                return;

            var msg = new FlagSyncMessage { Name = flagName, IsInt = false, BoolValue = newValue, IntValue = 0 };
            // Reliable: story/dialog flags must not be dropped mid-cooldown window
            if (net.Role == NetworkRole.Host)
                net.Broadcast(NetMessageType.FlagSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            else
                net.Send(NetMessageType.FlagSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            _lastSentBoolFlags[flagName] = newValue;
            _lastSendTime[flagName] = now;
            _pendingBoolFlags.Remove(flagName);
        }
    }

    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(int))]
    public static class FlagSyncIntPatch
    {
        private const float CooldownSec = 2f;

        private static readonly Dictionary<string, int> _lastSentIntFlags = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> _lastSendTime = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> _pendingIntFlags = new Dictionary<string, int>();

        public static void Reset()
        {
            _lastSentIntFlags.Clear();
            _lastSendTime.Clear();
            _pendingIntFlags.Clear();
        }

        public static void TickFlush()
        {
            if (_pendingIntFlags.Count == 0) return;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (net.Role != NetworkRole.Host && net.Role != NetworkRole.Client) return;

            float now = UnityEngine.Time.time;
            var toSend = new List<string>();
            foreach (var kvp in _pendingIntFlags)
            {
                if (_lastSendTime.TryGetValue(kvp.Key, out float lastSent) && now - lastSent < CooldownSec)
                    continue;
                toSend.Add(kvp.Key);
            }

            foreach (string name in toSend)
            {
                if (!_pendingIntFlags.TryGetValue(name, out int value))
                    continue;
                _pendingIntFlags.Remove(name);

                if (_lastSentIntFlags.TryGetValue(name, out int last) && last == value)
                    continue;

                TrySend(net, name, value, now);
            }
        }

        private static void Postfix(object[] __args)
        {
            string flagName = (string)__args[0];
            int newValue = (int)__args[1];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || (net.Role != NetworkRole.Host && net.Role != NetworkRole.Client))
                return;

            if (_lastSentIntFlags.TryGetValue(flagName, out int lastValue) && lastValue == newValue)
            {
                _pendingIntFlags.Remove(flagName);
                return;
            }

            float now = UnityEngine.Time.time;
            if (_lastSendTime.TryGetValue(flagName, out float lastSent) && now - lastSent < CooldownSec)
            {
                _pendingIntFlags[flagName] = newValue;
                return;
            }

            TrySend(net, flagName, newValue, now);
        }

        private static void TrySend(LanNetworkManager net, string flagName, int newValue, float now)
        {
            var msg = new FlagSyncMessage { Name = flagName, IsInt = true, BoolValue = false, IntValue = newValue };
            if (net.Role == NetworkRole.Host)
                net.Broadcast(NetMessageType.FlagSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            else
                net.Send(NetMessageType.FlagSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            _lastSentIntFlags[flagName] = newValue;
            _lastSendTime[flagName] = now;
            _pendingIntFlags.Remove(flagName);
        }
    }
}
