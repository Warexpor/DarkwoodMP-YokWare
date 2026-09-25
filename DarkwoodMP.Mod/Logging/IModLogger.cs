namespace DWMPHorde.Logging
{
    /// <summary>
    /// Loader-agnostic log sink. BepInEx wraps ManualLogSource; Melon wraps MelonLogger.
    /// Method names match ManualLogSource so ModRuntime.Log call sites stay unchanged.
    /// </summary>
    public interface IModLogger
    {
        void LogInfo(object data);
        void LogWarning(object data);
        void LogError(object data);
        void LogDebug(object data);
    }
}
