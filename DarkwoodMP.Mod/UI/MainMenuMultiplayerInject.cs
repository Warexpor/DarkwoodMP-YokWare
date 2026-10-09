using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Native tk2d title MULTIPLAYER button. Host/Join doors lead to LAN or Steam.
    /// Presentation: clone quitBtn → strip LocalizedText/sprites → tk2dTextMesh from CurrentVersion.
    /// </summary>
    public static partial class MainMenuMultiplayerInject
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
        /// <summary>HOST/JOIN panel rows use tighter spacing than the title.</summary>
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

        private static PanelView _panelView = PanelView.Root; // process-scoped: menu UI state
        private static bool _joinViaSteam; // process-scoped: menu UI state
        private static bool _hostingHint; // process-scoped: menu UI state

        private static bool _joinPending; // reset-in: ClearJoinState
        private static float _joinStartedAt; // process-scoped: read only while _joinPending
        private static float _handshakeAt; // reset-in: ClearJoinState
        private static bool _loggedWaitingWorld; // reset-in: ClearJoinState
        private static bool _worldRequest10sSent; // reset-in: ClearJoinState
        private static bool _worldRequest25sSent; // reset-in: ClearJoinState
        private static int _lastUiPoll; // process-scoped: menu UI state

        private static int _boundMenu0Id; // process-scoped: menu UI state
        private static int _lastScreenW; // process-scoped: menu UI state
        private static int _lastScreenH; // process-scoped: menu UI state
        private static bool _menu0WasActive; // process-scoped: menu UI state
        private static bool _launchLobbyTried; // process-scoped: launch-lobby connect is tried once per process

        private static GameObject ActiveJoinButton =>
            _joinViaSteam ? _joinSteamBtn : _joinLanBtn;

        public static void OnUpdate()
        {
            try
            {
                TryConsumeSteamLaunchLobby();
                TrackExternalJoin();
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
                RelabelOnLanguageChange();
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session, "menu relabel: " + ex.Message, ex);
            }

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
                ResetTitleStack();
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
                ApplyTitleStack();
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


        private sealed class YokWareUiTag : MonoBehaviour
        {
            public string Kind;
            /// <summary>The label's English text; shown through <see cref="Loc.T"/>, again on a language change.</summary>
            public string LabelEn;
        }

        private static string _labelLanguage; // process-scoped: language the panel labels were last written in

        /// <summary>The game's language changed in Options: write every mod label again in it.</summary>
        private static void RelabelOnLanguageChange()
        {
            if (_labelLanguage == Loc.Language)
                return;
            _labelLanguage = Loc.Language;
            var roots = new List<Transform>(2);
            if (_mpButton != null && _mpButton) roots.Add(_mpButton.transform);
            if (_panel != null && _panel) roots.Add(_panel.transform);
            for (int r = 0; r < roots.Count; r++)
            {
                YokWareUiTag[] tags = roots[r].GetComponentsInChildren<YokWareUiTag>(true);
                for (int i = 0; i < tags.Length; i++)
                {
                    if (tags[i] != null && !string.IsNullOrEmpty(tags[i].LabelEn))
                        SetLabel(tags[i].gameObject, tags[i].LabelEn);
                }
            }
        }

    }
}
