using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YokWare.VanillaMenu
{
    /// <summary>
    /// Menu screens built from the game's own menu pieces, so a mod screen looks and behaves like
    /// Options or Profiles: the spaced grey heading of Profiles, its centred "Back"-style items, the
    /// Video tab's grey right-aligned setting names with their white values, the Options "Return",
    /// the volume slider and the yes/no confirm box. Each piece is a clone of the vanilla object
    /// (same font, material, colours, hover, click sound and gamepad handling), placed in the
    /// vanilla menus' own units: x across, z up, from the screen centre, scaled with the resolution
    /// like <c>ScaleToResolution</c>.
    ///
    /// This file is compiled into the co-op mod and into the manual-saves plugin (each gets its own
    /// copy of these public types). Screens of either one live under <c>MainMenu</c> as
    /// <c>YokWare_Screen_*</c>; opening one hides any other.
    /// </summary>
    public static class Vm
    {
        public const string ScreenPrefix = "YokWare_Screen_";

        /// <summary>English text into the game's language (the owning mod's translator).</summary>
        public static Func<string, string> Translate = s => s; // process-scoped: set once by the owning mod

        /// <summary>Holds the owning mod's gameplay-input lock while a text field is being typed in.</summary>
        public static Action<bool> EditLock; // process-scoped: set once by the owning mod

        public static string T(string english) => string.IsNullOrEmpty(english) ? english : Translate(english);

        /// <summary>A word from the game's own UI sheet ("Back", "Return", "Yes", "No", "Profile", "Day").</summary>
        public static string Vanilla(string key)
        {
            try
            {
                string s = Language.Get(key, "UI");
                return string.IsNullOrEmpty(s) ? key : s;
            }
            catch
            {
                return key;
            }
        }

        public static MainMenu Menu => Singleton<MainMenu>.Instance;

        /// <summary>The open screen of this mod (null: none, or another mod's screen is up).</summary>
        public static VmScreen Current { get; private set; } // process-scoped: menu UI state

        // Row spacing and positions of the vanilla menus.
        public const float HeaderZ = 230f;      // Profiles heading
        public const float ItemTopZ = 150f;     // first profile row
        public const float ItemStep = 50f;      // profile rows
        public const float BackZ = -180f;       // Profiles "Back"
        public const float RowStep = 40f;       // Video setting rows
        public const float NameX = -1.76f;      // Video setting names (right edge)
        public const float ValueX = 31f;        // Video setting values (left edge)
        public const float ReturnX = -183.24f;  // Options "Return" (left edge)
        public const float ReturnZ = -313f;     // Options "Return" (its menu sits 100 up)

        /// <summary>Show <paramref name="screen"/>, hiding the menu or screen it opens from.</summary>
        public static void Open(VmScreen screen)
        {
            MainMenu menu = Menu;
            if (menu == null || screen == null)
                return;
            HideOtherScreens(menu, screen);
            if (menu.Menu0 != null && menu.Menu0.activeSelf)
                menu.Menu0.SetActive(false);
            VmScreen from = Current;
            if (Current != null && Current != screen)
                Current.Hide();
            Current = screen;
            if (from != screen)
            {
                try { screen.OnOpen?.Invoke(from); }
                catch (Exception ex) { Debug.LogError("[YokWare menu] " + screen.Name + " open failed: " + ex); }
            }
            screen.Show(menu);
        }

        /// <summary>Back from the open screen: to its parent screen, else to the main stack.</summary>
        public static void Back()
        {
            VmScreen s = Current;
            if (s == null)
                return;
            if (s.OnBack != null)
            {
                s.OnBack();
                return;
            }
            if (s.Parent != null)
                Open(s.Parent);
            else
                CloseAll(restoreMenu: true);
        }

        /// <summary>Close this mod's screens; <paramref name="restoreMenu"/> shows the main stack again.</summary>
        public static void CloseAll(bool restoreMenu)
        {
            VmTextField.CancelEditing();
            if (Current != null)
                Current.Hide();
            Current = null;
            MainMenu menu = Menu;
            if (restoreMenu && menu != null && menu.Menu0 != null && !AnyScreenUp(menu))
            {
                menu.Menu0.SetActive(true);
                try { menu.populateMainOptionsButtons(); }
                catch { /* gamepad list only */ }
            }
        }

        /// <summary>The open screen stopped being shown (another mod's screen, the menu closed).</summary>
        public static void Forget(VmScreen screen)
        {
            if (Current == screen)
                Current = null;
        }

        private static bool AnyScreenUp(MainMenu menu)
        {
            Transform t = menu.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (c.gameObject.activeSelf && c.name.StartsWith(ScreenPrefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void HideOtherScreens(MainMenu menu, VmScreen keep)
        {
            Transform t = menu.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (keep.Root != null && c == keep.Root.transform)
                    continue;
                if (c.gameObject.activeSelf && c.name.StartsWith(ScreenPrefix, StringComparison.Ordinal))
                    c.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Esc / gamepad B in the menu: closes the confirm box, else goes back one screen. True when
        /// this mod had a screen up (vanilla's own Esc handling is then skipped).
        /// </summary>
        public static bool HandleEsc()
        {
            VmScreen s = Current;
            if (s == null || s.Root == null || !s.Root.activeInHierarchy)
                return false;
            if (VmTextField.Editing != null)
                return true;
            if (s.ConfirmUp)
            {
                s.CloseConfirm(false);
                return true;
            }
            Back();
            return true;
        }

        // ------------------------------------------------------------------
        // Vanilla pieces
        // ------------------------------------------------------------------

        public static GameObject NameSource(MainMenu m) => FindChild(m.VideoMenu != null ? m.VideoMenu.transform : null, "FullscreenName");
        public static GameObject ValueSource(MainMenu m) => FindChild(m.VideoMenu != null ? m.VideoMenu.transform : null, "FullscreenBtn");
        public static GameObject ItemSource(MainMenu m) => m.profilesMenuBack != null ? m.profilesMenuBack.gameObject : null;
        public static GameObject ReturnSource(MainMenu m) => FindChild(m.OptionsMenu != null ? m.OptionsMenu.transform : null, "ReturnBtn");
        public static GameObject SliderSource(MainMenu m) => m.AudioMenu != null && m.AudioMenu.soundSlider != null ? m.AudioMenu.soundSlider.gameObject : null;

        private static GameObject _headerPrefab; // process-scoped: loaded asset

        public static GameObject HeaderSource()
        {
            if (_headerPrefab == null)
                _headerPrefab = Resources.Load("Prefabs/UI/MainMenu/profiles") as GameObject;
            return _headerPrefab;
        }

        private static GameObject FindChild(Transform parent, string name)
        {
            if (parent == null)
                return null;
            Transform t = parent.Find(name);
            return t != null ? t.gameObject : null;
        }

        /// <summary>
        /// A copy of a vanilla text/sprite object under <paramref name="parent"/>: its localisation
        /// and vanilla click function removed, placed at (x, z) facing the UI camera.
        /// </summary>
        public static GameObject Clone(GameObject source, Transform parent, string name, float x, float z)
        {
            GameObject go = UnityEngine.Object.Instantiate(source, parent, false);
            go.name = name;
            LocalizedText[] locs = go.GetComponentsInChildren<LocalizedText>(true);
            for (int i = 0; i < locs.Length; i++)
                UnityEngine.Object.DestroyImmediate(locs[i]);
            PositionMe[] pms = go.GetComponentsInChildren<PositionMe>(true);
            for (int i = 0; i < pms.Length; i++)
                UnityEngine.Object.DestroyImmediate(pms[i]);
            ScaleToResolution[] strs = go.GetComponentsInChildren<ScaleToResolution>(true);
            for (int i = 0; i < strs.Length; i++)
                UnityEngine.Object.DestroyImmediate(strs[i]);
            go.transform.localPosition = new Vector3(x, 0f, z);
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = Vector3.one;
            go.layer = parent.gameObject.layer;
            go.SetActive(true);
            Button b = go.GetComponent<Button>();
            if (b != null)
            {
                b.function = "";
                b.popupType = "";
                b.localized = false;
                b.isSwitchableButton = false;
                b.selected = false;
                b.rolledOver = false;
                b.rolledOut = true;
                b.disabled = false;
                b.OnFire = null;
            }
            return go;
        }

        /// <summary>Set a cloned label's text and fit its button's click box to it.</summary>
        public static void SetText(tk2dTextMesh tm, string shown)
        {
            if (tm == null)
                return;
            shown = ForMenuFont(shown ?? "");
            if (tm.maxChars < shown.Length + 4)
                tm.maxChars = shown.Length + 16;
            if (tm.text != shown)
            {
                tm.text = shown;
                tm.Commit();
            }
            FitCollider(tm);
        }

        /// <summary>
        /// The menu font (tahoma 11px) has Latin, Cyrillic, "·" and "«»" but no dashes or
        /// ellipsis: those are written the way the font can draw them.
        /// </summary>
        public static string ForMenuFont(string s)
        {
            if (s.IndexOf('\u2014') < 0 && s.IndexOf('\u2013') < 0 && s.IndexOf('\u2026') < 0)
                return s;
            return s.Replace("\u2014", "-").Replace("\u2013", "-").Replace("\u2026", "...");
        }

        public static void FitCollider(tk2dTextMesh tm)
        {
            BoxCollider box = tm.GetComponent<BoxCollider>();
            if (box == null)
                return;
            Bounds b = tm.GetEstimatedMeshBoundsForString(string.IsNullOrEmpty(tm.text) ? " " : tm.text);
            float w = Mathf.Abs(b.size.x) + 10f;
            float h = Mathf.Max(Mathf.Abs(b.size.y), 16f) + 8f;
            box.size = new Vector3(w, h, 0f);
            box.center = new Vector3(b.center.x, b.center.y, 0f);
        }

        /// <summary>A label the player cannot click: plain grey or white text.</summary>
        public static readonly Color Grey = new Color(0.5019608f, 0.5019608f, 0.5019608f, 1f);
        public static readonly Color Dim = new Color(0.30f, 0.30f, 0.30f, 1f);
        public static readonly Color Warn = new Color(0.78f, 0.52f, 0.40f, 1f);
    }

    /// <summary>One menu screen: a root under <c>MainMenu</c> rebuilt from its build action.</summary>
    public sealed class VmScreen
    {
        public readonly string Name;
        /// <summary>Fills the screen (called on show and on <see cref="Rebuild"/>).</summary>
        public Action<VmBuilder> Build;
        /// <summary>Every frame while shown.</summary>
        public Action Tick;
        /// <summary>A state key: the screen is rebuilt when it changes (who is online, what can be done).</summary>
        public Func<string> Signature;
        /// <summary>Back / Esc. Null: the parent screen, else the main stack.</summary>
        public Action OnBack;
        /// <summary>Opened from another screen (the one it was opened from; null: the main stack).</summary>
        public Action<VmScreen> OnOpen;
        public VmScreen Parent;

        /// <summary>True when <paramref name="other"/> is this screen or sits under it.</summary>
        public bool IsAncestorOf(VmScreen other)
        {
            for (VmScreen s = other; s != null; s = s.Parent)
            {
                if (s == this)
                    return true;
            }
            return false;
        }

        public GameObject Root { get; private set; }
        public readonly List<Button> Nav = new List<Button>();
        private string _builtSignature;
        private GameObject _content;
        private GameObject _confirm;
        private Action<bool> _confirmDone;

        public VmScreen(string name)
        {
            Name = name;
        }

        public bool Visible => Root != null && Root.activeInHierarchy;
        public bool ConfirmUp => _confirm != null && _confirm.activeSelf;

        public void Show(MainMenu menu)
        {
            if (Root == null)
            {
                Root = new GameObject(Vm.ScreenPrefix + Name);
                Root.layer = menu.Menu0 != null ? menu.Menu0.layer : menu.gameObject.layer;
                Root.transform.SetParent(menu.transform, false);
                VmRoot r = Root.AddComponent<VmRoot>();
                r.Screen = this;
                r.Place();
            }
            Root.SetActive(true);
            Rebuild();
        }

        public void Hide()
        {
            VmTextField.CancelEditing();
            CloseConfirmSilently();
            if (Root != null)
                Root.SetActive(false);
        }

        /// <summary>Throw the content away and build it again for the current state.</summary>
        public void Rebuild()
        {
            if (Root == null)
                return;
            VmTextField.CancelEditing();
            CloseConfirmSilently();
            if (_content != null)
                UnityEngine.Object.DestroyImmediate(_content);
            _content = new GameObject("Content");
            _content.layer = Root.layer;
            _content.transform.SetParent(Root.transform, false);
            Nav.Clear();
            _builtSignature = Signature != null ? Signature() : null;
            try
            {
                Build?.Invoke(new VmBuilder(this, _content.transform));
            }
            catch (Exception ex)
            {
                Debug.LogError("[YokWare menu] " + Name + " build failed: " + ex);
            }
            SelectForGamepad();
        }

        public void OnFrame()
        {
            if (Signature != null && !ConfirmUp && VmTextField.Editing == null)
            {
                string sig = Signature();
                if (sig != _builtSignature)
                {
                    Rebuild();
                    return;
                }
            }
            try
            {
                Tick?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError("[YokWare menu] " + Name + " tick failed: " + ex);
            }
        }

        public void SelectForGamepad()
        {
            MainMenu menu = Vm.Menu;
            if (menu == null || Singleton<Globals>.Instance == null || !Singleton<Globals>.Instance.controllerMode)
                return;
            List<Button> list = ConfirmUp ? _confirmNav : Nav;
            menu.controllerButtons = new List<Button>(list);
            menu.rolloutControllerButtons();
            menu.currentlySelectedControllerButton = 0;
            if (list.Count > 0)
                menu.selectMenuOption(0);
        }

        private readonly List<Button> _confirmNav = new List<Button>();

        /// <summary>
        /// The vanilla yes/no box over this screen: <paramref name="text"/> as its question,
        /// <paramref name="done"/>(true) on Yes, (false) on No or Esc.
        /// </summary>
        public void Confirm(string text, Action<bool> done, string yes = null, string no = null)
        {
            MainMenu menu = Vm.Menu;
            if (menu == null || menu.confirmBox == null || Root == null)
                return;
            CloseConfirmSilently();
            _confirm = UnityEngine.Object.Instantiate(menu.confirmBox, Root.transform, false);
            _confirm.name = "Confirm";
            LocalizedText[] locs = _confirm.GetComponentsInChildren<LocalizedText>(true);
            for (int i = 0; i < locs.Length; i++)
                UnityEngine.Object.DestroyImmediate(locs[i]);
            PositionMe pm = _confirm.GetComponent<PositionMe>();
            if (pm != null)
                UnityEngine.Object.DestroyImmediate(pm);
            ScaleToResolution str = _confirm.GetComponent<ScaleToResolution>();
            if (str != null)
                UnityEngine.Object.DestroyImmediate(str);
            _confirm.transform.localPosition = Vector3.zero;
            _confirm.transform.localRotation = Quaternion.identity;
            _confirm.transform.localScale = Vector3.one;
            // The box keeps its full-screen black backdrop where vanilla put it (screen-centred).
            Transform title = _confirm.transform.Find("Title");
            Transform title2 = _confirm.transform.Find("Title2");
            Transform countdown = _confirm.transform.Find("countdown");
            if (title2 != null) title2.gameObject.SetActive(false);
            if (countdown != null) countdown.gameObject.SetActive(false);
            if (title != null)
                Vm.SetText(title.GetComponent<tk2dTextMesh>(), Vm.T(text));
            _confirmDone = done;
            _confirmNav.Clear();
            WireConfirm("YesBtn", yes != null ? Vm.T(yes) : Vm.Vanilla("Yes"), true);
            WireConfirm("NoBtn", no != null ? Vm.T(no) : Vm.Vanilla("No"), false);
            LayoutConfirmButtons();
            if (_content != null)
                _content.SetActive(false);
            _confirm.SetActive(true);
            SelectForGamepad();
        }

        private void WireConfirm(string child, string label, bool answer)
        {
            Transform t = _confirm.transform.Find(child);
            if (t == null)
                return;
            Button b = t.GetComponent<Button>();
            if (b == null)
                return;
            b.function = "";
            b.localized = false;
            b.OnFire = () => CloseConfirm(answer);
            Vm.SetText(t.GetComponent<tk2dTextMesh>(), label);
            if (t.GetComponent<BoxCollider>() == null)
            {
                t.gameObject.AddComponent<BoxCollider>();
                Vm.FitCollider(t.GetComponent<tk2dTextMesh>());
            }
            _confirmNav.Add(b);
        }

        /// <summary>Least room between the two answers.</summary>
        private const float ConfirmGap = 40f;

        /// <summary>
        /// The answers stand where vanilla wrote "Yes" and "No", each from its left edge. Longer
        /// words ("Overwrite") run into the second one, so the pair is laid out again: centred
        /// where the vanilla pair is, with at least the vanilla room between them.
        /// </summary>
        private void LayoutConfirmButtons()
        {
            Transform yes = _confirm.transform.Find("YesBtn");
            Transform no = _confirm.transform.Find("NoBtn");
            tk2dTextMesh yesTm = yes != null ? yes.GetComponent<tk2dTextMesh>() : null;
            tk2dTextMesh noTm = no != null ? no.GetComponent<tk2dTextMesh>() : null;
            if (yesTm == null || noTm == null)
                return;
            float sy = yes.localScale.x, sn = no.localScale.x;
            Bounds y0 = yesTm.GetEstimatedMeshBoundsForString(Vm.ForMenuFont(Vm.Vanilla("Yes")));
            Bounds n0 = noTm.GetEstimatedMeshBoundsForString(Vm.ForMenuFont(Vm.Vanilla("No")));
            Bounds y1 = yesTm.GetEstimatedMeshBoundsForString(yesTm.text);
            Bounds n1 = noTm.GetEstimatedMeshBoundsForString(noTm.text);
            Vector3 yp = yes.localPosition, np = no.localPosition;
            float left0 = yp.x + y0.min.x * sy;
            float right0 = np.x + n0.max.x * sn;
            float gap = Mathf.Max(ConfirmGap, (np.x + n0.min.x * sn) - (yp.x + y0.max.x * sy));
            float yesW = y1.size.x * Mathf.Abs(sy), noW = n1.size.x * Mathf.Abs(sn);
            float left = (left0 + right0) * 0.5f - (yesW + gap + noW) * 0.5f;
            yp.x = left - y1.min.x * sy;
            np.x = left + yesW + gap - n1.min.x * sn;
            yes.localPosition = yp;
            no.localPosition = np;
        }

        public void CloseConfirm(bool answer)
        {
            Action<bool> done = _confirmDone;
            CloseConfirmSilently();
            SelectForGamepad();
            try
            {
                done?.Invoke(answer);
            }
            catch (Exception ex)
            {
                Debug.LogError("[YokWare menu] " + Name + " confirm failed: " + ex);
            }
        }

        private void CloseConfirmSilently()
        {
            _confirmDone = null;
            if (_confirm != null)
                UnityEngine.Object.DestroyImmediate(_confirm);
            _confirm = null;
            if (_content != null)
                _content.SetActive(true);
        }
    }

    /// <summary>Keeps a screen root where the vanilla menus sit (screen centre, resolution scale).</summary>
    public sealed class VmRoot : MonoBehaviour
    {
        public VmScreen Screen;

        public void Place()
        {
            MainMenu menu = Vm.Menu;
            float depth = 57.068848f;
            if (menu != null && menu.profilesMenu != null)
                depth = menu.profilesMenu.transform.localPosition.y;
            float m = Mathf.Min(Core.ResolutionHeightModifier, Core.ResolutionWidthModifier);
            if (m <= 0f)
                m = 1f;
            transform.localScale = new Vector3(m, m, m);
            Transform parent = transform.parent;
            float y = (parent != null ? parent.position.y : 0f) + depth;
            transform.position = new Vector3(UnityEngine.Screen.width / 2f, y, UnityEngine.Screen.height / 2f);
            transform.rotation = Quaternion.identity;
        }

        private void LateUpdate()
        {
            Place();
            if (Vm.Current != Screen)
                return;
            Screen.OnFrame();
        }

        private void OnDisable()
        {
            if (Vm.Current == Screen && Screen.Root != null && !Screen.Root.activeSelf)
                Vm.Forget(Screen);
        }
    }

    /// <summary>Adds pieces to a screen, in the vanilla menus' units.</summary>
    public sealed class VmBuilder
    {
        private readonly VmScreen _screen;
        private readonly Transform _parent;
        private int _n;

        public VmBuilder(VmScreen screen, Transform parent)
        {
            _screen = screen;
            _parent = parent;
        }

        public VmScreen Screen => _screen;

        private string NextName(string kind) => kind + "_" + (_n++);

        private tk2dTextMesh Text(GameObject source, string kind, float x, float z, string shown)
        {
            if (source == null)
                return null;
            GameObject go = Vm.Clone(source, _parent, NextName(kind), x, z);
            tk2dTextMesh tm = go.GetComponent<tk2dTextMesh>();
            Vm.SetText(tm, shown);
            return tm;
        }

        private Button Wire(tk2dTextMesh tm, Action onFire)
        {
            if (tm == null)
                return null;
            Button b = tm.GetComponent<Button>();
            if (b == null)
                b = tm.gameObject.AddComponent<Button>();
            if (tm.GetComponent<BoxCollider>() == null)
            {
                tm.gameObject.AddComponent<BoxCollider>();
                Vm.FitCollider(tm);
            }
            b.textMesh = tm;
            b.baseColor = tm.color;
            b.OnFire = () =>
            {
                try { onFire?.Invoke(); }
                catch (Exception ex) { Debug.LogError("[YokWare menu] " + _screen.Name + " click failed: " + ex); }
            };
            _screen.Nav.Add(b);
            return b;
        }

        /// <summary>The spaced grey heading over a list (Profiles).</summary>
        public tk2dTextMesh Header(string english, float z = Vm.HeaderZ)
        {
            GameObject src = Vm.HeaderSource();
            if (src == null)
                return null;
            GameObject go = Vm.Clone(src, _parent, NextName("Header"), 0f, z);
            tk2dTextMesh tm = go.GetComponent<tk2dTextMesh>();
            Vm.SetText(tm, Vm.T(english));
            return tm;
        }

        /// <summary>A centred menu entry (the Profiles list / "Back").</summary>
        public Button Item(string english, float z, Action onFire, bool enabled = true)
        {
            MainMenu m = Vm.Menu;
            tk2dTextMesh tm = Text(m != null ? Vm.ItemSource(m) : null, "Item", 0f, z, Vm.T(english));
            Button b = Wire(tm, onFire);
            if (!enabled)
                Disable(b);
            return b;
        }

        /// <summary>The Profiles "Back" entry.</summary>
        public Button Back(Action onFire = null, float z = Vm.BackZ)
        {
            MainMenu m = Vm.Menu;
            tk2dTextMesh tm = Text(m != null ? Vm.ItemSource(m) : null, "Back", 0f, z, Vm.Vanilla("Back"));
            return Wire(tm, onFire ?? Vm.Back);
        }

        /// <summary>The Options "Return" entry (bottom left of a settings page).</summary>
        public Button Return(Action onFire = null)
        {
            MainMenu m = Vm.Menu;
            tk2dTextMesh tm = Text(m != null ? Vm.ReturnSource(m) : null, "Return", Vm.ReturnX, Vm.ReturnZ, Vm.Vanilla("Return"));
            return Wire(tm, onFire ?? Vm.Back);
        }

        /// <summary>A button on the Options "Return" row (vanilla "Apply" at x 0, "Revert" at x 168).</summary>
        public Button OptionsButton(string shown, float x, Action onFire)
        {
            MainMenu m = Vm.Menu;
            tk2dTextMesh tm = Text(m != null ? Vm.ReturnSource(m) : null, "OptionsButton", x, Vm.ReturnZ, shown);
            return Wire(tm, onFire);
        }

        /// <summary>A setting's grey name, right-aligned at <paramref name="x"/> (Video tab).</summary>
        public tk2dTextMesh Name(string english, float z, float x = Vm.NameX)
        {
            MainMenu m = Vm.Menu;
            return Text(m != null ? Vm.NameSource(m) : null, "Name", x, z, Vm.T(english));
        }

        /// <summary>A setting's value that does something when clicked, left-aligned at <paramref name="x"/>.</summary>
        public Button Value(string shown, float z, Action onFire, float x = Vm.ValueX, bool enabled = true)
        {
            MainMenu m = Vm.Menu;
            tk2dTextMesh tm = Text(m != null ? Vm.ValueSource(m) : null, "Value", x, z, shown);
            Button b = Wire(tm, onFire);
            if (!enabled)
                Disable(b);
            return b;
        }

        /// <summary>Plain text (status line, explanation); <paramref name="wrap"/> &gt; 0 wraps at that width.</summary>
        public tk2dTextMesh Label(string shown, float x, float z, TextAnchor anchor, Color color, int wrap = 0)
        {
            MainMenu m = Vm.Menu;
            tk2dTextMesh tm = Text(m != null ? Vm.NameSource(m) : null, "Label", x, z, "");
            if (tm == null)
                return null;
            tm.anchor = anchor;
            tm.color = color;
            if (wrap > 0)
            {
                tm.formatting = true;
                tm.wordWrapWidth = wrap;
            }
            Vm.SetText(tm, shown);
            tm.Commit();
            return tm;
        }

        /// <summary>A setting row whose value cycles through <paramref name="choices"/> on click (Fullscreen Yes/No).</summary>
        public Button Choice(string name, float z, string[] choicesEnglish, Func<int> get, Action<int> set, bool enabled = true)
        {
            Name(name, z);
            Button b = null;
            b = Value(ChoiceText(choicesEnglish, get), z, () =>
            {
                int next = (Mathf.Max(0, get()) + 1) % choicesEnglish.Length;
                set(next);
                Vm.SetText(b.textMesh, ChoiceText(choicesEnglish, get));
            }, enabled: enabled);
            return b;
        }

        private static string ChoiceText(string[] choices, Func<int> get)
        {
            int i = get();
            if (i < 0 || i >= choices.Length)
                i = 0;
            return Vm.T(choices[i]);
        }

        /// <summary>A setting row whose value is typed in (click, type, Enter).</summary>
        public VmTextField TextField(string name, float z, Func<string> get, Action<string> set,
            int maxLength = 64, bool masked = false, Func<char, bool> accept = null, bool enabled = true)
        {
            Name(name, z);
            Button b = Value("", z, null, enabled: enabled);
            if (b == null)
                return null;
            VmTextField f = b.gameObject.AddComponent<VmTextField>();
            f.Init(b, get, set, maxLength, masked, accept);
            b.OnFire = f.BeginEdit;
            return f;
        }

        /// <summary>A setting row that takes the next key pressed (the Controls page).</summary>
        public VmKeyField KeyField(string name, float z, Func<string> get, Action<string> set, bool enabled = true)
        {
            Name(name, z);
            Button b = Value("", z, null, enabled: enabled);
            if (b == null)
                return null;
            VmKeyField f = b.gameObject.AddComponent<VmKeyField>();
            f.Init(b, get, set);
            b.OnFire = f.Begin;
            return f;
        }

        /// <summary>A setting row with the vanilla volume slider (value 0..1).</summary>
        public VmSlider Slider(string name, float z, Func<float> get, Action<float> set)
        {
            Name(name, z);
            MainMenu m = Vm.Menu;
            GameObject src = m != null ? Vm.SliderSource(m) : null;
            if (src == null)
                return null;
            OptionsSlider os = src.GetComponent<OptionsSlider>();
            float xSize = os != null ? os.xSize : 200f;
            Vector3 srcPos = src.transform.localPosition;
            // Vanilla: the slider's left end lines up with the values column.
            GameObject go = Vm.Clone(src, _parent, NextName("Slider"), Vm.ValueX + xSize / 2f, z);
            OptionsSlider copy = go.GetComponent<OptionsSlider>();
            Transform knob = copy != null ? copy.button : go.transform.Find("Button");
            if (copy != null)
                UnityEngine.Object.DestroyImmediate(copy);
            Button vb = go.GetComponent<Button>();
            if (vb != null)
                UnityEngine.Object.DestroyImmediate(vb);
            VmSlider s = go.AddComponent<VmSlider>();
            s.Init(knob, xSize, get, set);
            _ = srcPos;
            return s;
        }

        /// <summary>Undo <see cref="Disable"/>: <paramref name="color"/> is the colour it had when built.</summary>
        public static void Enable(Button b, Color color)
        {
            if (b == null)
                return;
            b.disabled = false;
            b.noRollover = false;
            b.baseColor = color;
            if (b.textMesh != null)
            {
                b.textMesh.color = color;
                b.textMesh.Commit();
            }
        }

        /// <summary>Make <paramref name="b"/> look and act unavailable.</summary>
        public static void Disable(Button b)
        {
            if (b == null)
                return;
            b.disabled = true;
            b.noRollover = true;
            if (b.textMesh != null)
            {
                b.textMesh.color = Vm.Dim;
                b.textMesh.Commit();
            }
            b.baseColor = Vm.Dim;
        }
    }

    /// <summary>One setting a <see cref="VmSettingsPage"/> keeps track of.</summary>
    public sealed class VmSetting
    {
        public string Key;
        public Func<object> Get;
        public Action<object> Set;
        public object Default;
        /// <summary>False: shown but locked right now (left alone by "Revert to default").</summary>
        public bool Enabled = true;
        /// <summary>
        /// A change with consequences (others see it, it changes the game for everyone): leaving
        /// with it not applied asks first. Other changes are simply kept.
        /// </summary>
        public bool Ask;
    }

    /// <summary>
    /// The vanilla Options way for a settings screen: a change shows at once, "Apply" keeps it,
    /// "Revert to default" puts the screen's settings back to their defaults. Leaving with a
    /// change not applied to a setting marked <see cref="VmSetting.Ask"/> asks "Do you wish to
    /// apply these changes?" (No puts those settings back to what the screen had when it was
    /// opened); every other change is kept without asking. Child screens opened from it do not
    /// count as leaving.
    /// </summary>
    public sealed class VmSettingsPage
    {
        private readonly VmScreen _screen;
        private readonly Func<IEnumerable<VmSetting>> _settings;
        private readonly Dictionary<string, object> _opened = new Dictionary<string, object>();
        private Button _apply;
        private Button _revert;
        private Color _applyColor;
        private Color _revertColor;

        public VmSettingsPage(VmScreen screen, Func<IEnumerable<VmSetting>> settings)
        {
            _screen = screen;
            _settings = settings;
            screen.OnOpen = from =>
            {
                if (from == null || !screen.IsAncestorOf(from))
                    Remember();
            };
            screen.OnBack = Leave;
            Action tick = screen.Tick;
            screen.Tick = () =>
            {
                tick?.Invoke();
                Refresh();
            };
        }

        /// <summary>Changed since opened or last applied.</summary>
        public bool Changed => AnyChanged(false);

        /// <summary><paramref name="askOnly"/>: only the settings that ask before leaving.</summary>
        private bool AnyChanged(bool askOnly)
        {
            foreach (VmSetting s in _settings())
            {
                if (s != null && (!askOnly || s.Ask) && _opened.TryGetValue(s.Key, out object was) && !Equals(was, s.Get()))
                    return true;
            }
            return false;
        }

        private bool AtDefaults
        {
            get
            {
                foreach (VmSetting s in _settings())
                {
                    if (s != null && s.Enabled && !Equals(s.Default, s.Get()))
                        return false;
                }
                return true;
            }
        }

        /// <summary>The Apply and Revert to default buttons, on the Return row.</summary>
        public void Buttons(VmBuilder b)
        {
            _apply = b.OptionsButton(Vm.Vanilla("Apply"), 0f, Apply);
            _revert = b.OptionsButton(Vm.Vanilla("Revert_to_default"), 168f, RevertToDefault);
            _applyColor = _apply != null ? _apply.baseColor : Color.white;
            _revertColor = _revert != null ? _revert.baseColor : Color.white;
            _shownApply = _shownRevert = null;
            Refresh();
        }

        private bool? _shownApply;
        private bool? _shownRevert;

        private void Refresh()
        {
            Show(_apply, _applyColor, Changed, ref _shownApply);
            Show(_revert, _revertColor, !AtDefaults, ref _shownRevert);
        }

        private static void Show(Button b, Color color, bool on, ref bool? shown)
        {
            if (b == null || shown == on)
                return;
            shown = on;
            if (on)
                VmBuilder.Enable(b, color);
            else
                VmBuilder.Disable(b);
        }

        private void Remember()
        {
            _opened.Clear();
            foreach (VmSetting s in _settings())
            {
                if (s != null && !_opened.ContainsKey(s.Key))
                    _opened[s.Key] = s.Get();
            }
        }

        public void Apply()
        {
            Remember();
            Refresh();
        }

        public void RevertToDefault()
        {
            foreach (VmSetting s in _settings())
            {
                if (s != null && s.Enabled)
                    s.Set(s.Default);
            }
            _screen.Rebuild();
        }

        private void PutBackAsking()
        {
            foreach (VmSetting s in _settings())
            {
                if (s != null && s.Ask && _opened.TryGetValue(s.Key, out object was) && !Equals(was, s.Get()))
                    s.Set(was);
            }
        }

        private void Leave()
        {
            if (!AnyChanged(true))
            {
                Remember();
                Close();
                return;
            }
            _screen.Confirm(Vm.Vanilla("ApplySettingsBox_title"), yes =>
            {
                if (!yes)
                    PutBackAsking();
                Remember();
                Close();
            });
        }

        private void Close()
        {
            if (_screen.Parent != null)
                Vm.Open(_screen.Parent);
            else
                Vm.CloseAll(restoreMenu: true);
        }
    }

    /// <summary>A typed-in value: click to edit, type, Enter or a click elsewhere keeps it, Esc drops it.</summary>
    public sealed class VmTextField : MonoBehaviour
    {
        public static VmTextField Editing { get; private set; } // process-scoped: menu UI state

        private Button _button;
        private Func<string> _get;
        private Action<string> _set;
        private int _max;
        private bool _masked;
        private Func<char, bool> _accept;
        private string _buffer;
        private int _startedFrame;

        public void Init(Button b, Func<string> get, Action<string> set, int max, bool masked, Func<char, bool> accept)
        {
            _button = b;
            _get = get;
            _set = set;
            _max = max;
            _masked = masked;
            _accept = accept;
            Show();
        }

        private string Display(string v)
        {
            if (string.IsNullOrEmpty(v))
                return Editing == this ? "" : "---";
            return _masked && Editing != this ? new string('*', Mathf.Min(v.Length, 12)) : v;
        }

        public void Show()
        {
            Vm.SetText(_button.textMesh, Display(_get != null ? _get() : ""));
        }

        public void BeginEdit()
        {
            if (Editing == this)
                return;
            CancelEditing();
            Editing = this;
            _buffer = _get != null ? (_get() ?? "") : "";
            _startedFrame = Time.frameCount;
            Vm.EditLock?.Invoke(true);
            Render();
        }

        public static void CancelEditing()
        {
            VmTextField f = Editing;
            if (f == null)
                return;
            Editing = null;
            Vm.EditLock?.Invoke(false);
            if (f != null && f._button != null)
                f.Show();
        }

        private void Commit()
        {
            string v = (_buffer ?? "").Trim();
            Editing = null;
            Vm.EditLock?.Invoke(false);
            try { _set?.Invoke(v); }
            catch (Exception ex) { Debug.LogError("[YokWare menu] field: " + ex); }
            Show();
        }

        private void Render()
        {
            bool caret = ((int)(Time.unscaledTime * 2f) & 1) == 0;
            string text = _masked ? new string('*', _buffer.Length) : _buffer;
            Vm.SetText(_button.textMesh, text + (caret ? "_" : " "));
            if (_button.textMesh != null)
            {
                _button.textMesh.color = Color.white;
                _button.textMesh.Commit();
            }
        }

        private void Update()
        {
            if (Editing != this)
                return;
            if (!isActiveAndEnabled)
            {
                CancelEditing();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelEditing();
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
                string paste = GUIUtility.systemCopyBuffer ?? "";
                foreach (char c in paste)
                    Append(c);
            }
            else
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\b')
                    {
                        if (_buffer.Length > 0)
                            _buffer = _buffer.Substring(0, _buffer.Length - 1);
                    }
                    else if (c == '\n' || c == '\r')
                    {
                        Commit();
                        return;
                    }
                    else
                    {
                        Append(c);
                    }
                }
            }
            // A click anywhere else keeps what was typed.
            if (Input.GetMouseButtonDown(0) && Time.frameCount != _startedFrame && !_button.rolledOver)
            {
                Commit();
                return;
            }
            Render();
        }

        private void Append(char c)
        {
            if (char.IsControl(c) || _buffer.Length >= _max)
                return;
            if (_accept != null && !_accept(c))
                return;
            _buffer += c;
        }

        private void OnDisable()
        {
            if (Editing == this)
                CancelEditing();
        }
    }

    /// <summary>Waits for the next key pressed, like rebinding on the Controls page.</summary>
    public sealed class VmKeyField : MonoBehaviour
    {
        private Button _button;
        private Func<string> _get;
        private Action<string> _set;
        private bool _waiting;
        private int _startedFrame;
        private static KeyCode[] _keys; // process-scoped: enum list

        public void Init(Button b, Func<string> get, Action<string> set)
        {
            _button = b;
            _get = get;
            _set = set;
            Show();
        }

        private void Show()
        {
            Vm.SetText(_button.textMesh, Label(_get != null ? (_get() ?? "") : ""));
        }

        /// <summary>A Unity KeyCode name as players know it: the mouse buttons by name, the side ones as Mouse 4 and 5.</summary>
        public static string Label(string keyCode)
        {
            switch (keyCode)
            {
                case "Mouse1": return Vm.T("Right mouse");
                case "Mouse2": return Vm.T("Middle mouse");
                case "Mouse3": return Vm.T("Mouse 4");
                case "Mouse4": return Vm.T("Mouse 5");
                case "Mouse5": return Vm.T("Mouse 6");
                case "Mouse6": return Vm.T("Mouse 7");
                default: return keyCode;
            }
        }

        public void Begin()
        {
            _waiting = true;
            _startedFrame = Time.frameCount;
            Vm.EditLock?.Invoke(true);
            Vm.SetText(_button.textMesh, Vm.T("Press a key"));
        }

        private void Stop()
        {
            _waiting = false;
            Vm.EditLock?.Invoke(false);
            Show();
        }

        private void Update()
        {
            if (!_waiting || Time.frameCount == _startedFrame)
                return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Stop();
                return;
            }
            if (!Input.anyKeyDown)
                return;
            if (_keys == null)
                _keys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            for (int i = 0; i < _keys.Length; i++)
            {
                KeyCode k = _keys[i];
                if (k == KeyCode.None || k == KeyCode.Mouse0 || k == KeyCode.Escape)
                    continue;
                if (!Input.GetKeyDown(k))
                    continue;
                try { _set?.Invoke(k.ToString()); }
                catch (Exception ex) { Debug.LogError("[YokWare menu] key: " + ex); }
                Stop();
                return;
            }
        }

        private void OnDisable()
        {
            if (_waiting)
                Stop();
        }
    }

    /// <summary>The vanilla volume slider driving one of our values (0..1).</summary>
    public sealed class VmSlider : MonoBehaviour
    {
        private Transform _knob;
        private float _xSize;
        private Func<float> _get;
        private Action<float> _set;
        private BoxCollider _col;

        public void Init(Transform knob, float xSize, Func<float> get, Action<float> set)
        {
            _knob = knob;
            _xSize = xSize > 1f ? xSize : 200f;
            _get = get;
            _set = set;
            _col = GetComponent<BoxCollider>();
            PlaceKnob(_get != null ? _get() : 0f);
        }

        private void PlaceKnob(float t)
        {
            if (_knob == null)
                return;
            t = Mathf.Clamp01(t);
            _knob.localPosition = new Vector3(-_xSize / 2f + t * _xSize, -2f, -5f);
        }

        private void Update()
        {
            if (_col == null || Core.CamUI == null)
                return;
            if (Input.GetMouseButton(0))
            {
                Ray ray = Core.CamUI.GetComponent<Camera>().ScreenPointToRay(Input.mousePosition);
                if (_col.Raycast(ray, out RaycastHit hit, 2000f))
                {
                    float lx = transform.InverseTransformPoint(hit.point).x;
                    float t = Mathf.Clamp01((lx + _xSize / 2f) / _xSize);
                    _set?.Invoke(t);
                    PlaceKnob(t);
                }
            }
        }
    }

    /// <summary>Esc / gamepad B in the menu goes back through this mod's screens first.</summary>
    [HarmonyPatch(typeof(InputScript), "pressEscInMainMenu")]
    internal static class VmEscPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Vm.HandleEsc())
                return true;
            __result = true;
            return false;
        }
    }

    /// <summary>The menu closing (resume, scene load) takes this mod's screens down with it.</summary>
    [HarmonyPatch(typeof(MainMenu), "OnDisable")]
    internal static class VmMenuClosePatch
    {
        private static void Postfix()
        {
            if (Vm.Current != null)
                Vm.CloseAll(restoreMenu: true);
        }
    }
}
