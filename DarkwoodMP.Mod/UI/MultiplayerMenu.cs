using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    public sealed class MultiplayerMenu : MonoBehaviour
    {
        private static MultiplayerMenu _instance;

        private bool _visible;
        private bool _advancedOpen;
        private string _connectAddress = "127.0.0.1";
        private string _portText = PluginInfo.DefaultPort.ToString();
        private string _passwordText = "";
        private string _steamLobbyText = "";
        private string _nameText = "";
        // A field is written back to the config only after the user edited it (never every
        // IMGUI pass: the network writes SteamLobbyId itself and each Save hits the disk).
        private bool _dirtyAddress;
        private bool _dirtyPort;
        private bool _dirtyPassword;
        private bool _dirtyLobby;
        private bool _dirtyName;
        private string _lastFocusedControl = "";
        private const string CtlAddress = "dwmp_addr";
        private const string CtlPort = "dwmp_port";
        private const string CtlPassword = "dwmp_pass";
        private const string CtlLobby = "dwmp_lobby";
        private const string CtlName = "dwmp_name";
        private const string LockOwner = "f2";
        private string _hostNextStepHint = "";
        private string _restoreSelfStatus = "";
        private float _restoreSelfStatusUntil;
        private ClientStateBackupData _peekBackup;
        private float _peekBackupAt;
        private Rect _windowRect;
        private Vector2 _scroll;
        private bool _windowRectInitialized;

        private LanNetworkManager Network => ModRuntime.Network;

        private static float UiScale => Mathf.Clamp(Screen.height / 900f, 1f, 2f);

        public static void ToggleVisible()
        {
            if (_instance == null) return;
            if (_instance._visible)
            {
                _instance.Close();
                return;
            }
            _instance._visible = true;
            _instance.PullFieldsFromConfig();
        }

        /// <summary>Toggle IMGUI settings (IP/port/password) for main-menu SETTINGS open/close.</summary>
        public static void ShowSettings()
        {
            if (_instance == null) return;
            if (_instance._visible)
            {
                _instance.Close();
                return;
            }
            _instance._visible = true;
            _instance.PullFieldsFromConfig();
        }

        private void Close()
        {
            WriteFieldsToConfig();
            _visible = false;
        }

        /// <summary>Commit any edited fields (HOST/JOIN buttons call this before reading the config).</summary>
        public static void PushFieldsToConfig()
        {
            if (_instance == null) return;
            _instance.WriteFieldsToConfig();
        }

        public static void SetHostNextStepHint(string hint)
        {
            if (_instance == null)
                EnsureExists();
            if (_instance != null)
                _instance._hostNextStepHint = hint ?? "";
        }

        public static void ClearHostNextStepHint()
        {
            if (_instance != null)
                _instance._hostNextStepHint = "";
        }

        public static void EnsureExists()
        {
            if (_instance != null)
                return;

            GameObject go = new GameObject("DWMPHorde_Menu");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<MultiplayerMenu>();
            _instance.ResetWindowRect();
            _instance.PullFieldsFromConfig();
        }

        private void PullFieldsFromConfig()
        {
            if (ModConfig.ConnectAddress != null)
                _connectAddress = ModConfig.ConnectAddress.Value ?? "127.0.0.1";
            if (ModConfig.ConnectPort != null)
                _portText = ModConfig.GetConnectPort().ToString();
            if (ModConfig.HostPassword != null)
                _passwordText = ModConfig.HostPassword.Value ?? "";
            if (ModConfig.PlayerName != null)
                _nameText = ModConfig.PlayerName.Value ?? "Player";
            _steamLobbyText = ResolveLobbyText();
            _dirtyAddress = _dirtyPort = _dirtyPassword = _dirtyLobby = _dirtyName = false;
        }

        /// <summary>Live lobby id while hosting/joining via Steam, else the configured one.</summary>
        private string ResolveLobbyText()
        {
            if (Network != null && Network.IsSteamSession && !string.IsNullOrEmpty(Network.SteamLobbyIdText))
                return Network.SteamLobbyIdText;
            return ModConfig.SteamLobbyId != null ? (ModConfig.SteamLobbyId.Value ?? "") : "";
        }

        /// <summary>
        /// Write back only the fields the user edited. Port is clamped to 1-65535 (invalid text
        /// reverts to the configured port).
        /// </summary>
        private void WriteFieldsToConfig()
        {
            if (_dirtyAddress && ModConfig.ConnectAddress != null && _connectAddress != null)
                ModConfig.ConnectAddress.Value = _connectAddress.Trim();
            if (_dirtyPort && ModConfig.ConnectPort != null)
            {
                if (int.TryParse(_portText, out int p))
                    ModConfig.ConnectPort.Value = Mathf.Clamp(p, ModConfig.MinPort, ModConfig.MaxPort);
                _portText = ModConfig.GetConnectPort().ToString();
            }
            if (_dirtyPassword && ModConfig.HostPassword != null && _passwordText != null)
                ModConfig.HostPassword.Value = _passwordText;
            if (_dirtyLobby && ModConfig.SteamLobbyId != null && _steamLobbyText != null)
                ModConfig.SteamLobbyId.Value = _steamLobbyText.Trim();
            if (_dirtyName && ModConfig.PlayerName != null && _nameText != null)
                ModConfig.PlayerName.Value = _nameText.Trim();
            _dirtyAddress = _dirtyPort = _dirtyPassword = _dirtyLobby = _dirtyName = false;
        }

        private void Update()
        {
            MainMenuMultiplayerInject.OnUpdate();

            // In-game only: the title screen has no gameplay input to hold back.
            bool lockInput = _visible && !Core.mainMenu;
            UiInputLock.Set(LockOwner, lockInput);
            if (lockInput && Input.GetKeyDown(KeyCode.Escape))
                Close();
        }

        private void ResetWindowRect()
        {
            float scale = UiScale;
            float width = Mathf.Clamp(480f * scale, 400f, Screen.width * 0.55f);
            float height = Mathf.Clamp(420f * scale, 340f, Screen.height * 0.55f);
            _windowRect = new Rect(24f, 24f, width, height);
            _windowRectInitialized = true;
        }

        private void OnGUI()
        {
            if (!_windowRectInitialized)
                ResetWindowRect();

            if (!_visible)
                return;

            Matrix4x4 oldMatrix = GUI.matrix;
            float scaleGui = UiScale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scaleGui, scaleGui, 1f));

            Rect scaledRect = new Rect(
                _windowRect.x / scaleGui,
                _windowRect.y / scaleGui,
                _windowRect.width / scaleGui,
                _windowRect.height / scaleGui);

            scaledRect = GUI.Window(987654, scaledRect, DrawWindow, PluginInfo.DisplayVersion);

            _windowRect = new Rect(
                scaledRect.x * scaleGui,
                scaledRect.y * scaleGui,
                scaledRect.width * scaleGui,
                scaledRect.height * scaleGui);

            GUI.matrix = oldMatrix;
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("Status: " + (Network != null ? Network.StatusText : "No network"), GUILayout.ExpandWidth(true));
            if (Network != null && Network.WorldSaveShare != null
                && !string.IsNullOrEmpty(Network.WorldSaveShare.ProgressText))
                GUILayout.Label(Network.WorldSaveShare.ProgressText, GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "Join: host must be in-chapter → world share → pick slot → ENTER WORLD.",
                GUILayout.ExpandWidth(true));

            // Focus left a field: that is the commit point for the text it held.
            string focused = GUI.GetNameOfFocusedControl();
            if (focused != _lastFocusedControl)
            {
                _lastFocusedControl = focused;
                WriteFieldsToConfig();
            }
            // Pick up a lobby id the network wrote (host created a lobby) unless the user is editing it.
            if (!_dirtyLobby && focused != CtlLobby)
                _steamLobbyText = ResolveLobbyText();

            GUILayout.Space(8f);
            GUILayout.Label("Host IP:", GUILayout.ExpandWidth(true));
            _connectAddress = Field(CtlAddress, _connectAddress, ref _dirtyAddress);

            GUILayout.Space(4f);
            GUILayout.Label("Port (1-65535):", GUILayout.ExpandWidth(true));
            _portText = Field(CtlPort, _portText, ref _dirtyPort);

            GUILayout.Space(4f);
            GUILayout.Label("Password (optional):", GUILayout.ExpandWidth(true));
            _passwordText = Field(CtlPassword, _passwordText ?? "", ref _dirtyPassword);

            GUILayout.Space(4f);
            GUILayout.Label("Steam lobby id:", GUILayout.ExpandWidth(true));
            _steamLobbyText = Field(CtlLobby, _steamLobbyText ?? "", ref _dirtyLobby);

            GUILayout.Space(4f);
            GUILayout.Label("Chat name:", GUILayout.ExpandWidth(true));
            _nameText = Field(CtlName, _nameText ?? "Player", ref _dirtyName);

            bool anyDirty = _dirtyAddress || _dirtyPort || _dirtyPassword || _dirtyLobby || _dirtyName;
            GUI.enabled = anyDirty;
            if (GUILayout.Button(anyDirty ? "Apply" : "Saved", GUILayout.Height(24f)))
                WriteFieldsToConfig();
            GUI.enabled = true;

            GUILayout.Space(10f);
            if (GUILayout.Button(_advancedOpen ? "Advanced ▾" : "Advanced ▸", GUILayout.Height(26f)))
                _advancedOpen = !_advancedOpen;

            if (_advancedOpen)
            {
                GUILayout.Space(4f);
                if (Network != null && Network.IsSteamSession && Network.Role == NetworkRole.Host
                    && !string.IsNullOrEmpty(Network.SteamLobbyIdText))
                {
                    GUILayout.Label("Lobby: " + Network.SteamLobbyIdText, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Copy lobby id + open invite", GUILayout.Height(28f)))
                    {
                        Networking.Steam.SteamCoopTransport.CopyToClipboard(Network.SteamLobbyIdText);
                        Network.InviteSteamFriends();
                    }
                }

                if (Network != null && Network.Role == NetworkRole.Host)
                {
                    bool shareBusy = Network.WorldSaveShare != null && Network.WorldSaveShare.IsBusy;
                    GUI.enabled = Network.IsConnected && Network.IsHandshakeComplete && !shareBusy;
                    if (GUILayout.Button(shareBusy ? "Resending world…" : "Resend world to clients", GUILayout.Height(28f)))
                        Network.WorldSaveShare?.ScheduleHostResend();
                    GUI.enabled = true;
                }

                if (Network != null && Network.Role != NetworkRole.Offline)
                {
                    if (GUILayout.Button("Disconnect", GUILayout.Height(28f)))
                    {
                        ClearHostNextStepHint();
                        if (Network.Role == NetworkRole.Host && Network.TryGracefulHostLeave())
                        { /* handoff */ }
                        else
                            Network.StopNetwork();
                    }
                }

                DrawRestoreSelfSection();
            }

            GUILayout.Space(10f);
            GUILayout.Label(
                PluginInfo.DisplayVersion + "  proto=" + PluginInfo.ProtocolVersion
                + "  |  F2=settings F3=save  |  " + PluginInfo.Guid + ".cfg",
                GUILayout.ExpandWidth(true));

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private static string Field(string controlName, string value, ref bool dirty)
        {
            GUI.SetNextControlName(controlName);
            string edited = GUILayout.TextField(value, GUILayout.ExpandWidth(true));
            if (!string.Equals(edited, value))
                dirty = true;
            return edited;
        }

        /// <summary>
        /// Client personal inv/skills/pos live in ClientStateBackup (host world share
        /// loads the host body). Auto-restore usually runs on join/load; this is the
        /// manual recovery path when that misses.
        /// </summary>
        private void DrawRestoreSelfSection()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Client self-backup (inv / skills / exit pos):", GUILayout.ExpandWidth(true));

            bool canRestore = TryGetRestoreSelfGate(out string reason);
            var peek = PeekLocalSelfBackup();
            if (peek != null)
            {
                GUILayout.Label(
                    "On disk: day≈" + peek.Day
                    + " lvl=" + peek.CurrentLevel
                    + " inv=" + (peek.InventoryItems?.Count ?? 0)
                    + " skills=" + (peek.Skills?.Count ?? 0)
                    + (string.IsNullOrEmpty(peek.Timestamp) ? "" : " @ " + peek.Timestamp),
                    GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label("On disk: none for this campaign.", GUILayout.ExpandWidth(true));
            }

            GUI.enabled = canRestore;
            if (GUILayout.Button("Restore self now", GUILayout.Height(28f)))
                TryRestoreSelf();
            GUI.enabled = true;

            if (!canRestore && !string.IsNullOrEmpty(reason))
                GUILayout.Label(reason, GUILayout.ExpandWidth(true));

            if (!string.IsNullOrEmpty(_restoreSelfStatus) && Time.realtimeSinceStartup < _restoreSelfStatusUntil)
            {
                GUI.color = Color.yellow;
                GUILayout.Label(_restoreSelfStatus, GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
            }
        }

        private bool TryGetRestoreSelfGate(out string reason)
        {
            reason = null;
            if (Core.mainMenu || Core.loadingGame || Player.Instance == null)
            {
                reason = "Need to be in-chapter (not title). Auto-restore runs on join.";
                return false;
            }

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.Role == NetworkRole.Host)
            {
                reason = "Host uses sav.dat — restore self is client-only (would overwrite host).";
                return false;
            }

            var data = PeekLocalSelfBackup();
            if (data == null)
            {
                reason = "No usable self-backup for this campaign yet (save / disconnect as client first).";
                return false;
            }

            return true;
        }

        private ClientStateBackupData PeekLocalSelfBackup()
        {
            // OnGUI can fire many times per frame; do not re-read or log on every paint.
            if (Time.realtimeSinceStartup - _peekBackupAt < 1.0f)
                return _peekBackup;
            _peekBackupAt = Time.realtimeSinceStartup;
            _peekBackup = ClientStateBackup.LoadLocalSelfBackupFile();
            return _peekBackup;
        }

        private void SetRestoreSelfStatus(string msg)
        {
            _restoreSelfStatus = msg ?? "";
            _restoreSelfStatusUntil = Time.realtimeSinceStartup + 6f;
        }

        private void TryRestoreSelf()
        {
            if (!TryGetRestoreSelfGate(out string reason))
            {
                SetRestoreSelfStatus(reason ?? "Restore blocked.");
                ModLog.Event(LogCat.Save, "RESTORE SELF blocked: " + (reason ?? "?"));
                return;
            }

            // Fresh read on click (bypass 1s peek cache).
            _peekBackupAt = 0f;
            var data = ClientStateBackup.LoadLocalSelfBackupFile();
            if (data == null)
            {
                SetRestoreSelfStatus("No local self-backup found.");
                ModLog.Event(LogCat.Save, "No local self-backup found.");
                return;
            }

            // Snapshot before so the log shows we actually ran.
            float beforeHp = Player.Instance.health;
            int beforeLvl = Player.Instance.currentLevel;

            if (!ClientStateBackup.RestoreFromBackup(data))
            {
                SetRestoreSelfStatus(WrongSaveWarning.Format(
                    "campaign mismatch, empty, or stale backup — restore refused"));
                return;
            }

            SetRestoreSelfStatus(
                "Restored self — lvl=" + data.CurrentLevel
                + " inv=" + (data.InventoryItems?.Count ?? 0)
                + " skills=" + (data.Skills?.Count ?? 0)
                + " pos=(" + data.PosX.ToString("F0") + "," + data.PosZ.ToString("F0") + ")");
            ModLog.Event(LogCat.Save,
                "Applied local self-backup (before lvl=" + beforeLvl
                + " hp=" + beforeHp.ToString("F0") + ").");
        }
    }
}
