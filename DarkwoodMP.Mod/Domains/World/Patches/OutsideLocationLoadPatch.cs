using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A save can put the player in an outside location whose object is not in the save: a
    /// dream pad (the prologue's included) is never saved, but vanilla still writes its name as
    /// the player's current location when the save runs while the player stands in it. The host
    /// makes such a save when it shares a new world during its prologue. Vanilla
    /// <c>SaveManager.Load</c> then calls <c>enter</c> on the missing location and stops with a
    /// <c>NullReferenceException</c>, leaving the load stuck. With no location to stand in, the
    /// player is loaded in the overworld, as vanilla's own return from a location leaves it.
    /// A dream the save resumes (<c>Dreams.wantToDream</c>) puts the player back in its pad
    /// itself, and a joiner's own prologue or saved character place it afterwards.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations.SaveState), nameof(OutsideLocations.SaveState.loadValues))]
    public static class OutsideLocationMissingOnLoadPatch
    {
        private static void Postfix(OutsideLocations outsideLocations)
        {
            if (outsideLocations == null || !outsideLocations.playerInOutsideLocation)
                return;
            string name = outsideLocations.currentLocationName;
            if (!string.IsNullOrEmpty(name) && outsideLocations.spawnedLocations != null
                && outsideLocations.spawnedLocations.TryGetValue(name, out Location loc) && loc != null)
                return;
            outsideLocations.playerInOutsideLocation = false;
            outsideLocations.currentLocationName = "";
            outsideLocations.objectThatTransportedMe = null;
            Core.modifyCamEffects(active: false, null);
            Dreams d = Dreams.Instance;
            Player p = Player.Instance;
            Vector3 back = outsideLocations.positionCopy;
            bool moved = false;
            if ((d == null || !d.wantToDream) && p != null && back != Vector3.zero)
            {
                p._transform.position = back;
                moved = true;
            }
            ModLog.Event(LogCat.Save, "[Load] saved in location '" + name + "', which the save does not hold: player loads in the overworld"
                + (moved ? " (at the spot it left from)" : ""));
        }
    }
}
