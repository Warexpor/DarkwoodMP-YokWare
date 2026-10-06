#if BEPINEX
using System.IO;
using BepInEx;
using DWMPHorde.Config;
using DWMPHorde.Logging;

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
            DiskLogFlush.Install();
            string cfgPath = Path.Combine(Paths.ConfigPath, PluginInfo.Guid + ".cfg");
            var store = new ModConfigStore(
                cfgPath, PluginInfo.Name, PluginInfo.Version, PluginInfo.Guid);
            ModRuntime.Start(new BepInExModLogger(Logger), store);
        }

        private void OnDestroy()
        {
            ModRuntime.Stop();
            DiskLogFlush.FlushNow();
        }
    }
}
#endif
