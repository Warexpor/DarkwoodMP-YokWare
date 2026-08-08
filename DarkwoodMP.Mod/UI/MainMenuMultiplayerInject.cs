using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Native tk2d title MULTIPLAYER button — Host/Join doors, then LAN|Steam.
    /// Presentation: clone quitBtn → strip LocalizedText/sprites → tk2dTextMesh from CurrentVersion.
    /// </summary>
    public static class MainMenuMultiplayerInject
    {
        private enum PanelView
        {
            Root,
            Host,
            Join
        }

        private const string MpButtonName = "YokWare_MultiplayerBtn";
        private const string PanelName = "YokWare_MenuPanel";
        private const string LabelName = "YokWare_Label";
        private const string TagKindMp = "mp";
        private const string TagKindPanel = "panel";
        private const string TagKindRow = "row";

        private const float RowSpacing = 60f;
        /// <summary>Nudge title MULTIPLAYER up toward EXIT (PositionMe offset units).</summary>
        private const float MpButtonNudgeUp = 16f;
        /// <summary>HOST/JOIN panel rows — tighter than title RowSpacing.</summary>
        private const float PanelRowSpacing = 46f;
        /// <summary>Panel tk2d labels vs Video/Profiles native size.</summary>
        private const float PanelLabelScale = 0.70f;
        private const int UiPollInterval = 15;
        private const float JoinTimeoutSec = 15f;
        private const float SteamJoinTimeoutSec = 35f;

        private static MainMenu _menu;
        private static GameObject _mpButton;
        private static GameObject _panel;

        private static GameObject _hostDoorBtn;
        private static GameObject _joinDoorBtn;
        private static GameObject _settingsBtn;
        private static GameObject _disconnectButton;
        private static GameObject _backRootBtn;
        private static GameObject _hostLanBtn;
        private static GameObject _hostSteamBtn;
        private static GameObject _joinLanBtn;
        private static GameObject _joinSteamBtn;
        private static GameObject _backSubBtn;

        private static PanelView _panelView = PanelView.Root;
        private static bool _joinViaSteam;
        private static bool _hostingHint;

        private static bool _joinPending;
        private static float _joinStartedAt;
        private static float _handshakeAt;
        private static bool _loggedWaitingWorld;
        private static bool _worldRequest10sSent;
        private static bool _worldRequest25sSent;
        private static int _lastUiPoll;

        private static int _boundMenu0Id;
        private static int _lastScreenW;
        private static int _lastScreenH;
        private static bool _menu0WasActive;
        private static bool _launchLobbyTried;

        private static GameObject ActiveJoinButton =>
            _joinViaSteam ? _joinSteamBtn : _joinLanBtn;

        public static void OnUpdate()
        {
            try
            {
                TryConsumeSteamLaunchLobby();
                if (_joinPending)
                    PollJoinState();
                else
                    PollPostHandshakeWorldWait();
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session, "join poll: " + ex.Message, ex);
            }

            if (Time.frameCount - _lastUiPoll < UiPollInterval)
                return;
            _lastUiPoll = Time.frameCount;

            try
            {
                TickUiLifecycle();
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session, "MainMenuMultiplayerInject: " + ex.Message, ex);
            }
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private static void TickUiLifecycle()
        {
            if (!Core.mainMenu)
            {
                SoftClearMenuCache();
                _menu0WasActive = false;
                return;
            }

            if (!ResolveMenu())
                return;

            bool menu0Active = _menu.Menu0 != null && _menu.Menu0.activeInHierarchy;
            bool panelActive = _panel != null && _panel && _panel.activeSelf;

            if (panelActive && menu0Active)
            {
                _panel.SetActive(false);
                panelActive = false;
            }

            if (menu0Active)
            {
                bool becameActive = !_menu0WasActive;
                int menu0Id = _menu.Menu0.GetInstanceID();
                bool menuRebuilt = menu0Id != _boundMenu0Id;
                bool resChanged = Screen.width != _lastScreenW || Screen.height != _lastScreenH;

                if (becameActive || menuRebuilt || !IsOwnedInteractive(_mpButton, TagKindMp))
                    EnsureMultiplayerButton(forceRebuild: menuRebuilt || !IsOwnedInteractive(_mpButton, TagKindMp));
                else if (resChanged)
                    RelayoutMultiplayerButton();

                _menu0WasActive = true;
            }
            else
            {
                _menu0WasActive = false;
            }

            if (panelActive)
                RefreshSessionButtons();
        }

        private static void SoftClearMenuCache()
        {
            if (_mpButton != null && !_mpButton)
                _mpButton = null;
            if (_panel != null && !_panel)
                ClearPanelRefs();
            _menu = null;
        }

        private static void ClearPanelRefs()
        {
            _panel = null;
            _hostDoorBtn = null;
            _joinDoorBtn = null;
            _settingsBtn = null;
            _disconnectButton = null;
            _backRootBtn = null;
            _hostLanBtn = null;
            _hostSteamBtn = null;
            _joinLanBtn = null;
            _joinSteamBtn = null;
            _backSubBtn = null;
        }

        private static bool ResolveMenu()
        {
            if (_menu == null)
                _menu = UnityEngine.Object.FindObjectOfType(typeof(MainMenu)) as MainMenu;
            return _menu != null && _menu.Menu0 != null && _menu.quitBtn != null;
        }

        private static void EnsureMultiplayerButton(bool forceRebuild)
        {
            if (!ResolveMenu())
                return;

            if (!forceRebuild && IsOwnedInteractive(_mpButton, TagKindMp))
            {
                WireButton(_mpButton, OpenPanel);
                return;
            }

            int purged = PurgeOurUiNearMenu();
            _mpButton = null;

            InjectMultiplayerButton();
            _boundMenu0Id = _menu.Menu0.GetInstanceID();
            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;

            if (purged > 0)
            {
                ModLog.Event(LogCat.Session,
                    "MULTIPLAYER rebuilt (purged " + purged + " stale node(s))");
            }
        }

        private static void RelayoutMultiplayerButton()
        {
            if (!IsOwnedInteractive(_mpButton, TagKindMp) || _menu?.Menu0 == null)
                return;
            float y = TitleMultiplayerOffsetY();
            SetRow(_mpButton, y);
            FitButtonHitbox(_mpButton);
            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;
        }

        private static bool IsOwnedInteractive(GameObject go, string kind)
        {
            if (go == null || !go)
                return false;
            if (!go.activeInHierarchy)
                return false;
            var tag = go.GetComponent<YokWareUiTag>();
            if (tag == null || tag.Kind != kind)
                return false;
            Button btn = go.GetComponent<Button>();
            if (btn == null || btn.disabled)
                return false;
            Collider col = go.GetComponent<Collider>();
            return col != null && col.enabled;
        }

        private static int PurgeOurUiNearMenu()
        {
            int n = 0;
            var roots = new List<Transform>(4);
            if (_menu?.Menu0 != null)
                roots.Add(_menu.Menu0.transform);
            if (_menu?.quitBtn != null && _menu.quitBtn.transform.parent != null)
                roots.Add(_menu.quitBtn.transform.parent);
            if (_menu?.Menu0 != null && _menu.Menu0.transform.parent != null)
                roots.Add(_menu.Menu0.transform.parent);

            var seen = new HashSet<int>();
            for (int r = 0; r < roots.Count; r++)
            {
                Transform root = roots[r];
                if (root == null)
                    continue;
                int rid = root.GetInstanceID();
                if (!seen.Add(rid))
                    continue;

                var kill = new List<GameObject>(8);
                CollectOurNodes(root, kill);
                for (int i = 0; i < kill.Count; i++)
                {
                    if (kill[i] == null || !kill[i])
                        continue;
                    try
                    {
                        UnityEngine.Object.DestroyImmediate(kill[i]);
                        n++;
                    }
                    catch
                    {
                        try { UnityEngine.Object.Destroy(kill[i]); n++; }
                        catch { /* ignore */ }
                    }
                }
            }

            ClearPanelRefs();
            return n;
        }

        private static void CollectOurNodes(Transform root, List<GameObject> kill)
        {
            if (root == null)
                return;
            YokWareUiTag[] tags = root.GetComponentsInChildren<YokWareUiTag>(true);
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] == null || tags[i].gameObject == null)
                    continue;
                if (tags[i].Kind == TagKindMp || tags[i].Kind == TagKindPanel)
                    kill.Add(tags[i].gameObject);
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null)
                    continue;
                if (t.name != MpButtonName && t.name != PanelName)
                    continue;
                if (t.GetComponent<YokWareUiTag>() != null)
                    continue;
                kill.Add(t.gameObject);
            }
        }

        // ------------------------------------------------------------------
        // Presentation
        // ------------------------------------------------------------------

        private static void InjectMultiplayerButton()
        {
            GameObject template = _menu.quitBtn;
            if (template == null || template.GetComponent<Button>() == null)
                return;

            // Title door: generated bevel art (matches PLAY/OPTIONS). Fallback = text label.
            _mpButton = CloneButton(template, template.transform.parent,
                MpButtonName, "MULTIPLAYER", OpenPanel, TagKindMp, useTextLabel: false);

            float y = TitleMultiplayerOffsetY();
            SetRow(_mpButton, y);
            WireButton(_mpButton, OpenPanel);

            // Attach after SetRow so collider bounds match final pose.
            if (!MenuButtonArt.TryAttachMultiplayerArt(_mpButton))
            {
                tk2dTextMesh tm = CreateLabel(_mpButton.transform, "MULTIPLAYER", settingsStyle: true);
                Button btn = _mpButton.GetComponent<Button>();
                if (btn != null && tm != null)
                    ApplySettingsButtonColors(btn, tm);
                FitButtonHitbox(_mpButton);
            }

            if (_mpButton != null)
                _mpButton.transform.SetAsLastSibling();

            ModLog.Event(LogCat.Session,
                "Injected MULTIPLAYER button @ " + Screen.width + "x" + Screen.height
                + " offsetY=" + y.ToString("F1"));
        }

        private static float TitleMultiplayerOffsetY()
        {
            // One row below EXIT, then nudge up so it sits closer to the vanilla stack.
            return ComputeVanillaLowestOffsetY() - RowSpacing + MpButtonNudgeUp;
        }

        private static float ComputeVanillaLowestOffsetY()
        {
            float lowest = 0f;
            if (_menu?.Menu0 == null)
                return lowest;
            PositionMe[] pms = _menu.Menu0.GetComponentsInChildren<PositionMe>(false);
            for (int i = 0; i < pms.Length; i++)
            {
                PositionMe pm = pms[i];
                if (pm == null || pm.gameObject == _mpButton)
                    continue;
                if (pm.GetComponent<YokWareUiTag>() != null)
                    continue;
                string n = pm.gameObject != null ? pm.gameObject.name : "";
                if (n.StartsWith("YokWare_", StringComparison.Ordinal))
                    continue;
                if (pm.offset.y < lowest)
                    lowest = pm.offset.y;
            }
            return lowest;
        }

        private static void BuildPanel()
        {
            if (_panel != null && _panel)
            {
                try { UnityEngine.Object.DestroyImmediate(_panel); }
                catch { UnityEngine.Object.Destroy(_panel); }
            }
            ClearPanelRefs();

            if (!ResolveMenu())
                return;
            GameObject template = _menu.quitBtn;
            if (template == null)
                return;

            _panel = new GameObject(PanelName);
            _panel.transform.SetParent(_menu.Menu0.transform.parent, false);
            Tag(_panel, TagKindPanel);

            _hostDoorBtn = CloneButton(template, _panel.transform, "YokWare_HostDoor", "HOST", () => ShowPanelView(PanelView.Host), TagKindRow);
            _joinDoorBtn = CloneButton(template, _panel.transform, "YokWare_JoinDoor", "JOIN", () => ShowPanelView(PanelView.Join), TagKindRow);
            _settingsBtn = CloneButton(template, _panel.transform, "YokWare_SettingsBtn", "SETTINGS", OnSettingsClicked, TagKindRow);
            _disconnectButton = CloneButton(template, _panel.transform, "YokWare_DiscBtn", "DISCONNECT", OnDisconnectClicked, TagKindRow);
            _backRootBtn = CloneButton(template, _panel.transform, "YokWare_BackRoot", "BACK", ClosePanel, TagKindRow);

            _hostLanBtn = CloneButton(template, _panel.transform, "YokWare_HostLan", "HOST LAN", OnHostLanClicked, TagKindRow);
            _hostSteamBtn = CloneButton(template, _panel.transform, "YokWare_HostSteam", "HOST STEAM", OnHostSteamClicked, TagKindRow);
            _joinLanBtn = CloneButton(template, _panel.transform, "YokWare_JoinLan", "JOIN LAN", OnJoinLanClicked, TagKindRow);
            _joinSteamBtn = CloneButton(template, _panel.transform, "YokWare_JoinSteam", "JOIN STEAM", OnJoinSteamClicked, TagKindRow);
            _backSubBtn = CloneButton(template, _panel.transform, "YokWare_BackSub", "BACK", () => ShowPanelView(PanelView.Root), TagKindRow);

            ShowPanelView(PanelView.Root);
        }

        private static void ShowPanelView(PanelView view)
        {
            _panelView = view;
            bool root = view == PanelView.Root;
            bool host = view == PanelView.Host;
            bool join = view == PanelView.Join;

            SetActiveSafe(_hostDoorBtn, root);
            SetActiveSafe(_joinDoorBtn, root);
            SetActiveSafe(_settingsBtn, root);
            SetActiveSafe(_backRootBtn, root);
            SetActiveSafe(_hostLanBtn, host);
            SetActiveSafe(_hostSteamBtn, host);
            SetActiveSafe(_joinLanBtn, join);
            SetActiveSafe(_joinSteamBtn, join);
            SetActiveSafe(_backSubBtn, host || join);

            var net = ModRuntime.Network as LanNetworkManager;
            bool online = net != null && net.Role != NetworkRole.Offline;
            SetActiveSafe(_disconnectButton, root && online);

            if (root)
            {
                int row = 0;
                SetRow(_hostDoorBtn, -PanelRowSpacing * row++);
                SetRow(_joinDoorBtn, -PanelRowSpacing * row++);
                SetRow(_settingsBtn, -PanelRowSpacing * row++);
                if (online)
                    SetRow(_disconnectButton, -PanelRowSpacing * row++);
                SetRow(_backRootBtn, -PanelRowSpacing * row);
            }
            else if (host)
            {
                SetRow(_hostLanBtn, 0f);
                SetRow(_hostSteamBtn, -PanelRowSpacing);
                SetRow(_backSubBtn, -PanelRowSpacing * 2f);
            }
            else
            {
                SetRow(_joinLanBtn, 0f);
                SetRow(_joinSteamBtn, -PanelRowSpacing);
                SetRow(_backSubBtn, -PanelRowSpacing * 2f);
            }

            RefreshSessionButtons();
        }

        private static void SetActiveSafe(GameObject go, bool active)
        {
            if (go != null && go)
                go.SetActive(active);
        }

        private static GameObject CloneButton(GameObject template, Transform parent,
            string name, string label, Action onFire, string tagKind, bool useTextLabel = true)
        {
            GameObject go = UnityEngine.Object.Instantiate(template, parent);
            go.name = name;
            go.SetActive(true);
            Tag(go, tagKind);

            LocalizedText[] locs = go.GetComponentsInChildren<LocalizedText>(true);
            for (int i = 0; i < locs.Length; i++)
            {
                if (locs[i] != null)
                    UnityEngine.Object.DestroyImmediate(locs[i]);
            }
            Renderer[] rends = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] != null)
                    rends[i].enabled = false;
            }

            StripChildColliders(go);
            Collider rootCol = go.GetComponent<Collider>();
            if (rootCol != null)
                rootCol.enabled = true;

            Button btn = go.GetComponent<Button>();
            if (btn != null)
            {
                btn.function = "";
                btn.popupType = "";
                btn.localized = false;
                btn.sprite = null;
                btn.disabled = false;
                btn.noRollover = false;
                btn.OnFire = () => Guarded(onFire);
            }

            if (useTextLabel)
            {
                // Panel rows: same outlined bitmap look as Video / Profiles menus.
                tk2dTextMesh tm = CreateLabel(go.transform, label, settingsStyle: true);
                if (tm != null)
                {
                    tm.transform.localScale *= PanelLabelScale;
                    if (btn != null)
                        ApplySettingsButtonColors(btn, tm);
                }
                FitButtonHitbox(go);
            }
            return go;
        }

        /// <summary>
        /// Resize the root BoxCollider to the visible art/label. quitBtn's hitbox is too
        /// narrow for MULTIPLAYER and too large for Options-sized panel rows — Button
        /// raycasts that collider for hover/click.
        /// </summary>
        internal static void FitButtonHitbox(GameObject buttonGo)
        {
            if (buttonGo == null || !buttonGo)
                return;

            Renderer visual = null;
            Transform art = buttonGo.transform.Find("YokWare_BtnArt");
            if (art != null)
                visual = art.GetComponent<Renderer>();
            if (visual == null)
            {
                tk2dTextMesh tm = buttonGo.GetComponentInChildren<tk2dTextMesh>(true);
                if (tm != null)
                    visual = tm.GetComponent<Renderer>();
            }
            if (visual == null)
                return;

            // Art quad includes transparent padding — shrink to opaque glyph UVs so the
            // hitbox matches the smaller idle letters (not the full padded canvas).
            Bounds wb;
            if (art != null && TryOpaqueQuadWorldBounds(art, visual, out wb))
            {
                // ok
            }
            else
            {
                wb = visual.bounds;
            }

            if (wb.size.sqrMagnitude < 0.01f)
                return;

            // Flat CamUI quads have ~0 thickness on one world axis — raycasts need depth.
            const float minThick = 12f;
            Vector3 size = wb.size;
            if (size.x <= size.y && size.x <= size.z)
                wb.Expand(new Vector3(Mathf.Max(0f, minThick - size.x), 0f, 0f));
            else if (size.y <= size.z)
                wb.Expand(new Vector3(0f, Mathf.Max(0f, minThick - size.y), 0f));
            else
                wb.Expand(new Vector3(0f, 0f, Mathf.Max(0f, minThick - size.z)));

            // Comfort pad so hover engages slightly outside the glyph.
            wb.Expand(new Vector3(wb.size.x * 0.10f, wb.size.y * 0.10f, wb.size.z * 0.10f));

            Transform t = buttonGo.transform;
            Vector3 c = wb.center;
            Vector3 e = wb.extents;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int xi = -1; xi <= 1; xi += 2)
            {
                for (int yi = -1; yi <= 1; yi += 2)
                {
                    for (int zi = -1; zi <= 1; zi += 2)
                    {
                        Vector3 local = t.InverseTransformPoint(c + new Vector3(e.x * xi, e.y * yi, e.z * zi));
                        min = Vector3.Min(min, local);
                        max = Vector3.Max(max, local);
                    }
                }
            }

            Vector3 localSize = max - min;
            if (localSize.x < 0.5f) localSize.x = 0.5f;
            if (localSize.y < 0.5f) localSize.y = 0.5f;
            if (localSize.z < 0.5f) localSize.z = 0.5f;

            BoxCollider box = buttonGo.GetComponent<BoxCollider>();
            if (box == null)
                box = buttonGo.AddComponent<BoxCollider>();
            box.center = (min + max) * 0.5f;
            box.size = localSize;
            box.enabled = true;

            // Only the root collider should receive the menu raycast.
            StripChildColliders(buttonGo);
        }

        /// <summary>
        /// World bounds of the opaque region of the CamUI-facing unit quad (−0.5..0.5).
        /// Uses idle texture when present so padding/bloom does not inflate the hitbox.
        /// </summary>
        private static bool TryOpaqueQuadWorldBounds(Transform art, Renderer visual, out Bounds worldBounds)
        {
            worldBounds = default;
            if (art == null || visual == null)
                return false;

            Texture2D tex = null;
            if (visual.sharedMaterial != null)
                tex = visual.sharedMaterial.mainTexture as Texture2D;

            // Prefer idle resource (stable letter core — ignores hover bloom padding).
            if (!MenuButtonArt.TryGetIdleOpaqueUv(out float u0, out float v0, out float u1, out float v1))
            {
                if (tex == null || !TryOpaqueUv(tex, 28, out u0, out v0, out u1, out v1))
                    return false;
            }

            // Unit quad mesh: UV (0,0)=(-0.5,-0.5), (1,1)=(0.5,0.5)
            Vector3[] corners =
            {
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u0), Mathf.Lerp(-0.5f, 0.5f, v0), 0f)),
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u1), Mathf.Lerp(-0.5f, 0.5f, v0), 0f)),
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u0), Mathf.Lerp(-0.5f, 0.5f, v1), 0f)),
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u1), Mathf.Lerp(-0.5f, 0.5f, v1), 0f)),
            };
            worldBounds = new Bounds(corners[0], Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
                worldBounds.Encapsulate(corners[i]);
            return worldBounds.size.sqrMagnitude > 0.01f;
        }

        private static bool TryOpaqueUv(Texture2D tex, byte alphaThr,
            out float u0, out float v0, out float u1, out float v1)
        {
            u0 = v0 = 0f;
            u1 = v1 = 1f;
            if (tex == null)
                return false;
            try
            {
                Color32[] px = tex.GetPixels32();
                int w = tex.width;
                int h = tex.height;
                int xMin = w, xMax = -1, yMin = h, yMax = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (px[row + x].a <= alphaThr)
                            continue;
                        if (x < xMin) xMin = x;
                        if (x > xMax) xMax = x;
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                    }
                }
                if (xMax < xMin || yMax < yMin)
                    return false;
                u0 = xMin / (float)w;
                u1 = (xMax + 1) / (float)w;
                v0 = yMin / (float)h;
                v1 = (yMax + 1) / (float)h;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Vanilla Options hover = idle gray → rollover white. We had forced both to white.
        /// </summary>
        private static void ApplySettingsButtonColors(Button btn, tk2dTextMesh tm)
        {
            if (btn == null || tm == null)
                return;

            Button refBtn = FindSettingsStyleButton();
            Color idle;
            Color hover;
            if (refBtn != null)
            {
                hover = refBtn.rolloverColor;
                if (refBtn.textMesh != null)
                    idle = refBtn.textMesh.color;
                else
                    idle = refBtn.baseColor;
            }
            else
            {
                idle = new Color(0.55f, 0.55f, 0.55f, 1f);
                hover = Color.white;
            }

            // If the reference was already hovered/white, keep a visible delta.
            if (ColorsNearlyEqual(idle, hover))
            {
                idle = new Color(0.55f, 0.55f, 0.55f, 1f);
                hover = Color.white;
            }

            tm.color = idle;
            tm.color2 = idle;
            tm.Commit();
            btn.baseColor = idle;
            btn.rolloverColor = hover;
            btn.textMesh = tm;
        }

        private static bool ColorsNearlyEqual(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.04f
                && Mathf.Abs(a.g - b.g) < 0.04f
                && Mathf.Abs(a.b - b.b) < 0.04f
                && Mathf.Abs(a.a - b.a) < 0.04f;
        }

        private static Button FindSettingsStyleButton()
        {
            if (_menu == null)
                return null;
            if (_menu.VideoMenu != null)
            {
                Transform fs = _menu.VideoMenu.transform.Find("FullscreenBtn");
                if (fs != null)
                {
                    Button b = fs.GetComponent<Button>();
                    if (b != null && b.textMesh != null)
                        return b;
                }
                Button[] btns = _menu.VideoMenu.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < btns.Length; i++)
                {
                    if (btns[i] != null && btns[i].textMesh != null)
                        return btns[i];
                }
            }
            if (_menu.profilesMenuBack != null)
            {
                Button back = _menu.profilesMenuBack.GetComponent<Button>();
                if (back != null && back.textMesh != null)
                    return back;
            }
            return null;
        }

        private static void WireButton(GameObject go, Action onFire)
        {
            if (go == null || !go)
                return;
            Button btn = go.GetComponent<Button>();
            if (btn == null)
                return;
            btn.disabled = false;
            btn.noRollover = false;
            btn.function = "";
            btn.popupType = "";
            btn.localized = false;
            btn.OnFire = () => Guarded(onFire);
            StripChildColliders(go);
            Collider rootCol = go.GetComponent<Collider>();
            if (rootCol != null)
                rootCol.enabled = true;
            if (btn.textMesh == null)
            {
                tk2dTextMesh tm = go.GetComponentInChildren<tk2dTextMesh>(true);
                if (tm != null)
                {
                    btn.textMesh = tm;
                    btn.baseColor = tm.color;
                }
            }
        }

        private static void StripChildColliders(GameObject root)
        {
            if (root == null)
                return;
            Collider[] cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || c.gameObject == root)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(c); }
                catch { c.enabled = false; }
            }
        }

        private static void Tag(GameObject go, string kind)
        {
            if (go == null)
                return;
            YokWareUiTag tag = go.GetComponent<YokWareUiTag>();
            if (tag == null)
                tag = go.AddComponent<YokWareUiTag>();
            tag.Kind = kind;
        }

        private static tk2dTextMesh FindLabelSource(bool settingsStyle)
        {
            if (_menu == null)
                return null;

            if (settingsStyle)
            {
                // Video options / Profiles use the outlined white bitmap look from the screenshots.
                if (_menu.VideoMenu != null)
                {
                    Transform fs = _menu.VideoMenu.transform.Find("FullscreenBtn");
                    if (fs != null)
                    {
                        Button b = fs.GetComponent<Button>();
                        if (b != null && b.textMesh != null)
                            return b.textMesh;
                    }
                    Button[] btns = _menu.VideoMenu.GetComponentsInChildren<Button>(true);
                    for (int i = 0; i < btns.Length; i++)
                    {
                        if (btns[i] != null && btns[i].textMesh != null)
                            return btns[i].textMesh;
                    }
                }
                if (_menu.profilesMenuBack != null)
                {
                    Button back = _menu.profilesMenuBack.GetComponent<Button>();
                    if (back != null && back.textMesh != null)
                        return back.textMesh;
                    tk2dTextMesh backTm = _menu.profilesMenuBack.GetComponentInChildren<tk2dTextMesh>(true);
                    if (backTm != null)
                        return backTm;
                }
            }

            return _menu.CurrentVersion;
        }

        private static tk2dTextMesh CreateLabel(Transform parent, string text, bool settingsStyle)
        {
            tk2dTextMesh source = FindLabelSource(settingsStyle);
            if (source == null)
            {
                ModLog.Warn(LogCat.Session, "Menu label source missing — button label blank");
                return null;
            }

            GameObject labelGo = UnityEngine.Object.Instantiate(source.gameObject, parent);
            labelGo.name = LabelName;

            MonoBehaviour[] mbs = labelGo.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < mbs.Length; i++)
            {
                if (mbs[i] == null || mbs[i] is tk2dTextMesh)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(mbs[i]); }
                catch { /* ignore */ }
            }
            Collider[] cols = labelGo.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(cols[i]); }
                catch { /* ignore */ }
            }

            labelGo.transform.localPosition = Vector3.zero;
            labelGo.transform.localRotation = Quaternion.identity;
            labelGo.transform.localScale = source.transform.localScale;
            labelGo.SetActive(true);

            tk2dTextMesh tm = labelGo.GetComponent<tk2dTextMesh>();
            if (tm == null)
                return null;
            tm.anchor = TextAnchor.MiddleCenter;
            if (tm.maxChars < text.Length + 4)
                tm.maxChars = text.Length + 8;
            tm.text = text;
            // Colors: settingsStyle applied by ApplySettingsButtonColors (idle≠hover).
            // Non-settings fallback keeps source colors.
            tm.Commit();

            Collider col = parent.GetComponent<Collider>();
            Renderer rend = labelGo.GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = true;

            if (col != null && rend != null
                && rend.bounds.size.x > 0.001f && rend.bounds.size.y > 0.001f)
            {
                if (settingsStyle)
                {
                    // Keep Video/Profiles native size — only shrink if wider than hitbox.
                    // Scaling up to the title quit collider made HOST/JOIN huge vs Options.
                    if (TryHitboxFace(parent.gameObject, out float faceW, out _))
                    {
                        float fitW = faceW * 0.95f / rend.bounds.size.x;
                        if (fitW < 0.99f)
                            labelGo.transform.localScale *= Mathf.Max(fitW, 0.35f);
                    }
                }
                else if (TryHitboxFace(parent.gameObject, out float faceW, out float faceH))
                {
                    float fitH = faceH * 0.62f / rend.bounds.size.y;
                    float fitW = faceW * 0.92f / rend.bounds.size.x;
                    float factor = Mathf.Clamp(Mathf.Min(fitH, fitW), 0.02f, 50f);
                    labelGo.transform.localScale *= factor;
                }

                Vector3 pos = parent.position;
                GameObject camObj = Core.CamUI;
                Camera cam = camObj != null ? camObj.GetComponent<Camera>() : null;
                if (cam != null)
                    pos -= cam.transform.forward * 1f;
                labelGo.transform.position = pos;
            }
            return tm;
        }

        /// <summary>
        /// Visible face of a flat UI BoxCollider (world AABB.y is often ~0 under CamUI).
        /// </summary>
        private static bool TryHitboxFace(GameObject go, out float faceW, out float faceH)
        {
            faceW = faceH = 0f;
            if (go == null)
                return false;
            var box = go.GetComponent<BoxCollider>();
            if (box == null)
                return false;
            Vector3 lossy = go.transform.lossyScale;
            float ax = Mathf.Abs(box.size.x * lossy.x);
            float ay = Mathf.Abs(box.size.y * lossy.y);
            float az = Mathf.Abs(box.size.z * lossy.z);
            float t = ax, u = ay, v = az;
            if (t > u) { float s = t; t = u; u = s; }
            if (u > v) { float s = u; u = v; v = s; }
            if (t > u) { float s = t; t = u; u = s; }
            faceH = u;
            faceW = v;
            return faceH > 0.05f && faceW > 0.05f;
        }

        private static void SetRow(GameObject go, float y)
        {
            if (go == null)
                return;
            PositionMe pm = go.GetComponent<PositionMe>();
            if (pm == null)
                return;
            pm.offset = new Vector2(pm.offset.x, y);
            pm.init();
        }

        private static void Guarded(Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session, "menu click: " + ex.Message, ex);
            }
        }

        // ------------------------------------------------------------------
        // Click handlers
        // ------------------------------------------------------------------

        private static void OpenPanel()
        {
            ModLog.Event(LogCat.Session, "MULTIPLAYER menu opened");
            MultiplayerMenu.PushFieldsToConfig();
            if (_panel == null || !_panel)
                BuildPanel();
            if (_panel == null || _menu == null)
                return;
            _menu.Menu0.SetActive(false);
            _panel.SetActive(true);
            ShowPanelView(PanelView.Root);
        }

        private static void ClosePanel()
        {
            if (_panel != null && _panel)
                _panel.SetActive(false);
            if (_menu != null && _menu.Menu0 != null)
                _menu.Menu0.SetActive(true);
        }

        private static void OnHostLanClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null)
                return;
            if (net.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Already in a session — use DISCONNECT first.");
                return;
            }

            int port = ModConfig.ConnectPort != null ? ModConfig.ConnectPort.Value : PluginInfo.DefaultPort;
            if (port < 1 || port > 65535)
                port = PluginInfo.DefaultPort;

            net.StartHost(port);
            if (net.Role != NetworkRole.Host)
            {
                ModLog.Event(LogCat.Session, "Host failed: " + (net.StatusText ?? "bind error"));
                return;
            }

            _joinPending = false;
            _hostingHint = true;
            ModLog.Event(LogCat.Session,
                "Hosting LAN on port " + port
                + " — load a save; clients on JOIN get the world after you are in-chapter.");
            ClosePanel();
            if (_menu != null)
                _menu.displayProfilesMenu();
        }

        private static void OnHostSteamClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null)
                return;
            if (net.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Already in a session — use DISCONNECT first.");
                return;
            }

            net.StartHostSteam();
            if (net.Role != NetworkRole.Host)
            {
                ModLog.Event(LogCat.Session, "Steam host failed: " + (net.StatusText ?? "steam error"));
                return;
            }

            _joinPending = false;
            _hostingHint = true;
            ModLog.Event(LogCat.Session,
                "Hosting Steam lobby — invite via overlay (SETTINGS shows lobby id). "
                + "Load a save; clients join after you are in-chapter.");
            ClosePanel();
            if (_menu != null)
                _menu.displayProfilesMenu();
        }

        private static void OnJoinLanClicked()
        {
            _joinViaSteam = false;
            BeginOrContinueJoin(steam: false);
        }

        private static void OnJoinSteamClicked()
        {
            _joinViaSteam = true;
            BeginOrContinueJoin(steam: true);
        }

        private static void BeginOrContinueJoin(bool steam)
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null || _joinPending)
                return;

            var lanReady = net as LanNetworkManager;
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetJoinProgress("CHOOSE SLOT");
                JoinWorldSlotPicker.EnsureExists();
                return;
            }
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingEnterWorld)
            {
                if (lanReady.WorldSaveShare.HasTerminalShareFailure)
                {
                    ModLog.Warn(LogCat.Session,
                        "ENTER WORLD blocked — " + lanReady.WorldSaveShare.ProgressText);
                    SetJoinProgress("SHARE FAIL");
                    return;
                }
                if (lanReady.WorldSaveShare.TryBeginEnterWorld())
                {
                    SetJoinProgress("LOADING…");
                    ModLog.Event(LogCat.Session, "ENTER WORLD — starting offline load (phase 2)");
                }
                return;
            }

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete && Core.mainMenu)
            {
                var lan = net as LanNetworkManager;
                if (lan?.WorldSaveShare != null && lan.WorldSaveShare.IsClientReceivingOrApplying)
                {
                    SetJoinProgress("DOWNLOADING…");
                    return;
                }
                if (lan != null && lan.RequestHostWorld("join-button"))
                {
                    SetJoinProgress("REQUESTING WORLD…");
                    ModLog.Event(LogCat.Session, "JOIN while connected — WorldRequest sent to host.");
                }
                else
                {
                    SetJoinProgress("WAITING…");
                    ModLog.Event(LogCat.Session,
                        "JOIN while connected — request rate-limited or share already in progress.");
                }
                return;
            }

            if (net.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Already in a session — use DISCONNECT first.");
                return;
            }

            if (steam)
            {
                string lobby = (ModConfig.SteamLobbyId != null ? ModConfig.SteamLobbyId.Value : "") ?? "";
                lobby = lobby.Trim();
                if (string.IsNullOrEmpty(lobby))
                {
                    ModLog.Event(LogCat.Session, "JOIN STEAM: set lobby id in SETTINGS (or accept a Steam invite).");
                    MultiplayerMenu.ShowSettings();
                    return;
                }

                net.ConnectSteam(lobby);
                _joinPending = true;
                _joinStartedAt = Time.realtimeSinceStartup;
                _handshakeAt = 0f;
                _loggedWaitingWorld = false;
                _worldRequest10sSent = false;
                _worldRequest25sSent = false;
                SetJoinProgress("STEAM…");
                ModLog.Event(LogCat.Session, "Connecting Steam lobby " + lobby + " …");
                return;
            }

            string ip = (ModConfig.ConnectAddress != null ? ModConfig.ConnectAddress.Value : "127.0.0.1") ?? "127.0.0.1";
            ip = ip.Trim();
            if (string.IsNullOrEmpty(ip))
                ip = "127.0.0.1";

            int port = ModConfig.ConnectPort != null ? ModConfig.ConnectPort.Value : PluginInfo.DefaultPort;
            if (port < 1 || port > 65535)
                port = PluginInfo.DefaultPort;

            net.ConnectToHost(ip, port);
            _joinPending = true;
            _joinStartedAt = Time.realtimeSinceStartup;
            _handshakeAt = 0f;
            _loggedWaitingWorld = false;
            _worldRequest10sSent = false;
            _worldRequest25sSent = false;
            SetJoinProgress("CONNECTING…");
            ModLog.Event(LogCat.Session, "Connecting to " + ip + ":" + port + " …");
        }

        private static void OnSettingsClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.ShowSettings();
        }

        private static void OnDisconnectClicked()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null)
                return;
            _joinPending = false;
            _hostingHint = false;
            MultiplayerMenu.ClearHostNextStepHint();
            if (net.Role == NetworkRole.Host && net.TryGracefulHostLeave())
            {
                ResetJoinLabelsIdle();
                RefreshSessionButtons();
                ModLog.Event(LogCat.Session, "Host disconnect — handing off to elect…");
                return;
            }
            net.StopNetwork();
            ResetJoinLabelsIdle();
            RefreshSessionButtons();
            ModLog.Event(LogCat.Session, "Disconnected.");
        }

        // ------------------------------------------------------------------
        // Join / session feedback
        // ------------------------------------------------------------------

        private static void SetJoinProgress(string text)
        {
            ResetInactiveJoinLabel();
            SetLabel(ActiveJoinButton, text);
        }

        private static void ResetInactiveJoinLabel()
        {
            if (_joinViaSteam)
                SetLabel(_joinLanBtn, "JOIN LAN");
            else
                SetLabel(_joinSteamBtn, "JOIN STEAM");
        }

        private static void ResetJoinLabelsIdle()
        {
            SetLabel(_joinLanBtn, "JOIN LAN");
            SetLabel(_joinSteamBtn, "JOIN STEAM");
        }

        private static void PollJoinState()
        {
            var net = ModRuntime.Network;
            if (net == null)
            {
                _joinPending = false;
                return;
            }

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete)
            {
                bool firstReady = _joinPending;
                _joinPending = false;
                if (firstReady)
                {
                    _handshakeAt = Time.realtimeSinceStartup;
                    _loggedWaitingWorld = false;
                    _worldRequest10sSent = false;
                    _worldRequest25sSent = false;
                    ModLog.Event(LogCat.Session,
                        "Connected to host — waiting for world share / auto-load…");
                }
                UpdateJoinLabelFromShare(net);
                RefreshSessionButtons();
                return;
            }

            if (net.Role == NetworkRole.Host)
            {
                _joinPending = false;
                ResetJoinLabelsIdle();
                RefreshSessionButtons();
                return;
            }

            bool steamJoin = net.IsSteamSession || _joinViaSteam;
            float joinTimeout = steamJoin ? SteamJoinTimeoutSec : JoinTimeoutSec;
            if (net.Role == NetworkRole.Offline
                || Time.realtimeSinceStartup - _joinStartedAt > joinTimeout)
            {
                bool wasTimeout = net.Role != NetworkRole.Offline;
                _joinPending = false;
                if (wasTimeout)
                    net.StopNetwork();
                ResetJoinLabelsIdle();
                RefreshSessionButtons();
                ModLog.Event(LogCat.Session,
                    wasTimeout
                        ? (steamJoin
                            ? "Steam join timeout — lobby id / password / proto / friends, or SNS relay."
                            : "Join timeout — check IP/port/password in SETTINGS (and firewall).")
                        : "Connection closed.");
            }
        }

        private static void PollPostHandshakeWorldWait()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Client || !net.IsHandshakeComplete)
                return;
            if (!Core.mainMenu)
                return;

            UpdateJoinLabelFromShare(net);

            if (_handshakeAt <= 0f)
                return;

            if (net.WorldSaveShare != null
                && (net.WorldSaveShare.IsAwaitingSlotPick
                    || net.WorldSaveShare.IsAwaitingEnterWorld
                    || net.WorldSaveShare.IsClientReceivingOrApplying))
                return;

            float waited = Time.realtimeSinceStartup - _handshakeAt;
            bool receiving = IsShareProgressActive(net);

            if (!_loggedWaitingWorld && waited > 8f && !receiving)
            {
                _loggedWaitingWorld = true;
                ModLog.Warn(LogCat.Session,
                    "Still on title 8s after handshake with no world download. "
                    + "Host must be IN the chapter (not title). Auto WorldRequest at 10s; or press JOIN again / host F2 Resend.");
            }

            if (!receiving && waited >= 10f && !_worldRequest10sSent)
            {
                _worldRequest10sSent = true;
                if (net.RequestHostWorld("title-wait-10s"))
                    SetJoinProgress("REQUESTING WORLD…");
            }
            else if (!receiving && waited >= 25f && !_worldRequest25sSent)
            {
                _worldRequest25sSent = true;
                if (net.RequestHostWorld("title-wait-25s"))
                    SetJoinProgress("REQUESTING WORLD…");
            }
        }

        private static bool IsShareProgressActive(LanNetworkManager net)
        {
            if (net?.WorldSaveShare == null)
                return false;
            if (net.WorldSaveShare.IsClientReceivingOrApplying)
                return true;
            if (net.WorldSaveShare.IsAwaitingSlotPick || net.WorldSaveShare.IsAwaitingEnterWorld)
                return true;
            string prog = net.WorldSaveShare.ProgressText ?? "";
            if (string.IsNullOrEmpty(prog))
                return false;
            return prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Appl", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Pick a profile", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Same world", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("ENTER WORLD", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Permanent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsShareFailureBlocked(LanNetworkManager net)
        {
            var share = net?.WorldSaveShare;
            return share != null && share.HasTerminalShareFailure;
        }

        private static void UpdateJoinLabelFromShare(LanNetworkManager net)
        {
            if (net == null)
                return;
            if (IsShareFailureBlocked(net))
            {
                SetJoinProgress("SHARE FAIL");
                return;
            }
            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetJoinProgress("CHOOSE SLOT");
                return;
            }
            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingEnterWorld)
            {
                SetJoinProgress("ENTER WORLD");
                return;
            }
            string prog = net.WorldSaveShare != null ? net.WorldSaveShare.ProgressText : null;
            if (!string.IsNullOrEmpty(prog))
            {
                if (prog.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("SHARE FAIL");
                else if (prog.IndexOf("ENTER WORLD", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Permanent copy", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("World ready", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("ENTER WORLD");
                else if (prog.IndexOf("Pick a profile", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("permanent", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("CHOOSE SLOT");
                else if (prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Writ", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Inflat", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Verif", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("DOWNLOADING…");
                else if (prog.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0
                         || prog.IndexOf("Appl", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("LOADING…");
                else if (prog.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("REQUESTING WORLD…");
                else
                    SetJoinProgress("CONNECTED");
            }
            else if (net.IsHandshakeComplete)
            {
                SetJoinProgress("CONNECTED");
            }
        }

        private static void RefreshSessionButtons()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            bool online = net != null && net.Role != NetworkRole.Offline;

            if (_panelView == PanelView.Root)
            {
                SetActiveSafe(_disconnectButton, online);
                // Relayout root when disconnect appears/disappears
                int row = 0;
                SetRow(_hostDoorBtn, -PanelRowSpacing * row++);
                SetRow(_joinDoorBtn, -PanelRowSpacing * row++);
                SetRow(_settingsBtn, -PanelRowSpacing * row++);
                if (online)
                    SetRow(_disconnectButton, -PanelRowSpacing * row++);
                SetRow(_backRootBtn, -PanelRowSpacing * row);
            }

            if (net != null && net.Role == NetworkRole.Host && _hostingHint)
                SetLabel(_hostDoorBtn, "HOSTING — LOAD SAVE");
            else if (!online)
            {
                _hostingHint = false;
                SetLabel(_hostDoorBtn, "HOST");
            }

            if (_joinPending)
                return;

            if (net == null)
                return;

            if (net.Role == NetworkRole.Host)
            {
                ResetJoinLabelsIdle();
                return;
            }

            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingSlotPick)
                SetJoinProgress("CHOOSE SLOT");
            else if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingEnterWorld
                     && !IsShareFailureBlocked(net))
                SetJoinProgress("ENTER WORLD");
            else if (net.Role == NetworkRole.Client && net.IsHandshakeComplete)
                UpdateJoinLabelFromShare(net);
            else if (!online)
                ResetJoinLabelsIdle();
        }

        private static void SetLabel(GameObject buttonGo, string text)
        {
            if (buttonGo == null || !buttonGo)
                return;
            tk2dTextMesh tm = buttonGo.GetComponentInChildren<tk2dTextMesh>(true);
            if (tm == null)
                return;
            if (tm.text == text)
                return;
            tm.text = text;
            tm.Commit();
            FitButtonHitbox(buttonGo);
        }

        private static void TryConsumeSteamLaunchLobby()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return;

            net.EnsureSteamCallbacks();

            if (_launchLobbyTried)
                return;
            try
            {
                if (!Core.mainMenu)
                    return;
            }
            catch { return; }

            _launchLobbyTried = true;
            net.TryConsumePendingSteamLaunchLobby();
        }

        private sealed class YokWareUiTag : MonoBehaviour
        {
            public string Kind;
        }
    }
}
