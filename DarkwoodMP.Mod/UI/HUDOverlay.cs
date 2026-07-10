using System;
using UnityEngine;
using DarkwoodMP.Network;

namespace DarkwoodMP.UI;

/// <summary>
/// The multiplayer menu: a draggable window toggled with a single hotkey
/// (default F1, configurable via MenuKey). Hosts, joins, disconnects and edits
/// the config. Styled via DarkwoodTheme to blend in with the game's look.
/// While connected and closed, only a small status line is shown.
/// </summary>
public class HUDOverlay : MonoBehaviour
{
    public static bool MenuVisible;

    private const int WindowId = 0x44574D50; // "DWMP"
    private Rect _windowRect = new Rect(60f, 60f, 350f, 0f);
    private Vector2 _playerScroll;
    private bool _fieldsLoaded;

    // Editable copies of the config values
    private string _playerName = "";
    private string _serverIp = "";
    private string _serverPort = "";
    private string _password = "";
    private string _worldSeed = "";

    private string _feedback = "";

    public void OnGUI()
    {
        var manager = NetworkManager.Instance;
        if (manager == null) return;

        var prevSkin = GUI.skin;
        GUI.skin = DarkwoodTheme.Skin;
        try
        {
            if (!MenuVisible)
            {
                if (manager.IsConnected)
                    DrawStatusLine(manager);
                return;
            }

            if (!_fieldsLoaded)
                LoadFields();

            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "D A R K W O O D   M P");
        }
        finally
        {
            GUI.skin = prevSkin;
        }
    }

    private void DrawStatusLine(NetworkManager manager)
    {
        var text = $"{ProductInfo.Name} v{ProductInfo.Version}  |  {(manager.IsHost ? "hosting" : "connected")}  |  {manager.ConnectedPlayers.Count} player(s)  |  menu: {ModConfig.Load().MenuKey}";
        var size = DarkwoodTheme.StatusLine.CalcSize(new GUIContent(text));
        GUI.Label(new Rect(10f, 6f, size.x + 6f, size.y + 2f), text, DarkwoodTheme.StatusLine);
    }

    private void DrawWindow(int id)
    {
        var manager = NetworkManager.Instance;
        var config = ModConfig.Load();

        GUILayout.BeginVertical();

        // --- Status ---
        GUILayout.Label(manager.IsConnected
            ? $"◆ {(manager.IsHost ? "HOSTING" : "CONNECTED")}  (id {manager.LocalPlayerId})"
            : "◇ NOT CONNECTED",
            manager.IsConnected ? DarkwoodTheme.OkLabel : DarkwoodTheme.WarnLabel);

        GUILayout.Space(4f);

        if (manager.IsConnected)
        {
            GUILayout.Label($"SURVIVORS ({manager.ConnectedPlayers.Count})", DarkwoodTheme.Header);
            GUILayout.BeginVertical(GUI.skin.box);
            _playerScroll = GUILayout.BeginScrollView(_playerScroll, GUILayout.Height(76f));
            foreach (var playerId in manager.ConnectedPlayers)
                GUILayout.Label($"› {manager.GetPlayerName(playerId)}");
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(8f);
            if (GUILayout.Button("DISCONNECT"))
            {
                manager.Disconnect();
                _feedback = "Disconnected.";
            }
        }
        else
        {
            DrawField("Name", ref _playerName);
            DrawField("World seed", ref _worldSeed);

            GUILayout.Space(8f);
            GUILayout.Label("— HOST —", DarkwoodTheme.Header);
            if (GUILayout.Button($"HOST GAME  (port {_serverPort})"))
            {
                if (SaveFields(config))
                {
                    manager.StartHost(config.ServerPort, config.SessionPassword, config.PlayerName);
                    _feedback = manager.IsConnected
                        ? $"Hosting on port {config.ServerPort}."
                        : "Hosting failed - see console.";
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("— JOIN —", DarkwoodTheme.Header);
            DrawField("IP / host", ref _serverIp);
            DrawField("Port", ref _serverPort);
            DrawField("Password", ref _password);
            if (GUILayout.Button("JOIN GAME"))
            {
                if (SaveFields(config))
                {
                    manager.StartClient(config.ServerIp, config.ServerPort, config.SessionPassword, config.PlayerName);
                    _feedback = $"Connecting to {config.ServerIp}:{config.ServerPort}...";
                }
            }

            GUILayout.Space(8f);
            if (GUILayout.Button("SAVE SETTINGS"))
            {
                if (SaveFields(config))
                    _feedback = "Settings saved.";
            }
        }

        if (!string.IsNullOrEmpty(_feedback))
        {
            GUILayout.Space(4f);
            GUILayout.Label(_feedback, DarkwoodTheme.AccentLabel);
        }

        GUILayout.Space(6f);
        GUILayout.Label($"v{ProductInfo.Version}   {config.MenuKey}: close   CTRL+C: chat", DarkwoodTheme.Dim);

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }

    private static void DrawField(string label, ref string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(80f));
        value = GUILayout.TextField(value ?? "");
        GUILayout.EndHorizontal();
    }

    private void LoadFields()
    {
        _fieldsLoaded = true;
        var config = ModConfig.Load();
        _playerName = config.PlayerName;
        _serverIp = config.ServerIp;
        _serverPort = config.ServerPort.ToString();
        _password = config.SessionPassword;
        _worldSeed = config.WorldSeed.ToString();
    }

    /// <summary>Validate the edited fields into the config and persist it.</summary>
    private bool SaveFields(ModConfig config)
    {
        if (!int.TryParse(_serverPort, out var port) || port <= 0 || port > 65535)
        {
            _feedback = "Invalid port.";
            return false;
        }
        if (!int.TryParse(_worldSeed, out var seed))
        {
            _feedback = "Invalid world seed (must be a number).";
            return false;
        }
        if (string.IsNullOrEmpty(_playerName.Trim()))
        {
            _feedback = "Name must not be empty.";
            return false;
        }

        config.PlayerName = _playerName.Trim();
        config.ServerIp = _serverIp.Trim();
        config.ServerPort = port;
        config.SessionPassword = _password;
        if (config.WorldSeed != seed)
            _feedback = "World seed saved - takes effect after restarting the game.";
        config.WorldSeed = seed;
        config.Save();
        return true;
    }
}
