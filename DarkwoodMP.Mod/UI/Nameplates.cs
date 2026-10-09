using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// The name of another player, under their character in the game's own hover-label font. It
    /// shows only while this player can see them, by the same sight test vanilla gives its enemies
    /// (<c>Player.isInSight</c>: the view cone or the close circle, and no wall between), so it
    /// never gives away someone behind a wall or in the dark outside the cone. Config
    /// <c>[Network] ShowPlayerNames</c>: always, pointed (cursor on them) or off. Names fade in and
    /// out, and are hidden in menus, the map, dialogues and cutscenes like the hover label.
    /// </summary>
    public sealed class Nameplates : MonoBehaviour
    {
        private enum Mode { Off, Pointed, Always }

        private sealed class Plate
        {
            public tk2dTextMesh Text;
            public float Alpha;
        }

        /// <summary>Under the feet, clear of the speech bubbles above the head.</summary>
        private const float BelowFeet = 46f;
        /// <summary>How close (world units) the cursor must be to point at a player.</summary>
        private const float PointRadius = 34f;
        private const float FadePerSec = 5f;
        private static readonly Color NameColor = new Color(0.8235294f, 0.8235294f, 0.8235294f, 1f);

        private static Nameplates _instance; // process-scoped: one HUD component
        private readonly Dictionary<int, Plate> _plates = new Dictionary<int, Plate>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<int> _gone = new List<int>();

        public static void EnsureExists()
        {
            if (_instance != null)
                return;
            var go = new GameObject("YokWare_Nameplates");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Nameplates>();
        }

        private static Mode Current()
        {
            string v = ModConfig.ShowPlayerNames != null ? ModConfig.ShowPlayerNames.Value : "always";
            if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase))
                return Mode.Off;
            if (string.Equals(v, "pointed", StringComparison.OrdinalIgnoreCase))
                return Mode.Pointed;
            return Mode.Always;
        }

        /// <summary>Where vanilla would show its own hover label: in the world, no menu over it.</summary>
        private static bool CanShow(Player p)
        {
            if (p == null || !Core.coreStarted || Core.loadingGame || Core.mainMenu || (Core.forbidInputs && !UiInputLock.IsHeld))
                return false;
            if (!p.alive || p.inMenu() || p.inDialogue)
                return false;
            UI ui = Singleton<UI>.Instance;
            return ui != null && ui.gameObject.activeInHierarchy && Singleton<CamMain>.Instance != null;
        }

        private void LateUpdate()
        {
            _seen.Clear();
            try
            {
                Mode mode = Current();
                LanNetworkManager net = ModRuntime.Network;
                Player p = Player.Instance;
                if (mode != Mode.Off && net != null && net.IsConnected && CanShow(p))
                {
                    Vector3 cursor = mode == Mode.Pointed ? Core.mouseWorldPosition(playerY: true) : Vector3.zero;
                    foreach (RemotePlayerProxy proxy in net.EnumerateRemoteProxies())
                    {
                        if (proxy == null || !proxy.isActiveAndEnabled || proxy.PlayerId <= 0)
                            continue;
                        Transform t = proxy.transform;
                        bool want = p.isInSight(t);
                        if (want && mode == Mode.Pointed)
                        {
                            Vector2 d = new Vector2(cursor.x - t.position.x, cursor.z - t.position.z);
                            want = d.sqrMagnitude <= PointRadius * PointRadius;
                        }
                        Plate plate;
                        if (!_plates.TryGetValue(proxy.PlayerId, out plate) || plate.Text == null)
                        {
                            if (!want)
                                continue;
                            plate = new Plate { Text = HudText.Create("YokWare_Name_p" + proxy.PlayerId) };
                            if (plate.Text == null)
                                continue;
                            _plates[proxy.PlayerId] = plate;
                        }
                        _seen.Add(proxy.PlayerId);
                        Fade(plate, want);
                        if (plate.Alpha <= 0f)
                            continue;
                        Vector3 ui = Core.worldToUIPos(t.position + new Vector3(0f, 0f, -BelowFeet));
                        HudText.FollowScale(plate.Text);
                        HudText.Place(plate.Text, ui.x, ui.z);
                        HudText.Set(plate.Text, PlayerNames.Shown(proxy.PlayerId),
                            new Color(NameColor.r, NameColor.g, NameColor.b, plate.Alpha));
                    }
                }
            }
            catch (Exception ex)
            {
                if (NetLogThrottle.ShouldLog("nameplates", 10f, out _))
                    Logging.ModLog.Warn(Logging.LogCat.UI, "[Names] " + ex.Message);
            }

            // Players not shown this frame fade out; a plate whose UI went with the scene is dropped.
            _gone.Clear();
            foreach (KeyValuePair<int, Plate> kv in _plates)
            {
                Plate plate = kv.Value;
                if (plate.Text == null)
                {
                    _gone.Add(kv.Key);
                    continue;
                }
                if (_seen.Contains(kv.Key))
                    continue;
                // Out of the menu-free world (pause, map, cutscene) names go at once, like the hover label.
                plate.Alpha = 0f;
                if (plate.Text.gameObject.activeSelf)
                    plate.Text.gameObject.SetActive(false);
            }
            for (int i = 0; i < _gone.Count; i++)
                _plates.Remove(_gone[i]);
        }

        private static void Fade(Plate plate, bool want)
        {
            float step = Time.unscaledDeltaTime * FadePerSec;
            plate.Alpha = Mathf.Clamp01(plate.Alpha + (want ? step : -step));
            bool active = plate.Alpha > 0f;
            if (plate.Text.gameObject.activeSelf != active)
                plate.Text.gameObject.SetActive(active);
        }
    }
}
