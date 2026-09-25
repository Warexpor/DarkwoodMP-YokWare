#if MELONLOADER
using System;
using System.IO;
using System.Reflection;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using MelonLoader;

[assembly: MelonInfo(typeof(DWMPHorde.MelonModMain), DWMPHorde.PluginInfo.Name, DWMPHorde.PluginInfo.Version, DWMPHorde.PluginInfo.Authors)]
[assembly: MelonGame("Acid Wizard Studio", "Darkwood")]

namespace DWMPHorde
{
    /// <summary>
    /// MelonLoader 0.7 entry for Path B. Config under Melon UserData;
    /// shared runtime body is loader-agnostic (no BepInEx types).
    /// </summary>
    public sealed class MelonModMain : MelonMod
    {
        private static bool _booted;

        public override void OnInitializeMelon()
        {
            if (_booted) return;
            _booted = true;

            string userData = ResolveUserDataDirectory();
            string cfgDir = Path.Combine(userData, "YokWare");
            Directory.CreateDirectory(cfgDir);
            string cfgPath = Path.Combine(cfgDir, PluginInfo.Guid + ".cfg");

            var store = new ModConfigStore(
                cfgPath, PluginInfo.Name, PluginInfo.Version, PluginInfo.Guid);
            var log = new MelonLoaderModLogger(LoggerInstance);
            log.LogInfo("YokWare MelonLoader entry — Path B");
            ModRuntime.Start(log, store);
            ModLog.Event(LogCat.Core, "Loader: MelonLoader | config=" + cfgPath);
        }

        public override void OnDeinitializeMelon()
        {
            ModRuntime.Stop();
            _booted = false;
        }

        private static string ResolveUserDataDirectory()
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MelonLoader", "UserData");
            try
            {
                Type env = Type.GetType("MelonLoader.Utils.MelonEnvironment, MelonLoader")
                    ?? Type.GetType("MelonLoader.MelonEnvironment, MelonLoader");
                var prop = env?.GetProperty("UserDataDirectory",
                    BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    object v = prop.GetValue(null, null);
                    if (v is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
            }
            catch { /* use fallback */ }
            return fallback;
        }
    }
}
#endif
