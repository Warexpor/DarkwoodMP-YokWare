using System;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Native tk2d title-screen MULTIPLAYER button (Yokyy product path, Horde network).
    ///
    /// Construction is deliberately the proven Yokyy approach:
    /// clone quitBtn → OnFire + empty function → hide sprite → tk2dTextMesh label
    /// sized against the button collider in world space → PositionMe.offset + init().
    /// PositionMe already tracks resolution; do not reinvent that.
    /// </summary>
    public static class MainMenuMultiplayerInject
    {
        private const float RowSpacing = 60f;
        private const int PollInterval = 15;
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
        private static int _lastPoll;

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

            if (Time.frameCount - _lastPoll < PollInterval)
                return;
            _lastPoll = Time.frameCount;

            try
            {
                TickMenu();
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session, "MainMenuMultiplayerInject: " + ex.Message, ex);
            }
        }

        private static void TickMenu()
        {
            if (!Core.mainMenu)
            {
                // Scene objects die on load; drop stale refs (keep join pending across load).
                _mpButton = null;
                _panel = null;
                _joinButton = null;
                _disconnectButton = null;
                _menu = null;
                return;
            }

            if (_menu == null)
                _menu = UnityEngine.Object.FindObjectOfType(typeof(MainMenu)) as MainMenu;
            if (_menu == null || _menu.Menu0 == null)
                return;

            if (_mpButton == null && _menu.Menu0.activeInHierarchy)
                Inject();

            // Game re-showed Menu0 (ESC) while our panel was open — yield
            if (_panel != null && _panel.activeSelf && _menu.Menu0.activeSelf)
                _panel.SetActive(false);

            if (_panel != null && _panel.activeSelf)
                RefreshSessionButtons();
        }

        // ------------------------------------------------------------------
        // Construction (Yokyy-proven — leave this path alone)
        // ------------------------------------------------------------------

        private static void Inject()
        {
            var template = _menu.quitBtn;
            if (template == null || template.GetComponent<Button>() == null)
                return;

            // Destroy any half-broken orphan from older inject attempts
            var orphan = _menu.Menu0.transform.Find("YokWare_MultiplayerBtn");
            if (orphan != null)
                UnityEngine.Object.Destroy(orphan.gameObject);

            _mpButton = CloneButton(template, template.transform.parent,
                "YokWare_MultiplayerBtn", "MULTIPLAYER", OpenPanel);

            // One row below the lowest Menu0 button (same as Yokyy).
            // PositionMe.offset is resolution-independent; init() applies scale.
            float lowest = 0f;
            foreach (var pm in _menu.Menu0.GetComponentsInChildren<PositionMe>(false))
            {
                if (pm == null || pm.gameObject == _mpButton)
                    continue;
                if (pm.offset.y < lowest)
                    lowest = pm.offset.y;
            }
            SetRow(_mpButton, lowest - RowSpacing);

            ModLog.Event(LogCat.Session,
                "Injected MULTIPLAYER button @ " + Screen.width + "x" + Screen.height
                + " offsetY=" + (lowest - RowSpacing).ToString("F1"));
        }

        private static void BuildPanel()
        {
            if (_panel != null)
            {
                UnityEngine.Object.Destroy(_panel);
                _panel = null;
            }

            var template = _menu.quitBtn;
            if (template == null)
                return;

            _panel = new GameObject("YokWare_MenuPanel");
            // Sibling of Menu0 so toggling Menu0 does not hide us
            _panel.transform.SetParent(_menu.Menu0.transform.parent, false);

            var host = CloneButton(template, _panel.transform, "YokWare_HostBtn", "HOST GAME", OnHostClicked);
            _joinButton = CloneButton(template, _panel.transform, "YokWare_JoinBtn", "JOIN GAME", OnJoinClicked);
            var settings = CloneButton(template, _panel.transform, "YokWare_SettingsBtn", "SETTINGS", OnSettingsClicked);
            var restore = CloneButton(template, _panel.transform, "YokWare_RestoreBtn", "RESTORE SELF", OnRestoreClicked);
            _disconnectButton = CloneButton(template, _panel.transform, "YokWare_DiscBtn", "DISCONNECT", OnDisconnectClicked);
            var back = CloneButton(template, _panel.transform, "YokWare_BackBtn", "BACK", ClosePanel);

            // Order: CloneButton lays out label at current world pose, then SetRow moves
            // the parent (label is a child — keeps correct local offset). Yokyy order.
            SetRow(host, 0f);
            SetRow(_joinButton, -RowSpacing);
            SetRow(settings, -RowSpacing * 2f);
            SetRow(restore, -RowSpacing * 3f);
            SetRow(_disconnectButton, -RowSpacing * 4f);
            SetRow(back, -RowSpacing * 5f);

            RefreshSessionButtons();
        }

        private static GameObject CloneButton(GameObject template, Transform parent,
            string name, string label, Action onFire)
        {
            var go = UnityEngine.Object.Instantiate(template, parent);
            go.name = name;
            go.SetActive(true);

            // Title buttons are texture/sprite words (LocalizedText), not editable text.
            // DestroyImmediate so deferred Destroy cannot re-stamp the EXIT sprite.
            foreach (var loc in go.GetComponentsInChildren<LocalizedText>(true))
                UnityEngine.Object.DestroyImmediate(loc);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;

            var btn = go.GetComponent<Button>();
            btn.function = "";
            btn.popupType = "";
            btn.localized = false;
            btn.sprite = null;
            btn.OnFire = () => Guarded(onFire);

            var tm = CreateLabel(go.transform, label);
            if (tm != null)
            {
                btn.textMesh = tm;
                btn.baseColor = tm.color;
            }
            return go;
        }

        /// <summary>
        /// Label = clone of MainMenu.CurrentVersion (game font). Strip PositionMe so it
        /// does not re-anchor to the version corner. Size/place against the button collider
        /// in world space (Yokyy); as a child it then rides PositionMe moves.
        /// </summary>
        private static tk2dTextMesh CreateLabel(Transform parent, string text)
        {
            var source = _menu != null ? _menu.CurrentVersion : null;
            if (source == null)
            {
                ModLog.Warn(LogCat.Session, "MainMenu.CurrentVersion missing — button label blank");
                return null;
            }

            var labelGo = UnityEngine.Object.Instantiate(source.gameObject, parent);
            labelGo.name = "YokWare_Label";

            foreach (var mb in labelGo.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is tk2dTextMesh)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(mb); }
                catch { /* ignore */ }
            }

            // Colliders on the version string would steal clicks from the button root.
            foreach (var c in labelGo.GetComponentsInChildren<Collider>(true))
            {
                try { UnityEngine.Object.DestroyImmediate(c); }
                catch { /* ignore */ }
            }

            labelGo.transform.localPosition = Vector3.zero;
            labelGo.transform.localRotation = Quaternion.identity;
            labelGo.transform.localScale = source.transform.localScale;
            labelGo.SetActive(true);

            var tm = labelGo.GetComponent<tk2dTextMesh>();
            if (tm == null)
                return null;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.text = text;
            tm.Commit();

            var col = parent.GetComponent<Collider>();
            var rend = labelGo.GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = true;

            if (col != null && rend != null
                && rend.bounds.size.y > 0.001f && rend.bounds.size.x > 0.001f)
            {
                float fitH = col.bounds.size.y * 0.65f / rend.bounds.size.y;
                float fitW = col.bounds.size.x * 1.05f / rend.bounds.size.x;
                float factor = Mathf.Clamp(Mathf.Min(fitH, fitW), 0.02f, 50f);
                labelGo.transform.localScale *= factor;

                var pos = col.bounds.center;
                var camObj = Core.CamUI;
                var cam = camObj != null ? camObj.GetComponent<Camera>() : null;
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
            var pm = go.GetComponent<PositionMe>();
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
            if (_panel == null)
                BuildPanel();
            if (_panel == null || _menu == null)
                return;
            _menu.Menu0.SetActive(false);
            _panel.SetActive(true);
            RefreshSessionButtons();
        }

        private static void ClosePanel()
        {
            if (_panel != null)
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
                "Hosting on port " + port
                + " — load/continue a save NOW. Clients on JOIN get the world only after you are in-chapter.");
            ClosePanel();
            if (_menu != null)
                _menu.displayProfilesMenu();
        }

        private static void OnJoinClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null || _joinPending)
                return;

            // Already connected on title and still waiting for share — explicit pull
            // (Yokyy RequestWorld UX: press JOIN again).
            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete && Core.mainMenu)
            {
                var lan = net as LanNetworkManager;
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
            var net = ModRuntime.Network;
            if (net == null)
                return;
            _joinPending = false;
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

            float waited = Time.realtimeSinceStartup - _handshakeAt;
            bool receiving = IsShareProgressActive(net);

            if (!_loggedWaitingWorld && waited > 8f && !receiving)
            {
                _loggedWaitingWorld = true;
                ModLog.Warn(LogCat.Session,
                    "Still on title 8s after handshake with no world download. "
                    + "Host must be IN the chapter (not title). Auto WorldRequest at 10s; or press JOIN again / host F2 Resend.");
            }

            // Yokyy RequestWorld equivalent: pull if host push was missed.
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
            string prog = net.WorldSaveShare.ProgressText ?? "";
            if (string.IsNullOrEmpty(prog))
                return false;
            return prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Appl", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void UpdateJoinLabelFromShare(LanNetworkManager net)
        {
            if (net == null)
                return;
            string prog = net.WorldSaveShare != null ? net.WorldSaveShare.ProgressText : null;
            if (!string.IsNullOrEmpty(prog))
            {
                if (prog.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetLabel(_joinButton, "SHARE FAIL");
                else if (prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0)
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

            if (_disconnectButton != null)
                _disconnectButton.SetActive(online);

            if (_joinPending)
                return;
            if (_joinButton == null || net == null)
                return;

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete)
                UpdateJoinLabelFromShare(net);
            else if (net.Role == NetworkRole.Host)
                SetLabel(_joinButton, "HOSTING");
            else
                SetLabel(_joinButton, "JOIN GAME");
        }

        private static void SetLabel(GameObject buttonGo, string text)
        {
            if (buttonGo == null)
                return;
            var tm = buttonGo.GetComponentInChildren<tk2dTextMesh>(true);
            if (tm == null)
                return;
            if (tm.text == text)
                return;
            tm.text = text;
            tm.Commit();
        }
    }
}
