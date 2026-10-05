using System;
using System.Text;
using DWMPHorde.Logging;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// <c>UI.hidePlayerUI</c> (map, menus) threw on the host in a playtest and the map never opened.
    /// When it throws, name what vanilla tripped on: a dead entry in the player's
    /// <c>attachedGameObjects</c> (and which), or a missing UI piece. Logged once per session.
    /// </summary>
    [HarmonyPatch(typeof(UI), nameof(UI.hidePlayerUI))]
    internal static class HidePlayerUiDiagnosticPatch
    {
        private static bool _logged; // process-scoped: one report per process is enough

        private static Exception Finalizer(UI __instance, Exception __exception)
        {
            if (__exception == null || _logged)
                return __exception;
            _logged = true;
            try
            {
                var sb = new StringBuilder("[HidePlayerUI] threw ").Append(__exception.GetType().Name).Append(":");
                Player p = Player.Instance;
                if (p == null)
                    sb.Append(" no player");
                else
                {
                    sb.Append(" attached=").Append(p.attachedGameObjects.Count);
                    for (int i = 0; i < p.attachedGameObjects.Count; i++)
                    {
                        UnityEngine.GameObject go = p.attachedGameObjects[i];
                        if (ReferenceEquals(go, null))
                            sb.Append(" [").Append(i).Append("]=null");
                        else if (go == null)
                            sb.Append(" [").Append(i).Append("]=destroyed");
                    }
                    sb.Append(" inventory=").Append(p.Inventory != null)
                      .Append(" hotbar=").Append(p.Hotbar != null)
                      .Append(" crafting=").Append(p.Crafting != null);
                }
                sb.Append(" upgradeMenu=").Append(__instance != null && __instance.upgradeItemMenu != null);
                ModLog.Event(LogCat.UI, sb.ToString());
            }
            catch (Exception ex)
            {
                ModLog.Event(LogCat.UI, "[HidePlayerUI] report failed: " + ex.Message);
            }
            return __exception;
        }
    }
}
