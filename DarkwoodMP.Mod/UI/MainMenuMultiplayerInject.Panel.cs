using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    public static partial class MainMenuMultiplayerInject
    {
        private static void InjectMultiplayerButton()
        {
            GameObject template = _menu.quitBtn;
            if (template == null || template.GetComponent<Button>() == null)
                return;

            // Title door: pixel art from the vanilla menu glyphs (matches PLAY/OPTIONS). Fallback = text label.
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
            // Second row of the title stack, right under PLAY; the rows below move down for it.
            PositionMe play = _menu?.playBtn != null ? _menu.playBtn.GetComponent<PositionMe>() : null;
            return (play != null ? play.offset.y : 60f) - RowSpacing;
        }

        /// <summary>Title rows shifted down for MULTIPLAYER, with their vanilla offsets.</summary>
        private static readonly List<KeyValuePair<PositionMe, Vector2>> _titleShifted = new List<KeyValuePair<PositionMe, Vector2>>(8); // reset-in: ResetTitleStack
        private static MainMenu _titleShiftedMenu; // reset-in: ResetTitleStack

        /// <summary>
        /// Title screen: OPTIONS, CREDITS, EXIT and the version / player-id labels sit one row
        /// lower than vanilla so MULTIPLAYER takes the row under PLAY. The shift is taken from the
        /// vanilla offsets (vanilla resets EXIT's on every menu open) and checked every UI poll.
        /// Pause-menu-only rows (RESUME, HELP, MAIN MENU) and the logo are left alone.
        /// </summary>
        private static void ApplyTitleStack()
        {
            if (_menu == null || _menu.Menu0 == null || _menu.playBtn == null)
                return;
            PositionMe play = _menu.playBtn.GetComponent<PositionMe>();
            if (play == null)
                return;
            if (_titleShiftedMenu != _menu)
            {
                ResetTitleStack();
                _titleShiftedMenu = _menu;
                PositionMe[] pms = _menu.Menu0.GetComponentsInChildren<PositionMe>(true);
                for (int i = 0; i < pms.Length; i++)
                {
                    PositionMe pm = pms[i];
                    if (pm == null || pm == play || pm.offset.y >= play.offset.y || IsPauseOnlyRow(pm.gameObject))
                        continue;
                    if (pm.GetComponent<YokWareUiTag>() != null || pm.gameObject.name.StartsWith("YokWare_", StringComparison.Ordinal))
                        continue;
                    _titleShifted.Add(new KeyValuePair<PositionMe, Vector2>(pm, pm.offset));
                }
            }
            for (int i = 0; i < _titleShifted.Count; i++)
            {
                PositionMe pm = _titleShifted[i].Key;
                if (pm == null)
                    continue;
                Vector2 want = _titleShifted[i].Value - new Vector2(0f, RowSpacing);
                if (pm.offset == want)
                    continue;
                pm.offset = want;
                pm.init();
            }
        }

        /// <summary>Put the shifted title rows back at their vanilla offsets (leaving the title).</summary>
        private static void ResetTitleStack()
        {
            for (int i = 0; i < _titleShifted.Count; i++)
            {
                PositionMe pm = _titleShifted[i].Key;
                if (pm == null || !pm)
                    continue;
                pm.offset = _titleShifted[i].Value;
                pm.init();
            }
            _titleShifted.Clear();
            _titleShiftedMenu = null;
        }

        private static bool IsPauseOnlyRow(GameObject go)
        {
            return go == _menu.mainMenuBtn
                || (_menu.ResumeBtn != null && go == _menu.ResumeBtn.gameObject)
                || (_menu.HelpBtn != null && go == _menu.HelpBtn.gameObject);
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

            var net = ModRuntime.Network;
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

        private static void OnSettingsClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.ShowSettings();
        }

        private static void OnDisconnectClicked()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return;
            _joinPending = false;
            _hostingHint = false;
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

        private static void RefreshSessionButtons()
        {
            ExpireFailureLabel();

            var net = ModRuntime.Network;
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

            // A HOST/JOIN failure label owns its button until it expires (see ExpireFailureLabel).
            if (FailureLabelActive)
                return;

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
            YokWareUiTag tag = buttonGo.GetComponent<YokWareUiTag>();
            if (tag != null)
                tag.LabelEn = text;
            tk2dTextMesh tm = buttonGo.GetComponentInChildren<tk2dTextMesh>(true);
            if (tm == null)
                return;
            string shown = Loc.T(text);
            if (tm.text == shown)
                return;
            if (tm.maxChars < shown.Length + 4)
                tm.maxChars = shown.Length + 8;
            tm.text = shown;
            tm.Commit();
            FitButtonHitbox(buttonGo);
        }

    }
}
