using DWMPHorde.Logging;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A new world is shared before vanilla gets to <c>Map.initialize</c>, which runs at the
    /// wake-up (<c>WorldGenerator.activatePlayer</c>) after the opening. That is where every map
    /// piece takes its name (a road's from its sprite) and joins its map's list, and a save keeps
    /// both: a load takes the names and lists from the save and does not initialize again. The
    /// package carried the pieces unnamed and unlisted, so a joiner's map lacked them (the road by
    /// the hideout, among others) and the host's discoveries of them found no piece by that name.
    /// Before the share's save the pieces are initialized as the wake-up would, once each: vanilla's
    /// later <c>Map.initialize</c> takes only the ones that start after this.
    /// </summary>
    internal static class MapShareInitialize
    {
        private static Map _initialized; // process-scoped: the scene's Map once vanilla initialize ran

        internal static void NoteInitialized(Map map) => _initialized = map;

        internal static void BeforeShareSave()
        {
            Map map = Map.Instance;
            if (map == null || ReferenceEquals(map, _initialized) || map.elementsToInitialize == null)
                return;
            int n = 0;
            for (int i = 0; i < map.elementsToInitialize.Count; i++)
            {
                MapElement e = map.elementsToInitialize[i];
                if (e == null)
                    continue;
                e.initialize();
                n++;
            }
            map.elementsToInitialize.Clear();
            ModLog.Event(LogCat.Save, "[MapShare] initialized " + n + " map piece(s) before the world share save");
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.initialize))]
    public static class MapShareInitializedPatch
    {
        private static void Postfix(Map __instance) => MapShareInitialize.NoteInitialized(__instance);
    }
}
