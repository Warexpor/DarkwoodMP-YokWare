using UnityEngine;

#if BEPINEX
using BepInEx;
using BepInEx.Logging;
#endif

#if MELONLOADER
using MelonLoader;

[assembly: MelonInfo(typeof(YokWare.EntitySpawner.EntitySpawnerPlugin),
    "YokWare Entity Spawner", "1.0.0", "yokware")]
[assembly: MelonGame("Acid Wizard Studio", "Darkwood")]
[assembly: MelonPriority(-1000)]
#endif

namespace YokWare.EntitySpawner
{
#if BEPINEX
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class EntitySpawnerPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo(PluginInfo.Name + " v" + PluginInfo.Version + " — F5 to open");
            var go = new GameObject("YokWare_EntitySpawner");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EntitySpawnerUI>();
        }
    }
#endif

#if MELONLOADER
    public sealed class EntitySpawnerPlugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg(PluginInfo.Name + " v" + PluginInfo.Version + " — F5 to open");
            var go = new GameObject("YokWare_EntitySpawner");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EntitySpawnerUI>();
        }
    }
#endif
}
