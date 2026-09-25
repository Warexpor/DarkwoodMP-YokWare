#if BEPINEX
using BepInEx.Logging;

namespace DWMPHorde.Logging
{
    /// <summary>Adapts BepInEx ManualLogSource to <see cref="IModLogger"/>.</summary>
    public sealed class BepInExModLogger : IModLogger
    {
        private readonly ManualLogSource _inner;

        public BepInExModLogger(ManualLogSource inner)
        {
            _inner = inner;
        }

        public void LogInfo(object data) => _inner?.LogInfo(data);
        public void LogWarning(object data) => _inner?.LogWarning(data);
        public void LogError(object data) => _inner?.LogError(data);
        public void LogDebug(object data) => _inner?.LogDebug(data);
    }
}
#endif
