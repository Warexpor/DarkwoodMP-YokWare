using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Text for the party map board while the world map is open: pin labels under their ink, the
    /// hovered pin's card (stamp, label, who placed it and on which day), the controls strip, and
    /// the label field. While the field has the keyboard <see cref="UiInputLock"/> holds vanilla
    /// input, so typing neither walks nor closes the map (Esc cancels the label, not the map).
    /// </summary>
    internal sealed class MapPinOverlay : MonoBehaviour
    {
        private const string LockOwner = "mappin";
        private const string FieldName = "YokWareMapPinLabel";

        private static MapPinOverlay _instance; // process-scoped: DontDestroyOnLoad driver
        private string _draft = "";
        private bool _focusPending;
        private int _closeFrame = -1;
        private GUIStyle _label, _labelShade, _card, _strip;
        private int _styleScale = -1;

        internal static void EnsureExists()
        {
            if (_instance != null) return;
            var go = new GameObject("YokWare_MapPinOverlay");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<MapPinOverlay>();
        }

        internal static void BeginEdit(string current)
        {
            EnsureExists();
            _instance._draft = current ?? "";
            _instance._focusPending = true;
        }

        private static bool Editing => MapPinView.Active && MapPinView.EditingId != 0;

        private void Update()
        {
            UiInputLock.Set(LockOwner, Editing);
            if (!Editing)
                return;
            // Raw keys too: IMGUI often swallows KeyDown while a TextField has focus.
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                Commit();
            else if (Input.GetKeyDown(KeyCode.Escape))
                Cancel();
        }

        private void Commit()
        {
            if (_closeFrame == Time.frameCount) return;
            _closeFrame = Time.frameCount;
            int id = MapPinView.EditingId;
            MapPin pin = MapPinBoard.Find(id);
            string label = MapPinBoard.SanitizeLabel(_draft);
            if (pin != null && label != (pin.Label ?? ""))
                MapPinBoard.RequestSetLabel(id, label);
            Cancel();
        }

        private void Cancel()
        {
            _closeFrame = Time.frameCount;
            MapPinView.EditingId = 0;
            _draft = "";
            _focusPending = false;
            GUIUtility.keyboardControl = 0;
            // Release now so the Esc frame is stamped for UiInputLock's Esc swallow (the map stays open).
            UiInputLock.Set(LockOwner, false);
        }

        private void EnsureStyles()
        {
            int scale = Mathf.RoundToInt(Mathf.Max(1f, Screen.height / 1080f) * 100f);
            if (_label != null && scale == _styleScale)
                return;
            _styleScale = scale;
            float s = scale / 100f;
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13f * s),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                wordWrap = false,
                clipping = TextClipping.Overflow
            };
            _label.normal.textColor = new Color(0.13f, 0.09f, 0.05f);
            _labelShade = new GUIStyle(_label);
            _labelShade.normal.textColor = new Color(0.93f, 0.88f, 0.75f, 0.85f);
            _card = new GUIStyle(GUI.skin.box)
            {
                fontSize = Mathf.RoundToInt(13f * s),
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = true,
                padding = new RectOffset(8, 8, 6, 6)
            };
            _card.normal.textColor = new Color(0.92f, 0.9f, 0.85f);
            _strip = new GUIStyle(_card) { alignment = TextAnchor.MiddleCenter };
        }

        private void OnGUI()
        {
            if (!MapPinView.Active)
                return;
            Map map = Map.Instance;
            if (map == null || !map.opened)
                return;
            EnsureStyles();
            float s = _styleScale / 100f;

            DrawLabels(s);
            DrawCard(s);
            DrawStrip(s);
            if (Editing)
                DrawEditor(s);
        }

        private void DrawLabels(float s)
        {
            for (int i = 0; i < MapPinView.DrawnCount; i++)
            {
                MapPin pin = MapPinView.DrawnPin(i);
                if (string.IsNullOrEmpty(pin.Label) || pin.Id == MapPinView.EditingId)
                    continue;
                if (!MapPinView.DrawnGuiPoint(i, out Vector2 at))
                    continue;
                var r = new Rect(at.x - 150f * s, at.y + 16f * s, 300f * s, 22f * s);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), pin.Label, _labelShade);
                GUI.Label(r, pin.Label, _label);
            }
        }

        private void DrawCard(float s)
        {
            if (MapPinView.HoveredId == 0 || Editing)
                return;
            MapPin pin = MapPinBoard.Find(MapPinView.HoveredId);
            if (pin == null || !MapPinView.TryPinGuiPoint(pin.Id, out Vector2 at))
                return;
            Color c = MapPinPalette.Get(pin.Color);
            string who = MapPinBoard.IsLocalOwner(pin) ? Loc.T("you") : (string.IsNullOrEmpty(pin.OwnerName) ? Loc.T("someone") : pin.OwnerName);
            string text = "<b>" + Loc.T(MapPinBoard.KindName(pin.Kind)) + "</b>"
                + (string.IsNullOrEmpty(pin.Label) ? "" : "  “" + pin.Label + "”")
                + "\n<color=#" + ColorUtility.ToHtmlStringRGB(c) + ">●</color> " + who
                + (pin.Day > 0 ? (Loc.Russian ? ", день " : ", day ") + pin.Day : "")
                + "\n<size=" + Mathf.RoundToInt(11f * s) + ">" + Loc.T("RMB erase · wheel restyle · double-click label") + "</size>";
            Vector2 size = _card.CalcSize(new GUIContent(text));
            float x = Mathf.Clamp(at.x + 24f * s, 4f, Screen.width - size.x - 4f);
            float y = Mathf.Clamp(at.y - size.y * 0.5f, 4f, Screen.height - size.y - 4f);
            GUI.Box(new Rect(x, y, size.x, size.y), text, _card);
        }

        private void DrawStrip(float s)
        {
            string kind = Loc.T(MapPinBoard.KindName(MapPinView.SelectedKind));
            string text = Loc.Russian
                ? "Метка: <b>" + kind + "</b> (колесо)    ПКМ — поставить / стереть    СКМ или Shift+ПКМ — сигнал    двойной щелчок по метке — подпись"
                : "Stamp: <b>" + kind + "</b> (wheel)    RMB place / erase    MMB or Shift+RMB ping    double-click a pin to label it";
            Vector2 size = _strip.CalcSize(new GUIContent(text));
            float w = Mathf.Min(size.x + 16f * s, Screen.width - 16f);
            GUI.Box(new Rect((Screen.width - w) * 0.5f, Screen.height - size.y - 14f * s, w, size.y), text, _strip);
        }

        private void DrawEditor(float s)
        {
            if (!MapPinView.TryPinGuiPoint(MapPinView.EditingId, out Vector2 at))
            {
                Cancel();
                return;
            }
            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    Commit();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Escape)
                {
                    Cancel();
                    e.Use();
                    return;
                }
            }
            float w = 260f * s, h = 24f * s;
            var r = new Rect(Mathf.Clamp(at.x - w * 0.5f, 4f, Screen.width - w - 4f), at.y + 16f * s, w, h);
            GUI.Box(new Rect(r.x - 4f, r.y - 20f * s, r.width + 8f, r.height + 24f * s), Loc.T("Label (Enter save, Esc cancel)"), _card);
            GUI.SetNextControlName(FieldName);
            _draft = GUI.TextField(r, _draft ?? "", MapPinBoard.MaxLabelLength);
            if (_focusPending)
            {
                GUI.FocusControl(FieldName);
                if (e != null && e.type == EventType.Repaint)
                    _focusPending = false;
            }
        }

        /// <summary>Session end: drop a half-written label and give the keyboard back.</summary>
        internal static void Reset()
        {
            if (_instance != null)
            {
                _instance._draft = "";
                _instance._focusPending = false;
            }
            UiInputLock.Set(LockOwner, false);
        }
    }
}
