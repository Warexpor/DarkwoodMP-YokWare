using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Native tk2d title MULTIPLAYER button — <b>Yokyy presentation</b>, hardened lifecycle.
    ///
    /// Presentation (do not freestyle):
    ///   clone quitBtn → strip LocalizedText/sprites → root collider only →
    ///   tk2dTextMesh label from MainMenu.CurrentVersion → PositionMe.offset + init().
    ///
    /// Lifecycle (this overhaul):
    ///   edge-triggered inject when Menu0 becomes showable; one owned GO;
    ///   DestroyImmediate purge of our tags only (no scene-wide Find spam);
    ///   never stack; heal wiring without rebuilding when still interactive.
    /// </summary>
    public static class MainMenuMultiplayerInject
    {
        private const string MpButtonName = "YokWare_MultiplayerBtn";
        private const string PanelName = "YokWare_MenuPanel";
        private const string LabelName = "YokWare_Label";
        private const string TagKindMp = "mp";
        private const string TagKindPanel = "panel";
        private const string TagKindRow = "row";

        private const float RowSpacing = 60f;
        private const int UiPollInterval = 15;
        private const float JoinTimeoutSec = 15f;

        private static MainMenu _menu;
        private static GameObject _mpButton;
        private static GameObject _panel;
        private static GameObject _joinButton;
        private static GameObject _disconnectButton;

        private static bool _joinPending;
        private static float _joinStartedAt;
        private static float _handshakeAt;
        private static bool _loggedWaitingWorld;
        private static bool _worldRequest10sSent;
        private static bool _worldRequest25sSent;
        private static int _lastUiPoll;

        /// <summary>Menu0 instance id we last injected for (scene rebuild → re-inject).</summary>
        private static int _boundMenu0Id;
        private static int _lastScreenW;
        private static int _lastScreenH;
        private static bool _menu0WasActive;

        public static void OnUpdate()
        {
            try
            {
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
        // Lifecycle (ownership, no inject storms)
        // ------------------------------------------------------------------

        private static void TickUiLifecycle()
        {
            if (!Core.mainMenu)
            {
                // In-chapter: keep owned GOs (Menu0 often survives for ESC). Drop menu cache only.
                SoftClearMenuCache();
                _menu0WasActive = false;
                return;
            }

            if (!ResolveMenu())
                return;

            bool menu0Active = _menu.Menu0 != null && _menu.Menu0.activeInHierarchy;
            bool panelActive = _panel != null && _panel && _panel.activeSelf;

            // ESC re-showed Menu0 while panel still open — yield to vanilla menu
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
            {
                _panel = null;
                _joinButton = null;
                _disconnectButton = null;
            }
            _menu = null;
        }

        private static bool ResolveMenu()
        {
            if (_menu == null)
                _menu = UnityEngine.Object.FindObjectOfType(typeof(MainMenu)) as MainMenu;
            return _menu != null && _menu.Menu0 != null && _menu.quitBtn != null;
        }

        /// <summary>
        /// One interactive MULTIPLAYER row under quitBtn's parent. Presentation = Yokyy.
        /// </summary>
        private static void EnsureMultiplayerButton(bool forceRebuild)
        {
            if (!ResolveMenu())
                return;

            if (!forceRebuild && IsOwnedInteractive(_mpButton, TagKindMp))
            {
                WireButton(_mpButton, OpenPanel);
                return;
            }

            // Scoped purge — only our tagged/named nodes near this menu (no full-scene scan).
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
            float y = ComputeVanillaLowestOffsetY() - RowSpacing;
            SetRow(_mpButton, y);
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

        /// <summary>
        /// DestroyImmediate only YokWare_* under Menu0 / quit parent / panel parent.
        /// Avoids FindObjectsOfType thrash and never touches vanilla buttons.
        /// </summary>
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

                // Children first — collect, then destroy
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

            _panel = null;
            _joinButton = null;
            _disconnectButton = null;
            return n;
        }

        private static void CollectOurNodes(Transform root, List<GameObject> kill)
        {
            if (root == null)
                return;
            // Depth-first: destroy leaves via list (DestroyImmediate on parent kills children)
            YokWareUiTag[] tags = root.GetComponentsInChildren<YokWareUiTag>(true);
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] == null || tags[i].gameObject == null)
                    continue;
                // Only top-level owned roots (mp button, panel) — not labels (children of button)
                if (tags[i].Kind == TagKindMp || tags[i].Kind == TagKindPanel)
                    kill.Add(tags[i].gameObject);
            }

            // Legacy name-only clones from older builds (no tag)
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null)
                    continue;
                if (t.name != MpButtonName && t.name != PanelName)
                    continue;
                if (t.GetComponent<YokWareUiTag>() != null)
                    continue; // already queued via tag
                kill.Add(t.gameObject);
            }
        }

        // ------------------------------------------------------------------
        // Presentation (Yokyy — clone quit, label, PositionMe)
        // ------------------------------------------------------------------

        private static void InjectMultiplayerButton()
        {
            GameObject template = _menu.quitBtn;
            if (template == null || template.GetComponent<Button>() == null)
                return;

            _mpButton = CloneButton(template, template.transform.parent,
                MpButtonName, "MULTIPLAYER", OpenPanel, TagKindMp);

            float y = ComputeVanillaLowestOffsetY() - RowSpacing;
            SetRow(_mpButton, y);
            WireButton(_mpButton, OpenPanel);
            if (_mpButton != null)
                _mpButton.transform.SetAsLastSibling();

            ModLog.Event(LogCat.Session,
                "Injected MULTIPLAYER button @ " + Screen.width + "x" + Screen.height
                + " offsetY=" + y.ToString("F1"));
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
            _panel = null;
            _joinButton = null;
            _disconnectButton = null;

            if (!ResolveMenu())
                return;
            GameObject template = _menu.quitBtn;
            if (template == null)
                return;

            _panel = new GameObject(PanelName);
            _panel.transform.SetParent(_menu.Menu0.transform.parent, false);
            Tag(_panel, TagKindPanel);

            GameObject host = CloneButton(template, _panel.transform, "YokWare_HostBtn", "HOST LAN", OnHostClicked, TagKindRow);
            _joinButton = CloneButton(template, _panel.transform, "YokWare_JoinBtn", "JOIN LAN", OnJoinClicked, TagKindRow);
            GameObject hostSteam = CloneButton(template, _panel.transform, "YokWare_HostSteamBtn", "HOST STEAM", OnHostSteamClicked, TagKindRow);
            GameObject joinSteam = CloneButton(template, _panel.transform, "YokWare_JoinSteamBtn", "JOIN STEAM", OnJoinSteamClicked, TagKindRow);
            GameObject settings = CloneButton(template, _panel.transform, "YokWare_SettingsBtn", "SETTINGS", OnSettingsClicked, TagKindRow);
            GameObject restore = CloneButton(template, _panel.transform, "YokWare_RestoreBtn", "RESTORE SELF", OnRestoreClicked, TagKindRow);
            _disconnectButton = CloneButton(template, _panel.transform, "YokWare_DiscBtn", "DISCONNECT", OnDisconnectClicked, TagKindRow);
            GameObject back = CloneButton(template, _panel.transform, "YokWare_BackBtn", "BACK", ClosePanel, TagKindRow);

            // Yokyy order: label laid out at current pose, then SetRow moves parent.
            SetRow(host, 0f);
            SetRow(_joinButton, -RowSpacing);
            SetRow(hostSteam, -RowSpacing * 2f);
            SetRow(joinSteam, -RowSpacing * 3f);
            SetRow(settings, -RowSpacing * 4f);
            SetRow(restore, -RowSpacing * 5f);
            SetRow(_disconnectButton, -RowSpacing * 6f);
            SetRow(back, -RowSpacing * 7f);

            RefreshSessionButtons();
        }

        private static GameObject CloneButton(GameObject template, Transform parent,
            string name, string label, Action onFire, string tagKind)
        {
            GameObject go = UnityEngine.Object.Instantiate(template, parent);
            go.name = name;
            go.SetActive(true);
            Tag(go, tagKind);

            // Texture-word title buttons → strip art, use editable label (Yokyy).
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

            // Only root collider — child colliders steal hover (dead button symptom).
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

            tk2dTextMesh tm = CreateLabel(go.transform, label);
            if (btn != null && tm != null)
            {
                btn.textMesh = tm;
                btn.baseColor = tm.color;
            }
            return go;
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

        /// <summary>
        /// Label = clone of MainMenu.CurrentVersion (game font). Strip PositionMe.
        /// Size/place against button collider in world space (Yokyy).
        /// </summary>
        private static tk2dTextMesh CreateLabel(Transform parent, string text)
        {
            tk2dTextMesh source = _menu != null ? _menu.CurrentVersion : null;
            if (source == null)
            {
                ModLog.Warn(LogCat.Session, "MainMenu.CurrentVersion missing — button label blank");
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
            tm.text = text;
            tm.Commit();

            Collider col = parent.GetComponent<Collider>();
            Renderer rend = labelGo.GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = true;

            if (col != null && rend != null
                && rend.bounds.size.y > 0.001f && rend.bounds.size.x > 0.001f)
            {
                float fitH = col.bounds.size.y * 0.65f / rend.bounds.size.y;
                float fitW = col.bounds.size.x * 1.05f / rend.bounds.size.x;
                float factor = Mathf.Clamp(Mathf.Min(fitH, fitW), 0.02f, 50f);
                labelGo.transform.localScale *= factor;

                Vector3 pos = col.bounds.center;
                GameObject camObj = Core.CamUI;
                Camera cam = camObj != null ? camObj.GetComponent<Camera>() : null;
                if (cam != null)
                    pos -= cam.transform.forward * 1f;
                labelGo.transform.position = pos;
            }
            return tm;
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
        // Click handlers (Horde network)
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
            RefreshSessionButtons();
        }

        private static void ClosePanel()
        {
            if (_panel != null && _panel)
                _panel.SetActive(false);
            if (_menu != null && _menu.Menu0 != null)
                _menu.Menu0.SetActive(true);
        }

        private static void OnHostClicked()
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
            ModLog.Event(LogCat.Session,
                "Hosting LAN on port " + port
                + " — load/continue a save NOW. Clients on JOIN get the world only after you are in-chapter.");
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
            ModLog.Event(LogCat.Session,
                "Hosting Steam lobby — invite friends via overlay (SETTINGS shows lobby id). "
                + "Load/continue a save; clients join after you are in-chapter.");
            // Open invite once lobby id exists (async CreateLobby) — user can also press SETTINGS.
            ClosePanel();
            if (_menu != null)
                _menu.displayProfilesMenu();
        }

        private static void OnJoinSteamClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null || _joinPending)
                return;

            var lanReady = net as LanNetworkManager;
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetLabel(_joinButton, "CHOOSE SLOT");
                JoinWorldSlotPicker.EnsureExists();
                return;
            }
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingEnterWorld)
            {
                if (lanReady.WorldSaveShare.TryBeginEnterWorld())
                    SetLabel(_joinButton, "LOADING…");
                return;
            }

            if (net.Role != NetworkRole.Offline)
            {
                // Reuse LAN join mid-session world request path.
                OnJoinClicked();
                return;
            }

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
            SetLabel(_joinButton, "STEAM…");
            ModLog.Event(LogCat.Session, "Connecting Steam lobby " + lobby + " …");
        }

        private static void OnJoinClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null || _joinPending)
                return;

            // Mid-menu: package in RAM — user must pick permanent profile slot first.
            var lanReady = net as LanNetworkManager;
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetLabel(_joinButton, "CHOOSE SLOT");
                JoinWorldSlotPicker.EnsureExists();
                ModLog.Event(LogCat.Session,
                    "JOIN while awaiting slot pick — open permanent world copy picker (IMGUI)");
                return;
            }

            // Phase 1 done: permanent copy on disk — explicit ENTER WORLD starts offline load (phase 2).
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingEnterWorld)
            {
                if (lanReady.WorldSaveShare.TryBeginEnterWorld())
                {
                    SetLabel(_joinButton, "LOADING…");
                    ModLog.Event(LogCat.Session, "ENTER WORLD — starting offline load (phase 2)");
                }
                return;
            }

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete && Core.mainMenu)
            {
                var lan = net as LanNetworkManager;
                // Still downloading — JOIN pulls share (or no-ops if already in progress).
                if (lan?.WorldSaveShare != null && lan.WorldSaveShare.IsClientReceivingOrApplying)
                {
                    SetLabel(_joinButton, "DOWNLOADING…");
                    return;
                }
                if (lan != null && lan.RequestHostWorld("join-button"))
                {
                    SetLabel(_joinButton, "REQUESTING WORLD…");
                    ModLog.Event(LogCat.Session, "JOIN while connected — WorldRequest sent to host.");
                }
                else
                {
                    SetLabel(_joinButton, "WAITING…");
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
            SetLabel(_joinButton, "CONNECTING...");
            ModLog.Event(LogCat.Session, "Connecting to " + ip + ":" + port + " …");
        }

        private static void OnSettingsClicked()
        {
            MultiplayerMenu.EnsureExists();
            // Toggle: open if closed, close if open (writes fields on close).
            MultiplayerMenu.ShowSettings();
        }

        private static void OnRestoreClicked()
        {
            if (Player.Instance == null)
            {
                ModLog.Event(LogCat.Save, "Load into a game first before RESTORE SELF.");
                return;
            }
            var data = ClientStateBackup.LoadLocalSelfBackupFile();
            if (data == null)
            {
                ModLog.Event(LogCat.Save, "No local self-backup found.");
                return;
            }
            ClientStateBackup.RestoreFromBackup(data);
            ModLog.Event(LogCat.Save, "Applied local self-backup.");
        }

        private static void OnDisconnectClicked()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null)
                return;
            _joinPending = false;
            if (net.Role == NetworkRole.Host && net.TryGracefulHostLeave())
            {
                SetLabel(_joinButton, "JOIN GAME");
                RefreshSessionButtons();
                ModLog.Event(LogCat.Session, "Host disconnect — handing off to elect…");
                return;
            }
            net.StopNetwork();
            SetLabel(_joinButton, "JOIN GAME");
            RefreshSessionButtons();
            ModLog.Event(LogCat.Session, "Disconnected.");
        }

        // ------------------------------------------------------------------
        // Join / session feedback
        // ------------------------------------------------------------------

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
                SetLabel(_joinButton, "JOIN GAME");
                RefreshSessionButtons();
                return;
            }

            if (net.Role == NetworkRole.Offline
                || Time.realtimeSinceStartup - _joinStartedAt > JoinTimeoutSec)
            {
                bool wasTimeout = net.Role != NetworkRole.Offline;
                _joinPending = false;
                if (wasTimeout)
                    net.StopNetwork();
                SetLabel(_joinButton, "JOIN GAME");
                RefreshSessionButtons();
                ModLog.Event(LogCat.Session,
                    wasTimeout
                        ? "Join timeout — check IP/port/password in SETTINGS (and firewall)."
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

            // Slot pick / ENTER WORLD / download already active — do NOT auto WorldRequest
            // (that forced a second share + overwrite while the player was still choosing a slot).
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
                    SetLabel(_joinButton, "REQUESTING WORLD…");
            }
            else if (!receiving && waited >= 25f && !_worldRequest25sSent)
            {
                _worldRequest25sSent = true;
                if (net.RequestHostWorld("title-wait-25s"))
                    SetLabel(_joinButton, "REQUESTING WORLD…");
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

        private static void UpdateJoinLabelFromShare(LanNetworkManager net)
        {
            if (net == null)
                return;
            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetLabel(_joinButton, "CHOOSE SLOT");
                return;
            }
            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingEnterWorld)
            {
                SetLabel(_joinButton, "ENTER WORLD");
                return;
            }
            string prog = net.WorldSaveShare != null ? net.WorldSaveShare.ProgressText : null;
            if (!string.IsNullOrEmpty(prog))
            {
                if (prog.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "SHARE FAIL");
                else if (prog.IndexOf("ENTER WORLD", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Permanent copy", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("World ready", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "ENTER WORLD");
                else if (prog.IndexOf("Pick a profile", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("permanent", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "CHOOSE SLOT");
                else if (prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Writ", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Inflat", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Verif", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "DOWNLOADING…");
                else if (prog.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0
                         || prog.IndexOf("Appl", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "LOADING…");
                else if (prog.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "REQUESTING WORLD…");
                else
                    SetLabel(_joinButton, "CONNECTED");
            }
            else if (net.IsHandshakeComplete)
            {
                SetLabel(_joinButton, "CONNECTED");
            }
        }

        private static void RefreshSessionButtons()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            bool online = net != null && net.Role != NetworkRole.Offline;

            if (_disconnectButton != null && _disconnectButton)
                _disconnectButton.SetActive(online);

            if (_joinPending)
                return;
            if (_joinButton == null || !_joinButton || net == null)
                return;

            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingSlotPick)
                SetLabel(_joinButton, "CHOOSE SLOT");
            else if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingEnterWorld)
                SetLabel(_joinButton, "ENTER WORLD");
            else if (net.Role == NetworkRole.Client && net.IsHandshakeComplete)
                UpdateJoinLabelFromShare(net);
            else if (net.Role == NetworkRole.Host)
                SetLabel(_joinButton, "HOSTING");
            else
                SetLabel(_joinButton, "JOIN GAME");
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
        }

        /// <summary>Marks our UI roots so purge never touches vanilla menu buttons.</summary>
        private sealed class YokWareUiTag : MonoBehaviour
        {
            public string Kind;
        }
    }
}
