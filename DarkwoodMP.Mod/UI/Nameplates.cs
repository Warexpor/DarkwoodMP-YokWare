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
    /// The name of another player, over their character in one of the game's own fonts. It
    /// shows only while this player can see them, by the same sight test vanilla gives its enemies
    /// (<c>Player.isInSight</c>: the view cone or the close circle, and no wall between), so it
    /// never gives away someone behind a wall or in the dark outside the cone. Config
    /// <c>[Network] ShowPlayerNames</c>: always, pointed (cursor on them) or off. Names fade in and
    /// out, grow faint with distance, and are hidden in menus, the map, dialogues and cutscenes
    /// like the hover label.
    /// </summary>
    public sealed class Nameplates : MonoBehaviour
    {
        private enum Mode { Off, Pointed, Always }

        private sealed class Plate
        {
            public tk2dTextMesh Text;
            public float Alpha;
            public int Style = -1;
        }

        /// <summary>A look for the names, from the game's own fonts (<c>FontsDB</c>).</summary>
        internal struct Style
        {
            public string Label;
            public string Font;
            public float Grey;
            public float Alpha;
            public float Spacing;
            public float Size;
            public bool Lower;
            public string Around;

            public Style(string label, string font, float grey, float alpha, float spacing, float size, bool lower = false, string around = null)
            {
                Label = label; Font = font; Grey = grey; Alpha = alpha; Spacing = spacing; Size = size; Lower = lower; Around = around;
            }
        }

        /// <summary>The looks tried so far; <see cref="StyleIndex"/> picks one (test pilot <c>namestyle</c>).</summary>
        internal static readonly Style[] Styles =
        {
            // Size 1 is the hover label's own 2x: the font is an 11px bitmap, and 0.75 (1.5x) smeared its strokes.
            new Style("hover label, dimmed", "tahoma_11px_outlinedata", 0.62f, 0.85f, 0.5f, 1f),
            new Style("soft, no outline", "tahoma_11px_light_AAdata", 0.70f, 0.80f, 1f, 1f),
            new Style("menu heading, spaced", "tahoma_11px_light_aa_occludeddata", 0.62f, 0.90f, 3f, 1f, lower: true),
            new Style("thin outline, spaced", "tahoma_11px_light_outlinedata", 0.66f, 0.85f, 2f, 1f),
            new Style("engraved", "tahoma_11px_bevel_whitedata", 0.80f, 0.90f, 1f, 1f),
            new Style("day-title letters", "bebasNeue_35pxdata", 0.58f, 0.80f, 2f, 0.42f),
            new Style("spaced with dots", "tahoma_11px_light_AAdata", 0.62f, 0.85f, 2f, 1f, around: "\u00b7"),
        };

        internal static int StyleIndex = 0; // process-scoped: the look in use

        /// <summary>Over the head (no speech bubbles there any more: what is said stays in the chat).</summary>
        private const float OverHead = 44f;
        /// <summary>World units: full strength up to here, then fading out to <see cref="FarDistance"/>.</summary>
        private const float NearDistance = 220f;
        private const float FarDistance = 700f;
        /// <summary>How close (world units) the cursor must be to point at a player.</summary>
        private const float PointRadius = 34f;
        /// <summary>Seconds to fade a name in or out.</summary>
        private const float FadeSec = 0.45f;

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
            string v = ModConfig.ShowPlayerNames != null ? ModConfig.ShowPlayerNames.Value : "pointed";
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
                        Style st = Styles[Mathf.Clamp(StyleIndex, 0, Styles.Length - 1)];
                        Apply(plate, st);
                        float dist = Vector2.Distance(new Vector2(t.position.x, t.position.z),
                            new Vector2(p.transform.position.x, p.transform.position.z));
                        float far = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(NearDistance, FarDistance, dist));
                        float a = Mathf.SmoothStep(0f, 1f, plate.Alpha) * far * st.Alpha;
                        Vector3 ui = Core.worldToUIPos(t.position + new Vector3(0f, 0f, OverHead));
                        HudText.FollowScale(plate.Text);
                        // Whole pixels: a bitmap font between two pixels goes soft and shimmers as either player moves.
                        HudText.Place(plate.Text, Mathf.Round(ui.x), Mathf.Round(ui.z));
                        HudText.Set(plate.Text, Shown(st, PlayerNames.Shown(proxy.PlayerId)), new Color(st.Grey, st.Grey, st.Grey, a));
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

        private static string Shown(Style st, string name)
        {
            if (st.Lower)
                name = name.ToLowerInvariant();
            return st.Around != null ? st.Around + " " + name + " " + st.Around : name;
        }

        /// <summary>Put the look on a plate (font, spacing, size), once per change.</summary>
        private static void Apply(Plate plate, Style st)
        {
            int idx = Mathf.Clamp(StyleIndex, 0, Styles.Length - 1);
            if (plate.Style == idx)
                return;
            plate.Style = idx;
            tk2dTextMesh tm = plate.Text;
            tk2dFontData font = HudText.Font(st.Font);
            if (font != null && tm.font != font)
                tm.font = font;
            tm.anchor = TextAnchor.LowerCenter;
            tm.Spacing = st.Spacing;
            tm.scale = new Vector3(2f * st.Size, 2f * st.Size, 2f * st.Size);
            tm.text = "";
            tm.Commit();
        }

        private static void Fade(Plate plate, bool want)
        {
            float step = Time.unscaledDeltaTime / FadeSec;
            plate.Alpha = Mathf.Clamp01(plate.Alpha + (want ? step : -step));
            bool active = plate.Alpha > 0f;
            if (plate.Text.gameObject.activeSelf != active)
                plate.Text.gameObject.SetActive(active);
        }
    }
}
