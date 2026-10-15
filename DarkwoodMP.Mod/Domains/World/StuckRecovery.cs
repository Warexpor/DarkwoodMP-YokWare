using System;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A way out for a player left somewhere it cannot walk out of: an outside location that never
    /// loaded around it (an empty copy of the village after a shortcut, a pad slot with nothing on
    /// it after a rejoin). From the pause menu the player is put at its hideout's bed, out of any
    /// location, as vanilla's own move home (<c>transportToHome</c>) places it, without that
    /// function's world changes (characters sent back, traps cleared), which are the host's.
    /// </summary>
    internal static class StuckRecovery
    {
        /// <summary>The menu entry is offered: in a game, alive, not loading, not in a scene that owns the player.</summary>
        internal static bool CanUse()
        {
            Player p = Player.Instance;
            if (p == null || !p.alive || GameScreen.AtTitle || Core.loadingGame)
                return false;
            if (PersonalPrologue.LocalInPrologue || EpilogueNetHandlers.IsLocalInEpilogue())
                return false;
            Dreams dreams = Singleton<Dreams>.Instance;
            if (DreamSyncManager.IsLocalDreamActive || (dreams != null && (dreams.dreaming || dreams.dreamPrepared || dreams.switchingDream)))
                return false;
            OutsideLocations ol = Singleton<OutsideLocations>.Instance;
            return ol == null || !ol.loading;
        }

        internal static bool GoHome()
        {
            if (!CanUse())
                return false;
            Player p = Player.Instance;
            Location home = DeathStateTracker.HomeLocation(p);
            if (home == null || home.playerSpawn == null)
            {
                ModLog.Warn(LogCat.Session, "[Stuck] no hideout to return to");
                return false;
            }
            Vector3 from = p.transform.position;
            try
            {
                p.stopWhatImDoing();
                p.teleportTo(home.playerSpawn.transform.position, Quaternion.Euler(90f, 0f, 0f));
                OutsideLocations ol = Singleton<OutsideLocations>.Instance;
                if (ol != null && (ol.playerInOutsideLocation || ClientStateBackup.IsDreamPadCoordinate(from)))
                    ol.returningOnTeleportedPlayer(); // leaves every pad, World grid, LocationExit to peers
                else
                    Singleton<WorldGrid>.Instance?.refreshPosition(p.transform.position, instant: true, force: true);
                p.whereAmI?.checkWhereAmI();
                ModLog.Event(LogCat.Session, "[Stuck] player moved home from " + from + " to " + p.transform.position);
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Session, "[Stuck] moving home failed: " + ex.Message);
                return false;
            }
        }
    }
}
