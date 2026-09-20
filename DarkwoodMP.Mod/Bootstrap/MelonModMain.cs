#if MELONLOADER
using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using DWMPHorde.Logging;
using MelonLoader;

[assembly: MelonInfo(typeof(DWMPHorde.MelonModMain), DWMPHorde.PluginInfo.Name, DWMPHorde.PluginInfo.Version, DWMPHorde.PluginInfo.Authors)]
[assembly: MelonGame(null, null)]

namespace DWMPHorde
{
    /// <summary>
    /// MelonLoader 0.7 entry for Path B. Config under Melon UserData;
    /// reuses BepInEx ConfigFile/ManualLogSource types for the shared runtime body.
    /// </summary>
    public sealed class MelonModMain : MelonMod
    {
        private static bool _booted;

        public override void OnInitializeMelon()
        {
            if (_booted) return;
            _booted = true;

            // Melon 0.7: Utils.MelonEnvironment; fall back if type missing on older refs.
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MelonLoader", "UserData");
            try
            {
                Type env = Type.GetType("MelonLoader.Utils.MelonEnvironment, MelonLoader")
                    ?? Type.GetType("MelonLoader.MelonEnvironment, MelonLoader");
                var prop = env?.GetProperty("UserDataDirectory",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null)
                {
                    object v = prop.GetValue(null, null);
                    if (v is string s && !string.IsNullOrEmpty(s))
                        userData = s;
                }
            }
            catch { /* use fallback */ }

            string cfgDir = Path.Combine(userData, "YokWare");
            Directory.CreateDirectory(cfgDir);
            string cfgPath = Path.Combine(cfgDir, PluginInfo.Guid + ".cfg");

            ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.Name);
            log.LogInfo("YokWare MelonLoader entry — Path B");

            var config = new ConfigFile(cfgPath, saveOnInit: true);
            ModRuntime.Start(log, config);
            ModLog.Event(LogCat.Core, "Loader: MelonLoader | config=" + cfgPath);
        }
    }
}
#endif
