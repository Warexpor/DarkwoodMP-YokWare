#if BEPINEX
using System;
using BepInEx.Logging;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// BepInEx's disk log buffers lines and writes them out on a 2 s timer; the file is only closed by a
    /// finalizer, which Unity's shutdown never runs. So the last seconds before quitting, and everything
    /// the teardown logs, reached the console but not LogOutput.log. This listener runs after the disk
    /// log: errors are written out at once, and from the moment the game starts quitting every line is.
    /// </summary>
    internal sealed class DiskLogFlush : ILogListener
    {
        private static bool _installed; // process-scoped: one listener per process
        private static bool _quitting;

        internal static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            BepInEx.Logging.Logger.Listeners.Add(new DiskLogFlush());
            Application.quitting += () =>
            {
                _quitting = true;
                FlushNow();
            };
        }

        /// <summary>Write every buffered disk log line to the file now.</summary>
        internal static void FlushNow()
        {
            foreach (var listener in BepInEx.Logging.Logger.Listeners)
            {
                if (listener is DiskLogListener disk)
                {
                    try { disk.LogWriter?.Flush(); }
                    catch (Exception) { /* file already closed */ }
                }
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (_quitting || (eventArgs.Level & (BepInEx.Logging.LogLevel.Error | BepInEx.Logging.LogLevel.Fatal)) != 0)
                FlushNow();
        }

        public void Dispose() { }
    }
}
#endif
