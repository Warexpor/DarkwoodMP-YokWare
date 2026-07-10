using System;
using DarkwoodMP.Network;
using UnityEngine;

namespace DarkwoodMP.UI;

/// <summary>
/// Native tk2d title-screen integration: injects a MULTIPLAYER button into the
/// game's MainMenu (Menu0) and swaps in a HOST GAME / JOIN GAME / SETTINGS /
/// BACK panel built from clones of the game's own buttons.
///
/// No Harmony needed: Button.getClicked invokes the public `OnFire` Action
/// before dispatching `function` via SendMessage (IL-verified), so a clone
/// with function="" and OnFire set is a first-class native button. Buttons
/// self-handle mouse raycasts in Button.Update, and PositionMe (public
/// `offset` + `init()`) anchors them, so a clone needs only a new offset.
///
/// Poll-driven from ModMain.OnUpdate instead of patching MainMenu.OnEnable:
/// rebuilds whenever the title menu is up and our objects are gone (scene
/// reload, return-to-menu), and self-heals if the game reactivates Menu0
/// behind our panel (ESC navigation).
///
/// Deliberate v1 limits: mouse-only (not in MainMenu.controllerButtons), and
/// typed settings (IP/port/name/password) stay in the IMGUI overlay, opened
/// by the SETTINGS button - tk2d has no text-input widget.
/// </summary>
public static class MultiplayerMenu
{
    private const float RowSpacing = 60f;
    private const int PollInterval = 15; // frames

    private static MainMenu _menu;         // cached scene singleton
    private static GameObject _mpButton;   // MULTIPLAYER entry in Menu0
    private static GameObject _panel;      // container for the MP button set
    private static GameObject _joinButton; // for CONNECTING... feedback
    private static bool _joinPending;
    private static int _lastPoll;

