using UnityEngine;

#if BEPINEX
using BepInEx;
using BepInEx.Logging;
#endif

#if MELONLOADER
using MelonLoader;

[assembly: MelonInfo(typeof(YokWare.ManualSaves.ManualSavesPlugin),
    "YokWare Manual Saves", "1.0.0", "yokware")]
[assembly: MelonGame("Acid Wizard Studio", "Darkwood")]
[assembly: MelonAdditionalDependencies("DarkwoodMP.Mod")]
#endif

namespace YokWare.ManualSaves
{
#if BEPINEX
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    [BepInDependency(PluginInfo.CoopGuid, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class ManualSavesPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo(PluginInfo.Name + " v" + PluginInfo.Version + " — F3 opens the save slots");
            var go = new GameObject("YokWare_ManualSaves");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<SavesScreen>();
        }
    }
#endif

#if MELONLOADER
    public sealed class ManualSavesPlugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg(PluginInfo.Name + " v" + PluginInfo.Version + " — F3 opens the save slots");
            var go = new GameObject("YokWare_ManualSaves");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<SavesScreen>();
        }
    }
#endif

    internal static class Log
    {
        internal static void Info(string msg)
        {
#if BEPINEX
            ManualSavesPlugin.Log?.LogInfo(msg);
#else
            MelonLoader.MelonLogger.Msg(msg);
#endif
        }

        internal static void Error(string msg)
        {
#if BEPINEX
            ManualSavesPlugin.Log?.LogError(msg);
#else
            MelonLoader.MelonLogger.Error(msg);
#endif
        }
    }
}
