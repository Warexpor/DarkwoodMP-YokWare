using System;
using System.Collections.Generic;
using DWMPHorde.Logging;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Registry of static state resets that must run when the network stops.
    /// Patches and services register their cleanup methods here instead of
    /// being called individually in StopNetwork().
    /// </summary>
    internal static class NetworkResetRegistry
    {
        private static readonly List<Action> _resets = new List<Action>();

        public static void Register(Action reset)
        {
            if (reset != null && !_resets.Contains(reset))
                _resets.Add(reset);
        }

        public static void Unregister(Action reset)
        {
            _resets.Remove(reset);
        }

        /// <summary>
        /// Run every registered reset. Each action is isolated — one throw
        /// must not skip the rest (session leak on partial cleanup).
        /// </summary>
        public static void ResetAll()
        {
            for (int i = 0; i < _resets.Count; i++)
            {
                try
                {
                    _resets[i]?.Invoke();
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Network, "NetworkResetRegistry action failed", ex);
                }
            }
        }
    }
}