    public static void OnUpdate()
    {
        if (Time.frameCount - _lastPoll < PollInterval) return;
        _lastPoll = Time.frameCount;

        try
        {
            if (!Core.mainMenu)
            {
                // Game running: scene objects die on load; drop stale refs
                _mpButton = null;
                _panel = null;
                _joinButton = null;
                _joinPending = false;
                return;
            }

            if (_menu == null)
                _menu = UnityEngine.Object.FindObjectOfType(typeof(MainMenu)) as MainMenu;
            if (_menu == null || _menu.Menu0 == null) return;

            if (_mpButton == null && _menu.Menu0.activeInHierarchy)
                Inject();

            // The game navigated back to Menu0 on its own (ESC etc.) - yield
            if (_panel != null && _panel.activeSelf && _menu.Menu0.activeSelf)
                _panel.SetActive(false);

            if (_joinPending)
                PollJoinState();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[MultiplayerMenu] {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    private static void Inject()
    {
        var template = _menu.quitBtn;
        if (template == null || template.GetComponent<Button>() == null) return;

        _mpButton = CloneButton(template, template.transform.parent,
            "DWMP_MultiplayerBtn", "MULTIPLAYER", OpenPanel);

        // One row below the lowest active root-menu button (adapts to both
        // title layout and the pause-menu layout MainMenu.OnEnable arranges)
        var lowest = 0f;
        foreach (var pm in _menu.Menu0.GetComponentsInChildren<PositionMe>(false))
        {
            if (pm.gameObject == _mpButton) continue;
            if (pm.offset.y < lowest) lowest = pm.offset.y;
        }
        SetRow(_mpButton, lowest - RowSpacing);

        ModLogger.Msg("[MultiplayerMenu] Injected MULTIPLAYER into main menu");
    }

    private static void BuildPanel()
    {
        var template = _menu.quitBtn;
        _panel = new GameObject("DWMP_MenuPanel");
        // Sibling of Menu0 so toggling Menu0 doesn't toggle us
        _panel.transform.SetParent(_menu.Menu0.transform.parent, false);

        var host = CloneButton(template, _panel.transform, "DWMP_HostBtn", "HOST GAME", OnHostClicked);
        _joinButton = CloneButton(template, _panel.transform, "DWMP_JoinBtn", "JOIN GAME", OnJoinClicked);
        var settings = CloneButton(template, _panel.transform, "DWMP_SettingsBtn", "SETTINGS", OnSettingsClicked);
        var restore = CloneButton(template, _panel.transform, "DWMP_RestoreBtn", "RESTORE SELF", OnRestoreClicked);
        var back = CloneButton(template, _panel.transform, "DWMP_BackBtn", "BACK", ClosePanel);

        SetRow(host, 0f);
        SetRow(_joinButton, -RowSpacing);
        SetRow(settings, -RowSpacing * 2);
        SetRow(restore, -RowSpacing * 3);
        SetRow(back, -RowSpacing * 4);
    }

    private static GameObject CloneButton(GameObject template, Transform parent,
        string name, string label, Action onFire)
    {
        var go = UnityEngine.Object.Instantiate(template, parent);
        go.name = name;
        go.SetActive(true);

        // The title buttons are TEXTURE-based: the visible word is a
        // per-language SPRITE swapped by LocalizedText.textureBased - there
        // is no text to edit, which is why clones kept reading "EXIT". So:
        // strip localization (Immediate - a deferred Destroy let its init
        // re-stamp the sprite), hide every cloned visual, and attach a real
        // tk2dTextMesh label instead. The root collider survives, so
        // clicking/rollover keep working.
        foreach (var loc in go.GetComponentsInChildren<LocalizedText>(true))
            UnityEngine.Object.DestroyImmediate(loc);
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            r.enabled = false;

        var btn = go.GetComponent<Button>();
        btn.function = "";       // getClicked skips SendMessage when empty
        btn.popupType = "";
        btn.localized = false;
        btn.sprite = null;       // rollover/rollout then only color the textMesh
        btn.OnFire = () => Guarded(onFire);

        var tm = CreateLabel(go.transform, label);
        if (tm != null)
        {
            btn.textMesh = tm;
            btn.baseColor = tm.color; // rollout() restores this after hover
        }
        return go;
    }

    /// <summary>
    /// Text label for a cloned button: a copy of MainMenu.CurrentVersion (a
    /// tk2dTextMesh guaranteed to exist in the menu scene, using the game
    /// font) - the buttons themselves carry no editable text (sprite words).
    /// </summary>
    private static tk2dTextMesh CreateLabel(Transform parent, string text)
    {
        var source = _menu != null ? _menu.CurrentVersion : null;
        if (source == null)
        {
            ModLogger.Warning("[MultiplayerMenu] MainMenu.CurrentVersion missing - button will be blank");
            return null;
        }

        var labelGo = UnityEngine.Object.Instantiate(source.gameObject, parent);
        labelGo.name = "DWMP_Label";

        // CurrentVersion carries its own scripts - notably a screen-anchoring
        // PositionMe, which (cloned) re-anchored every label to the version
        // string's corner: all labels rendered stacked in one spot while the
        // clickboxes sat correctly. Keep ONLY the text mesh component.
        foreach (var mb in labelGo.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb is tk2dTextMesh) continue;
            try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
        }

        labelGo.transform.localPosition = Vector3.zero;
        labelGo.transform.localRotation = Quaternion.identity;
        labelGo.transform.localScale = source.transform.localScale;
        labelGo.SetActive(true);

        var tm = labelGo.GetComponent<tk2dTextMesh>();
        if (tm == null) return null;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.text = text;
        tm.Commit();

        // Size + position against the button's own CLICKBOX - the only
        // reference in the same world space (PositionMe row offsets are
        // screen-anchored units and say nothing about world size, which is
        // why fixed target heights rendered huge).
        var col = parent.GetComponent<Collider>();
        var rend = labelGo.GetComponent<Renderer>();
        if (col != null && rend != null
            && rend.bounds.size.y > 0.001f && rend.bounds.size.x > 0.001f)
        {
            var fitH = col.bounds.size.y * 0.65f / rend.bounds.size.y;
            var fitW = col.bounds.size.x * 1.05f / rend.bounds.size.x;
            var factor = Mathf.Clamp(Mathf.Min(fitH, fitW), 0.02f, 50f);
            labelGo.transform.localScale *= factor;

            // Center on the clickbox (collider centers are often offset from
            // the button transform), nudged toward the UI camera so the text
            // draws in front of whatever the button rendered at
            var pos = col.bounds.center;
            var camObj = Core.CamUI;
            var cam = camObj != null ? camObj.GetComponent<Camera>() : null;
            if (cam != null) pos -= cam.transform.forward * 1f;
            labelGo.transform.position = pos;
        }
        return tm;
    }

    private static void SetRow(GameObject go, float y)
    {
        var pm = go.GetComponent<PositionMe>();
        if (pm == null) return;
        pm.offset = new Vector2(pm.offset.x, y);
        pm.init();
    }

    private static void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) { ModLogger.Error($"[MultiplayerMenu] click: {ex.Message}"); }
    }

