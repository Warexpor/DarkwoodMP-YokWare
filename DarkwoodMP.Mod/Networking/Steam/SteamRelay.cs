using System;
using System.Runtime.InteropServices;
using DWMPHorde.Logging;
using Steamworks;

namespace DWMPHorde.Networking.Steam
{
    /// <summary>
    /// One-shot SteamNetworkingSockets relay warm + send-buffer knobs (friend Yokyy SNS path).
    /// </summary>
    public static class SteamRelay
    {
        private static bool _warmed;

        public static void WarmRelay()
        {
            if (_warmed)
                return;
            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                // Friend DLL: send buffer 4MB, rate 256KB–4MB/s, timeout 90s.
                SetGlobalInt((ESteamNetworkingConfigValue)9, 4194304);
                SetGlobalInt((ESteamNetworkingConfigValue)10, 262144);
                SetGlobalInt((ESteamNetworkingConfigValue)11, 4194304);
                SetGlobalInt((ESteamNetworkingConfigValue)25, 90000);
                _warmed = true;
                ModLog.Event(LogCat.Network,
                    "Steam SNS relay warmed (send buf 4MB, rate 256KB-4MB/s, timeout 90s)");
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Steam SNS WarmRelay: " + ex.Message);
            }
        }

        private static void SetGlobalInt(ESteamNetworkingConfigValue key, int value)
        {
            try
            {
                GCHandle pin = GCHandle.Alloc(value, GCHandleType.Pinned);
                bool ok;
                try
                {
                    ok = SteamNetworkingUtils.SetConfigValue(
                        key,
                        (ESteamNetworkingConfigScope)1,
                        IntPtr.Zero,
                        (ESteamNetworkingConfigDataType)1,
                        pin.AddrOfPinnedObject());
                }
                finally
                {
                    pin.Free();
                }
                if (!ok)
                    ModLog.Warn(LogCat.Network, "Steam SetConfigValue refused: " + key + "=" + value);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Steam config " + key + ": " + ex.Message);
            }
        }
    }
}
