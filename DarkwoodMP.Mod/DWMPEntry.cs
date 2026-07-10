using BepInEx;

namespace DWMPHorde
{
    /// <summary>
    /// Minimal BepInEx entry — must stay tiny so Unity can instantiate it.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class DWMPHordeEntry : BaseUnityPlugin
    {
        private void Awake()
        {
            ModRuntime.Start(Logger, Config);
        }

        private void OnDestroy()
        {
            ModRuntime.Stop();
        }
    }
}
