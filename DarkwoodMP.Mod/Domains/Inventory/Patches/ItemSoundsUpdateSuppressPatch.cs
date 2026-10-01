using System.Collections.Generic;
using DWMPHorde.Audio;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When MOS owns the scrape for a networked object, skip native
    /// <see cref="ItemSounds.Update"/> moving-loop logic entirely.
    /// ItemSounds.Update only drives the moving scrape (start/stop movingSoundAO),
    /// so returning false is safe and prevents double-scrape on remote peers.
    ///
    /// Host only: still fire vanilla-interval <see cref="Character.alertInArea"/>
    /// while the remote scrape is active. Native checkIfMoving never starts when
    /// Update is suppressed, so furniture the client pushes/drags was silent to AI.
    /// </summary>
    [HarmonyPatch(typeof(ItemSounds), "Update")]
    public static class ItemSoundsUpdateSuppressPatch
    {
        /// <summary>Matches vanilla ItemSounds.checkIfMoving WaitForSeconds(0.5f).</summary>
        private const float AlertIntervalSec = 0.5f;
        private static readonly Dictionary<int, float> _lastAlertTime = new Dictionary<int, float>(32);

        /// <summary>ItemSounds instance id → GameObject name (the name getter allocates every call).</summary>
        private static readonly Dictionary<int, string> _nameById = new Dictionary<int, string>(256);
        private const int MaxCachedNames = 8192;

        public static void Reset()
        {
            _lastAlertTime.Clear();
            _nameById.Clear();
        }

        private static bool Prefix(ItemSounds __instance)
        {
            // Single-player / not connected: never suppress.
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;

            if (__instance == null)
                return true;

            int id = __instance.GetInstanceID();
            if (!_nameById.TryGetValue(id, out string name))
            {
                if (__instance.gameObject == null)
                    return true;
                if (_nameById.Count >= MaxCachedNames)
                    _nameById.Clear();
                name = __instance.gameObject.name;
                _nameById[id] = name;
            }
            if (!ItemMovingSoundHelper.IsRemoteScrape(name))
                return true;

            // Remote-owned: ensure any already-armed native AO is killed once.
            try
            {
                var ao = Traverse.Create(__instance).Field("movingSoundAO").GetValue<AudioObject>();
                if (ao != null)
                {
                    ao.Stop(ItemMovingSoundHelper.IntentionalStopFade);
                    Traverse.Create(__instance).Field("movingSoundAO").SetValue(null);
                }
            }
            catch
            {
                // ignore traverse failure — MOS path still owns playback
            }

            // Host AI must hear client-owned scrape at vanilla cadence + ranges.
            if (net.Role == NetworkRole.Host)
                MaybeAlertHostAi(__instance);

            return false;
        }

        private static void MaybeAlertHostAi(ItemSounds sounds)
        {
            if (sounds == null)
                return;

            int id = sounds.GetInstanceID();
            float now = Time.time;
            if (_lastAlertTime.TryGetValue(id, out float last) && now - last < AlertIntervalSec)
                return;
            _lastAlertTime[id] = now;

            Character.alertInArea(
                sounds.transform.position,
                sounds.movingAlertDistance,
                dangerousSound: false,
                sounds.movingAlertVolume);
        }
    }

    /// <summary>
    /// Vanilla <see cref="ItemSounds.alertCharactersInArea"/> only alerts when
    /// <see cref="Player.Instance"/> is touching or dragging the item. Widen that
    /// gate on the host so a remote-owned scrape/drag counts as a player body.
    /// </summary>
    [HarmonyPatch(typeof(ItemSounds), "alertCharactersInArea")]
    public static class HostItemSoundsAlertCharactersPatch
    {
        private static bool Prefix(ItemSounds __instance)
        {
            if (__instance == null)
                return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            Item item = __instance.GetComponent<Item>();
            Collider col = __instance.GetComponent<Collider>();
            Player host = Player.Instance;

            bool hostOwns = host != null && (
                (col != null && host.touchingColliders != null && host.touchingColliders.Contains(col))
                || (host.itemBeingDragged != null && item != null && host.itemBeingDragged == item));

            if (hostOwns)
                return true;

            string name = __instance.gameObject != null ? __instance.gameObject.name : null;
            bool remoteOwns = !string.IsNullOrEmpty(name)
                && (ItemMovingSoundHelper.IsRemoteScrape(name)
                    || IsRemoteDragName(name));

            if (!remoteOwns)
                return true;

            Character.alertInArea(
                __instance.transform.position,
                __instance.movingAlertDistance,
                dangerousSound: false,
                __instance.movingAlertVolume);
            return false;
        }

        private static bool IsRemoteDragName(string objectName)
        {
            var net = LanNetworkManager.Instance;
            if (net == null)
                return false;
            return net._remoteDragItemNames != null
                && net._remoteDragItemNames.Contains(objectName);
        }
    }
}