    // ------------------------------------------------------------------
    // Click handlers
    // ------------------------------------------------------------------

    private static void OpenPanel()
    {
        if (_panel == null) BuildPanel();
        _menu.Menu0.SetActive(false);
        _panel.SetActive(true);
        RefreshJoinLabel();
    }

    private static void ClosePanel()
    {
        if (_panel != null) _panel.SetActive(false);
        _menu.Menu0.SetActive(true);
    }

    private static void OnHostClicked()
    {
        var manager = NetworkManager.Instance;
        if (manager == null) return;
        if (manager.IsConnected)
        {
            ChatManager.Instance?.AddSystemMessage("Already in a session - disconnect first (F1 menu).");
            return;
        }

        var config = ModConfig.Load();
        manager.StartHost(config.ServerPort, config.SessionPassword, config.PlayerName);
        if (!manager.IsHost) return; // port bind failed; StartHost logged it

        ChatManager.Instance?.AddSystemMessage(
            $"Hosting on port {config.ServerPort} - now start or continue a game. " +
            "Joiners download your world once it is loaded.");

        // Drop the player into the native world-select flow immediately
        ClosePanel();
        _menu.displayProfilesMenu();
    }

    private static void OnJoinClicked()
    {
        var manager = NetworkManager.Instance;
        if (manager == null || _joinPending) return;
        if (manager.IsConnected)
        {
            ChatManager.Instance?.AddSystemMessage("Already in a session - disconnect first (F1 menu).");
            return;
        }

        var config = ModConfig.Load();
        manager.StartClient(config.ServerIp, config.ServerPort, config.SessionPassword, config.PlayerName);
        _joinPending = true;
        SetLabel(_joinButton, "CONNECTING...");
    }

    private static void OnSettingsClicked()
    {
        // Typed fields (IP/port/name/password) live in the IMGUI overlay
        HUDOverlay.MenuVisible = !HUDOverlay.MenuVisible;
    }

    private static void OnRestoreClicked()
    {
        if (Player.Instance == null)
        {
            ChatManager.Instance?.AddSystemMessage(
                "Load into a game first. Clients also auto-restore self-backup after world gen.");
            return;
        }
        ClientStateBackup.ResetSession();
        ClientStateBackup.TryRestoreLocal();
        ChatManager.Instance?.AddSystemMessage("Applied local self-backup if present (YokWareBackups).");
    }

    // ------------------------------------------------------------------
    // Join feedback
    // ------------------------------------------------------------------

    private static void PollJoinState()
    {
        var manager = NetworkManager.Instance;
        if (manager == null) { _joinPending = false; return; }

        if (manager.IsConnected)
        {
            _joinPending = false;
            SetLabel(_joinButton, manager.IsDownloadingWorld ? "DOWNLOADING..." : "CONNECTED");
        }
        else if (!manager.IsConnecting)
        {
            // Handshake gave up (NetworkLayer times out after 10s)
            _joinPending = false;
            SetLabel(_joinButton, "JOIN GAME");
            ChatManager.Instance?.AddSystemMessage("Could not reach the host (timeout).");
        }
    }

    private static void RefreshJoinLabel()
    {
        var manager = NetworkManager.Instance;
        if (_joinPending || manager == null) return;
        if (manager.IsConnected)
            SetLabel(_joinButton, manager.IsDownloadingWorld ? "DOWNLOADING..." : "CONNECTED");
        else
            SetLabel(_joinButton, "JOIN GAME");
    }

    private static void SetLabel(GameObject buttonGo, string text)
    {
        if (buttonGo == null) return;
        var tm = buttonGo.GetComponentInChildren<tk2dTextMesh>(true);
        if (tm == null) return;
        tm.text = text;
        tm.Commit();
    }
}
