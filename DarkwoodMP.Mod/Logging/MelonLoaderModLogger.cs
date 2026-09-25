#if MELONLOADER
using MelonLoader;

namespace DWMPHorde.Logging
{
    /// <summary>Adapts MelonLogger to <see cref="IModLogger"/>.</summary>
    public sealed class MelonLoaderModLogger : IModLogger
    {
        private readonly MelonLogger.Instance _inner;

        public MelonLoaderModLogger(MelonLogger.Instance inner)
        {
            _inner = inner;
        }

        public void LogInfo(object data) => _inner?.Msg(data?.ToString() ?? "");
        public void LogWarning(object data) => _inner?.Warning(data?.ToString() ?? "");
        public void LogError(object data) => _inner?.Error(data?.ToString() ?? "");
        public void LogDebug(object data) => _inner?.Msg(data?.ToString() ?? "");
    }
}
#endif
