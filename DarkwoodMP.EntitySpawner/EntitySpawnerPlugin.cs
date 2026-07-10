using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace YokWare.EntitySpawner
{
    /// <summary>
    /// Standalone F5 entity spawner — independent of the multiplayer mod.
    /// </summary>
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
}
