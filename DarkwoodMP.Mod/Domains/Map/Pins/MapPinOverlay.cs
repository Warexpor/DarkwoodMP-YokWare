using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Text for the party map board while the world map is open, in the game's own hover-label
    /// font: pin labels under their ink, one quiet line of controls at the bottom of the map, and
    /// the label being written. (The hovered pin's card is the vanilla location popup, see
    /// <see cref="MapPinView"/>.) While a label is written <see cref="UiInputLock"/> holds vanilla
    /// input, so typing neither walks nor closes the map (Esc cancels the label, not the map).
    /// </summary>
    internal sealed class MapPinOverlay : MonoBehaviour
    {
        private const string LockOwner = "mappin";
        /// <summary>The controls line: 1080p pixels above the bottom edge (vanilla's biome name sits at 100).</summary>
        private const float HintZ = 34f;
        /// <summary>Under a pin's ink, in 1080p pixels.</summary>
        private const float LabelDrop = 14f;

        private static readonly Color HintColor = new Color(0.55f, 0.55f, 0.55f, 0.9f);
        private static readonly Color LabelColor = new Color(0.86f, 0.82f, 0.70f, 0.95f);
        private static readonly Color DraftColor = new Color(1f, 1f, 1f, 1f);

        private static MapPinOverlay _instance; // process-scoped: DontDestroyOnLoad driver
        private readonly List<tk2dTextMesh> _labels = new List<tk2dTextMesh>(32);
        private tk2dTextMesh _hint, _draftText;
        private string _draft = "";
        private int _openedFrame = -1;

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
            _instance._openedFrame = Time.frameCount;
        }

        private static bool Editing => MapPinView.Active && MapPinView.EditingId != 0;

        private void Update()
        {
            UiInputLock.Set(LockOwner, Editing);
            if (!Editing || Time.frameCount == _openedFrame)
                return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cancel();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                Commit();
                return;
            }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            if (ctrl && Input.GetKeyDown(KeyCode.V))
            {
                foreach (char c in GUIUtility.systemCopyBuffer ?? "")
                    Append(c == '\n' || c == '\r' || c == '\t' ? ' ' : c);
                return;
            }
            foreach (char c in Input.inputString)
            {
                if (c == '\b')
                {
                    if (_draft.Length > 0)
                        _draft = _draft.Substring(0, _draft.Length - 1);
                }
                else if (c == '\n' || c == '\r')
                {
                    Commit();
                    return;
                }
                else if (!ctrl)
                {
                    Append(c);
                }
            }
        }

        private void Append(char c)
        {
            if (char.IsControl(c) || _draft.Length >= MapPinBoard.MaxLabelLength)
                return;
            _draft += c;
        }

        private void Commit()
        {
            int id = MapPinView.EditingId;
            MapPin pin = MapPinBoard.Find(id);
            string label = MapPinBoard.SanitizeLabel(_draft);
            if (pin != null && label != (pin.Label ?? ""))
                MapPinBoard.RequestSetLabel(id, label);
            Cancel();
        }

        private void Cancel()
        {
            MapPinView.EditingId = 0;
            _draft = "";
            // Release now so the Esc frame is stamped for UiInputLock's Esc swallow (the map stays open).
            UiInputLock.Set(LockOwner, false);
        }

        private void LateUpdate()
        {
            Map map = Map.Instance;
            if (!MapPinView.Active || map == null || !map.opened || HudText.Source == null)
            {
                HideAll();
                return;
            }
            // Over the map: the depth of vanilla's own text on it.
            float depth = map.biomeName != null ? map.biomeName.transform.position.y : HudText.Source.transform.position.y;
            float s = HudText.Scale;

            DrawLabels(depth, s);
            DrawDraft(depth, s);
            DrawHint(depth, s);
        }

        private void DrawLabels(float depth, float s)
        {
            int used = 0;
            for (int i = 0; i < MapPinView.DrawnCount; i++)
            {
                MapPin pin = MapPinView.DrawnPin(i);
                if (string.IsNullOrEmpty(pin.Label) || pin.Id == MapPinView.EditingId)
                    continue;
                if (!MapPinView.DrawnUiPoint(i, out Vector2 at))
                    continue;
                tk2dTextMesh tm = Label(used);
                if (tm == null)
                    break;
                used++;
                Show(tm, pin.Label, LabelColor, at.x, at.y - LabelDrop * s, depth);
            }
            for (int i = used; i < _labels.Count; i++)
                Hide(_labels[i]);
        }

        private void DrawDraft(float depth, float s)
        {
            if (!Editing)
            {
                Hide(_draftText);
                return;
            }
            if (!MapPinView.TryPinUiPoint(MapPinView.EditingId, out Vector2 at))
            {
                Cancel();
                Hide(_draftText);
                return;
            }
            if (_draftText == null)
                _draftText = Create("YokWare_MapPinDraft", 2f);
            bool caret = ((int)(Time.unscaledTime * 2f) & 1) == 0;
            Show(_draftText, _draft + (caret ? "_" : " "), DraftColor, at.x, at.y - LabelDrop * s, depth);
        }

        private void DrawHint(float depth, float s)
        {
            if (_hint == null)
                _hint = Create("YokWare_MapPinHint", 2f);
            string text;
            if (Editing)
                text = Loc.T("Enter - save  ·  Esc - cancel");
            else if (MapPinView.HoveredId != 0)
                text = Loc.T("RMB - erase  ·  wheel - change the mark  ·  double click - write on it");
            else if (MapPinView.GhostShown)
                text = Loc.T(MapPinBoard.KindName(MapPinView.SelectedKind));
            else
                text = Loc.T("RMB - mark  ·  wheel - choose the mark  ·  MMB - signal");
            Show(_hint, text, HintColor, Screen.width * 0.5f, HintZ * s, depth);
        }

        private tk2dTextMesh Label(int i)
        {
            while (_labels.Count <= i)
                _labels.Add(null);
            if (_labels[i] == null)
                _labels[i] = Create("YokWare_MapPinLabel" + i, 1f);
            return _labels[i];
        }

        /// <summary>A copy of the hover label, anchored at its top centre, at a whole multiple of the font's pixels.</summary>
        private static tk2dTextMesh Create(string name, float size)
        {
            tk2dTextMesh tm = HudText.Create(name);
            if (tm == null)
                return null;
            tm.anchor = TextAnchor.UpperCenter;
            tm.scale = new Vector3(size, size, size);
            tm.Commit();
            return tm;
        }

        private static void Show(tk2dTextMesh tm, string text, Color color, float x, float z, float depth)
        {
            if (tm == null)
                return;
            HudText.FollowScale(tm);
            HudText.Set(tm, text, color);
            tm.transform.position = new Vector3(Mathf.Round(x), depth, Mathf.Round(z));
            if (!tm.gameObject.activeSelf)
                tm.gameObject.SetActive(true);
        }

        private static void Hide(tk2dTextMesh tm)
        {
            if (tm != null && tm.gameObject.activeSelf)
                tm.gameObject.SetActive(false);
        }

        private void HideAll()
        {
            for (int i = 0; i < _labels.Count; i++)
                Hide(_labels[i]);
            Hide(_hint);
            Hide(_draftText);
        }

        /// <summary>Session end: drop a half-written label and give the keyboard back.</summary>
        internal static void Reset()
        {
            if (_instance != null)
            {
                _instance._draft = "";
                _instance.HideAll();
            }
            UiInputLock.Set(LockOwner, false);
        }
    }
}
