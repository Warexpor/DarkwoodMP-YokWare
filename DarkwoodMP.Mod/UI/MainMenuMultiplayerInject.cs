using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using UnityEngine;
using YokWare.VanillaMenu;

namespace DWMPHorde
{
    /// <summary>
    /// The MULTIPLAYER entry of the title and pause menus (pixel-art button in the vanilla stack)
    /// and the multiplayer screens behind it (<see cref="MultiplayerScreens"/>), plus the join flow
    /// that runs while the player waits on the title screen.
    /// </summary>
    public static partial class MainMenuMultiplayerInject
    {
        private const string MpButtonName = "YokWare_MultiplayerBtn";
        private const string LabelName = "YokWare_Label";
        private const string TagKindMp = "mp";

        private const float RowSpacing = 60f;
        private const int UiPollInterval = 15;
        private const float JoinTimeoutSec = 15f;
        private const float SteamJoinTimeoutSec = 35f;

        private static MainMenu _menu;
        private static GameObject _mpButton;

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
        private static bool _wasAtTitle; // process-scoped: menu UI state

        public static void OnUpdate()
        {
            // The mod's text follows the game's language (Options > Language), checked every frame.
            Loc.SetLanguage(GameSettings.GetString("LanguageCode"));
            try
            {
                TryConsumeSteamLaunchLobby();
                TrackExternalJoin();
                if (_joinPending)
                    PollJoinState();
                else
                    PollPostHandshakeWorldWait();
                MultiplayerScreens.AutoOpen();
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
                ResetMenuStack();
                SoftClearMenuCache();
                _menu0WasActive = false;
                return;
            }

            if (!ResolveMenu())
                return;

            bool menu0Active = _menu.Menu0 != null && _menu.Menu0.activeInHierarchy;
            bool atTitle = GameScreen.AtTitle;
            if (atTitle != _wasAtTitle)
            {
                // Title ↔ pause menu: the stack and the button row are different.
                _wasAtTitle = atTitle;
                ResetMenuStack();
            }

            if (menu0Active)
            {
                ApplyMenuStack();
                bool becameActive = !_menu0WasActive;
                int menu0Id = _menu.Menu0.GetInstanceID();
                bool menuRebuilt = menu0Id != _boundMenu0Id;
                bool resChanged = Screen.width != _lastScreenW || Screen.height != _lastScreenH;

                if (becameActive || menuRebuilt || !IsOwnedInteractive(_mpButton, TagKindMp))
                    EnsureMultiplayerButton(forceRebuild: menuRebuilt || !IsOwnedInteractive(_mpButton, TagKindMp));
                else if (resChanged)
                    RelayoutMultiplayerButton();
                else
                    SetRow(_mpButton, MultiplayerRowOffsetY());

                SyncScaleToTemplate();
                _menu0WasActive = true;
            }
            else
            {
                _menu0WasActive = false;
            }
        }

        private static void SoftClearMenuCache()
        {
            if (_mpButton != null && !_mpButton)
                _mpButton = null;
            _menu = null;
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
                WireButton(_mpButton, MultiplayerScreens.OpenRoot);
                SetRow(_mpButton, MultiplayerRowOffsetY());
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
            SetRow(_mpButton, MultiplayerRowOffsetY());
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

            var seen = new HashSet<int>();
            for (int r = 0; r < roots.Count; r++)
            {
                Transform root = roots[r];
                if (root == null || !seen.Add(root.GetInstanceID()))
                    continue;

                var kill = new List<GameObject>(8);
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (t != null && t.name == MpButtonName)
                        kill.Add(t.gameObject);
                }
                for (int i = 0; i < kill.Count; i++)
                {
                    if (kill[i] == null || !kill[i])
                        continue;
                    UnityEngine.Object.DestroyImmediate(kill[i]);
                    n++;
                }
            }
            return n;
        }

        private sealed class YokWareUiTag : MonoBehaviour
        {
            public string Kind;
            /// <summary>The label's English text; shown through <see cref="Loc.T"/>, again on a language change.</summary>
            public string LabelEn;
        }

        private static string _labelLanguage; // process-scoped: language the menu was last written in

        /// <summary>The game's language changed in Options: write the mod's menu again in it.</summary>
        private static void RelabelOnLanguageChange()
        {
            if (_labelLanguage == Loc.Language)
                return;
            bool first = _labelLanguage == null;
            _labelLanguage = Loc.Language;
            if (first)
                return;
            if (_mpButton != null && _mpButton)
            {
                YokWareUiTag tag = _mpButton.GetComponent<YokWareUiTag>();
                tk2dTextMesh tm = _mpButton.GetComponentInChildren<tk2dTextMesh>(true);
                if (tag != null && tm != null && !string.IsNullOrEmpty(tag.LabelEn))
                {
                    tm.text = Loc.T(tag.LabelEn);
                    tm.Commit();
                    FitButtonHitbox(_mpButton);
                }
            }
            Vm.Current?.Rebuild();
        }
    }
}
