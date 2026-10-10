using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>Owner colours: one per owner on the board. No red: vanilla's map uses red for "you are here" and death bags.</summary>
    internal static class MapPinPalette
    {
        private static readonly Color[] Colors = // process-scoped: constant palette
        {
            new Color(0.22f, 0.47f, 0.90f),
            new Color(0.27f, 0.68f, 0.30f),
            new Color(0.95f, 0.62f, 0.12f),
            new Color(0.62f, 0.32f, 0.80f),
            new Color(0.10f, 0.70f, 0.72f),
            new Color(0.90f, 0.38f, 0.65f),
            new Color(0.62f, 0.45f, 0.22f),
            new Color(0.45f, 0.45f, 0.45f),
        };

        internal static int Count => Colors.Length;

        internal static Color Get(int index) => Colors[(index % Colors.Length + Colors.Length) % Colors.Length];
    }

    /// <summary>
    /// Draws the party map board on the open world map and takes the map input for it. Pins are
    /// vanilla map ink (sprites from the map's own atlas) over a glow in the owner's colour. Only the
    /// world map of the current chapter shows pins; the prologue map and locations have none.
    ///
    /// Right click places the chosen stamp or erases the pin under the cursor; the mouse wheel picks
    /// the stamp (or restyles the pin under the cursor); middle click or Shift + right click pings
    /// the map for everyone; a double click on a pin writes a label on it (<see cref="MapPinOverlay"/>).
    /// The chosen stamp is not drawn under the cursor: the wheel brings it up for a moment (the
    /// first notch only shows it, the next ones change it). A hovered pin is described by the
    /// game's own location popup. Pins carry no collider, so they never hide a location's name
    /// under them.
    /// </summary>
    internal static class MapPinView
    {
        private const string HaloSprite = "deathDrop_M";
        private const string PingSprite = "gps_M";
        private const string FallbackSprite = "generic_marker";
        private static readonly string[] KindSprites = // process-scoped: constant sprite names
        {
            "med_bunker_enter_01_M", // Mark: an inked X
            "big_piotrek_01_M",      // Danger: skull
            "big_hunter_01_M",       // Loot: rifle
            "big_hideout_03_M",      // Shelter: house in a ring
            "sub_borderDoctor_01_M", // Camp: campfire
            "landmark_shrine_01_M",  // Grave: cross
        };

        private const float IconScale = 0.55f;
        private const float HaloScale = 1.9f;
        private const float HoverRadiusPx = 22f;
        private const float DoubleClickSec = 0.35f;
        private const float LocalPingCooldown = 1.5f;
        /// <summary>Seconds the chosen stamp stays under the cursor after the wheel, the last part fading.</summary>
        private const float GhostShowSec = 2.5f;
        private const float GhostFadeSec = 0.6f;
        private const float GhostIconAlpha = 0.5f, GhostHaloAlpha = 0.35f;
        /// <summary>Any key of the game's "UI" language sheet: the popup's title is written over it.</summary>
        private const string PopupKey = "PlayerIsInThisLocation";

        // Local heights over the map icons (higher is nearer the camera, drawn on top).
        private const float HaloY = 25f, IconY = 26f, PingY = 28f, GhostY = 30f;

        private sealed class Drawn
        {
            public MapPin Pin;
            public GameObject Halo, Icon;
            public float Until;
        }

        private static readonly List<Drawn> _drawn = new List<Drawn>(64);
        private static readonly List<Drawn> _pings = new List<Drawn>(4);
        private static GameObject _ghostHalo, _ghostIcon;
        private static MapPinKind _ghostKind = (MapPinKind)255;
        private static Map _map;
        private static Map.Type _type;
        private static int _chapter;
        private static int _drawnVersion = -1;
        private static float _lastLmbAt = -10f;
        private static int _lastLmbPinId;
        private static float _lastPingAt = -10f;
        private static float _ghostUntil = -10f;
        private static ItemPopup _popup;
        private static tk2dTextMesh _popupName;

        /// <summary>The stamp right click places (kept between map opens).</summary>
        internal static MapPinKind SelectedKind;
        /// <summary>Board id of the pin under the cursor (0 = none).</summary>
        internal static int HoveredId;
        /// <summary>Board id of the pin whose label is being written (0 = none).</summary>
        internal static int EditingId;
        /// <summary>The world map with the board on it is open.</summary>
        internal static bool Active;
        /// <summary>The chosen stamp is under the cursor right now (the wheel brought it up).</summary>
        internal static bool GhostShown;

        internal static void OnMapOpen(Map map)
        {
            Clear();
            if (map == null || map.iconHolder == null || !MapPinBoard.SessionUp(out _))
                return;
            Map.Type type = Traverse.Create(map).Field("currentType").GetValue<Map.Type>();
            if (type == null || type.name != "World" || type.scale <= 0f)
                return;
            MapPinBoard.EnsureSeeded(force: true);
            _map = map;
            _type = type;
            _chapter = MapPinBoard.CurrentChapter();
            Active = true;
            MapPinOverlay.EnsureExists();
            Redraw();
        }

        internal static void OnMapClose() => Clear();

        private static void Clear()
        {
            DestroyAll(_drawn);
            DestroyAll(_pings);
            DestroyGo(ref _ghostHalo);
            DestroyGo(ref _ghostIcon);
            _ghostKind = (MapPinKind)255;
            _map = null;
            _type = null;
            _chapter = 0;
            Active = false;
            HoveredId = 0;
            EditingId = 0;
            _drawnVersion = -1;
            _lastLmbPinId = 0;
            _ghostUntil = -10f;
            GhostShown = false;
            _popup = null;
            _popupName = null;
        }

        private static void DestroyAll(List<Drawn> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                DestroyGo(ref list[i].Halo);
                DestroyGo(ref list[i].Icon);
            }
            list.Clear();
        }

        private static void DestroyGo(ref GameObject go)
        {
            if (go != null) Object.Destroy(go);
            go = null;
        }

        internal static void OnMapUpdate(Map map)
        {
            if (!Active || map != _map || !map.opened || map.iconHolder == null)
                return;
            if (!MapPinBoard.SessionUp(out _))
            {
                Clear();
                return;
            }

            if (_drawnVersion != MapPinBoard.Version)
                Redraw();
            AnimatePings();

            bool haveCursor = TryCursorWorld(out float cx, out float cz);
            Drawn hovered = haveCursor ? PinUnderCursor() : null;
            HoveredId = hovered != null ? hovered.Pin.Id : 0;
            for (int i = 0; i < _drawn.Count; i++)
            {
                Drawn d = _drawn[i];
                if (d.Icon != null)
                    d.Icon.transform.localScale = Vector3.one * (d == hovered ? IconScale * 1.3f : IconScale);
            }
            if (EditingId != 0 && MapPinBoard.Find(EditingId) == null)
                EditingId = 0; // erased by someone else while being labelled

            float ghostLeft = _ghostUntil - Time.unscaledTime;
            GhostShown = haveCursor && hovered == null && EditingId == 0 && ghostLeft > 0f;
            UpdateGhost(GhostShown, cx, cz, Mathf.Clamp01(ghostLeft / GhostFadeSec));

            if (EditingId != 0 || !haveCursor)
                return;
            if (hovered != null)
                ShowPopup(hovered);

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetMouseButtonDown(2) || (shift && Input.GetMouseButtonDown(1)))
            {
                float now = Time.unscaledTime;
                if (now - _lastPingAt >= LocalPingCooldown)
                {
                    _lastPingAt = now;
                    MapPinBoard.RequestPing(_chapter, cx, cz);
                }
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                if (hovered != null)
                    MapPinBoard.RequestRemove(hovered.Pin.Id);
                else
                    MapPinBoard.RequestPut(SelectedKind, _chapter, cx, cz);
                return;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f)
            {
                int step = wheel > 0f ? 1 : -1;
                if (hovered != null)
                    MapPinBoard.RequestSetKind(hovered.Pin.Id, Step(hovered.Pin.Kind, step));
                else
                {
                    // The first notch only brings the stamp up; while it shows, the wheel changes it.
                    if (GhostShown)
                        SelectedKind = Step(SelectedKind, step);
                    _ghostUntil = Time.unscaledTime + GhostShowSec;
                }
            }

            if (Input.GetMouseButtonDown(0))
            {
                float now = Time.unscaledTime;
                int id = hovered != null ? hovered.Pin.Id : 0;
                if (id != 0 && id == _lastLmbPinId && now - _lastLmbAt <= DoubleClickSec)
                {
                    EditingId = id;
                    MapPinOverlay.BeginEdit(hovered.Pin.Label);
                    _lastLmbPinId = 0;
                }
                else
                {
                    _lastLmbPinId = id;
                    _lastLmbAt = now;
                }
            }
        }

        private static MapPinKind Step(MapPinKind k, int step)
            => (MapPinKind)((((int)k + step) % MapPinBoard.KindCount + MapPinBoard.KindCount) % MapPinBoard.KindCount);

        // ── Drawing ───────────────────────────────────────────────────────────────

        private static void Redraw()
        {
            DestroyAll(_drawn);
            _drawnVersion = MapPinBoard.Version;
            for (int i = 0; i < MapPinBoard.Pins.Count; i++)
            {
                MapPin p = MapPinBoard.Pins[i];
                if (p.Chapter != _chapter)
                    continue;
                Color owner = MapPinPalette.Get(p.Color);
                var d = new Drawn
                {
                    Pin = p,
                    Halo = MakeSprite(HaloSprite, p.X, p.Z, HaloY, HaloScale, new Color(owner.r, owner.g, owner.b, 0.9f)),
                    Icon = MakeSprite(KindSprites[(int)p.Kind], p.X, p.Z, IconY, IconScale, Color.Lerp(Color.white, owner, 0.35f))
                };
                _drawn.Add(d);
            }
            // Pings live in their own list (pulsing, timed); rebuild the ones that are new.
            for (int i = 0; i < MapPinBoard.Pings.Count; i++)
            {
                MapPing ping = MapPinBoard.Pings[i];
                if (ping.Pin.Chapter != _chapter || HasPing(ping))
                    continue;
                Color owner = MapPinPalette.Get(ping.Pin.Color);
                _pings.Add(new Drawn
                {
                    Pin = ping.Pin,
                    Until = ping.Until,
                    Halo = MakeSprite(HaloSprite, ping.Pin.X, ping.Pin.Z, PingY - 1f, HaloScale * 1.6f, owner),
                    Icon = MakeSprite(PingSprite, ping.Pin.X, ping.Pin.Z, PingY, 1f, Color.white)
                });
            }
        }

        private static bool HasPing(MapPing ping)
        {
            for (int i = 0; i < _pings.Count; i++)
                if (ReferenceEquals(_pings[i].Pin, ping.Pin))
                    return true;
            return false;
        }

        private static void AnimatePings()
        {
            float now = Time.unscaledTime;
            for (int i = _pings.Count - 1; i >= 0; i--)
            {
                Drawn d = _pings[i];
                float left = d.Until - now;
                if (left <= 0f || d.Icon == null)
                {
                    DestroyGo(ref d.Halo);
                    DestroyGo(ref d.Icon);
                    _pings.RemoveAt(i);
                    continue;
                }
                float pulse = 1f + 0.3f * Mathf.Sin(now * 7f);
                float fade = Mathf.Clamp01(left / 5f);
                d.Icon.transform.localScale = Vector3.one * pulse;
                SetAlpha(d.Icon, fade);
                if (d.Halo != null)
                {
                    d.Halo.transform.localScale = Vector3.one * (HaloScale * 1.6f * (2f - pulse * 0.8f));
                    SetAlpha(d.Halo, fade * 0.8f);
                }
            }
        }

        private static void SetAlpha(GameObject go, float a)
        {
            tk2dBaseSprite s = go.GetComponent<tk2dBaseSprite>();
            if (s == null) return;
            Color c = s.color;
            c.a = a;
            s.color = c;
        }

        /// <summary>
        /// The hovered pin in the game's own location popup: the stamp as its title, then the label,
        /// who placed it and on which day. Vanilla's map update hides the popup every frame nothing
        /// of its own is hovered, so this runs after it, every frame the pin is hovered.
        /// </summary>
        private static void ShowPopup(Drawn d)
        {
            InventoryController inv = Singleton<InventoryController>.Instance;
            ItemPopup popup = inv != null ? inv.itemPopup : null;
            if (popup == null || d.Icon == null)
                return;
            if (popup != _popup || _popupName == null)
            {
                _popup = popup;
                _popupName = Traverse.Create(popup).Field("nameText").GetValue<tk2dTextMesh>();
            }
            if (_popupName == null)
                return; // the popup has not started yet
            MapPin pin = d.Pin;
            string who = MapPinBoard.IsLocalOwner(pin) ? Loc.T("you") : (string.IsNullOrEmpty(pin.OwnerName) ? Loc.T("someone") : pin.OwnerName);
            string desc = (string.IsNullOrEmpty(pin.Label) ? "" : "\"" + pin.Label + "\"\n")
                + who + (pin.Day > 0 ? (Loc.Russian ? ", день " : ", day ") + pin.Day : "");
            // Where vanilla puts it for a location's icon.
            Vector3 p = d.Icon.transform.position;
            Vector2 at = new Vector2(p.x - 18f, p.z + 40f - Screen.height) / Core.ResolutionWidthModifier;
            popup.show(PopupKey, at, YokWare.VanillaMenu.Vm.ForMenuFont(desc));
            string title = YokWare.VanillaMenu.Vm.ForMenuFont(Loc.T(MapPinBoard.KindName(pin.Kind)));
            if (_popupName.text != title)
            {
                _popupName.text = title;
                _popupName.Commit();
            }
        }

        private static void UpdateGhost(bool show, float x, float z, float fade)
        {
            if (!show)
            {
                if (_ghostIcon != null) _ghostIcon.SetActive(false);
                if (_ghostHalo != null) _ghostHalo.SetActive(false);
                return;
            }
            if (_ghostKind != SelectedKind || _ghostIcon == null)
            {
                DestroyGo(ref _ghostHalo);
                DestroyGo(ref _ghostIcon);
                Color own = MapPinPalette.Get(LocalColor());
                _ghostHalo = MakeSprite(HaloSprite, x, z, GhostY - 1f, HaloScale, new Color(own.r, own.g, own.b, GhostHaloAlpha));
                _ghostIcon = MakeSprite(KindSprites[(int)SelectedKind], x, z, GhostY, IconScale, new Color(1f, 1f, 1f, GhostIconAlpha));
                _ghostKind = SelectedKind;
            }
            Place(_ghostHalo, x, z, GhostY - 1f);
            Place(_ghostIcon, x, z, GhostY);
            if (_ghostIcon != null) SetAlpha(_ghostIcon, GhostIconAlpha * fade);
            if (_ghostHalo != null) SetAlpha(_ghostHalo, GhostHaloAlpha * fade);
            if (_ghostIcon != null) _ghostIcon.SetActive(true);
            if (_ghostHalo != null) _ghostHalo.SetActive(true);
        }

        /// <summary>The colour this player's pins already have on the board (the ghost shows it), else the next free one.</summary>
        private static int LocalColor()
        {
            for (int i = 0; i < MapPinBoard.Pins.Count; i++)
                if (MapPinBoard.IsLocalOwner(MapPinBoard.Pins[i]))
                    return MapPinBoard.Pins[i].Color;
            var net = ModRuntime.Network;
            return net != null ? System.Math.Max(0, net.LocalPlayerId - 1) : 0;
        }

        private static GameObject MakeSprite(string sprite, float x, float z, float y, float scale, Color color)
        {
            GameObject go = Core.AddPrefab("UI/UIMapElement", Vector3.zero, Quaternion.Euler(90f, 0f, 0f), _map.iconHolder);
            if (go == null)
                return null;
            go.name = "YokWare_MapPin";
            // No collider: vanilla's hover takes the highest UIMapElement under the cursor, and a pin
            // over a location would hide its name. Hover over pins is by screen distance here.
            Collider col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            UIMapElement ui = go.GetComponent<UIMapElement>();
            if (ui != null) Object.DestroyImmediate(ui);
            tk2dBaseSprite s = go.GetComponent<tk2dBaseSprite>();
            if (s != null)
            {
                if (s.Collection != null && s.Collection.GetSpriteDefinition(sprite) != null)
                    s.SetSprite(sprite);
                else if (s.Collection != null && s.Collection.GetSpriteDefinition(FallbackSprite) != null)
                    s.SetSprite(FallbackSprite);
                s.color = color;
            }
            go.transform.localScale = Vector3.one * scale;
            Place(go, x, z, y);
            return go;
        }

        /// <summary>Vanilla's placement for a world-map icon: world / scale under the icon holder, whole pixels.</summary>
        private static void Place(GameObject go, float x, float z, float y)
        {
            if (go == null || _type == null) return;
            go.transform.localPosition = new Vector3(x / _type.scale, 5f + y, z / _type.scale);
            Vector3 p = go.transform.position;
            go.transform.position = new Vector3(
                (int)p.x + _type.iconOffset.x * Core.ResolutionWidthModifier,
                p.y,
                (int)p.z + _type.iconOffset.y * Core.ResolutionHeightModifier);
        }

        // ── Cursor ────────────────────────────────────────────────────────────────

        private static Camera UiCamera => Core.CamUI != null ? Core.CamUI.GetComponent<Camera>() : null;

        /// <summary>
        /// The world spot under the cursor: the cursor ray meets the icon plane, and the icon holder's
        /// own transform turns that back into map units. The map is scaled to the resolution, so the
        /// holder's world offset alone (the old way) put pins off the click above 1080p.
        /// </summary>
        private static bool TryCursorWorld(out float x, out float z)
        {
            x = z = 0f;
            Camera cam = UiCamera;
            if (cam == null || _map == null || _type == null)
                return false;
            Transform holder = _map.iconHolder.transform;
            Ray ray = cam.ScreenPointToRay(Core.cursorPos());
            var plane = new Plane(holder.up, holder.TransformPoint(new Vector3(0f, 5f, 0f)));
            if (!plane.Raycast(ray, out float enter))
                return false;
            Vector3 local = holder.InverseTransformPoint(ray.GetPoint(enter));
            float offX = _type.iconOffset.x * Core.ResolutionWidthModifier / Mathf.Max(0.0001f, holder.lossyScale.x);
            float offZ = _type.iconOffset.y * Core.ResolutionHeightModifier / Mathf.Max(0.0001f, holder.lossyScale.z);
            x = (local.x - offX) * _type.scale;
            z = (local.z - offZ) * _type.scale;
            return true;
        }

        /// <summary>UI-space point (screen pixels, x right and y up) of a pin's icon, or false when not drawn.</summary>
        internal static bool TryPinUiPoint(int pinId, out Vector2 ui)
        {
            ui = default(Vector2);
            for (int i = 0; i < _drawn.Count; i++)
            {
                Drawn d = _drawn[i];
                if (d.Pin.Id != pinId || d.Icon == null) continue;
                return TryUiPoint(d.Icon, out ui);
            }
            return false;
        }

        private static bool TryUiPoint(GameObject go, out Vector2 ui)
        {
            ui = default(Vector2);
            Camera cam = UiCamera;
            if (cam == null || go == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(go.transform.position);
            ui = new Vector2(sp.x, sp.y);
            return true;
        }

        /// <summary>The nearest pin to the cursor in screen pixels (the map scales with resolution).</summary>
        private static Drawn PinUnderCursor()
        {
            Camera cam = UiCamera;
            if (cam == null) return null;
            Vector2 cursor = Core.cursorPos();
            float scale = _map != null ? Mathf.Max(1f, _map.iconHolder.transform.lossyScale.x) : 1f;
            float best = HoverRadiusPx * scale;
            best *= best;
            Drawn hit = null;
            for (int i = 0; i < _drawn.Count; i++)
            {
                Drawn d = _drawn[i];
                if (d.Icon == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(d.Icon.transform.position);
                float dx = sp.x - cursor.x, dy = sp.y - cursor.y;
                float dd = dx * dx + dy * dy;
                if (dd < best)
                {
                    best = dd;
                    hit = d;
                }
            }
            return hit;
        }

        /// <summary>Pins drawn on the open map, for the overlay's labels.</summary>
        internal static int DrawnCount => _drawn.Count;

        internal static MapPin DrawnPin(int i) => _drawn[i].Pin;

        internal static bool DrawnUiPoint(int i, out Vector2 ui) => TryUiPoint(_drawn[i].Icon, out ui);

        /// <summary>Session end: nothing of the board stays on a map (the open map is torn down too).</summary>
        internal static void Reset()
        {
            Clear();
            SelectedKind = MapPinKind.Mark;
            _lastLmbAt = -10f;
            _lastPingAt = -10f;
        }
    }
}
