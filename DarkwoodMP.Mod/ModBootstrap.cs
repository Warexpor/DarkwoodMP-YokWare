using System;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.UI;
using UnityEngine;

namespace DarkwoodMP;

/// <summary>
/// Shared init + frame tick for BepInEx and MelonLoader entry points.
/// </summary>
public static class ModBootstrap
{
    public static string LoaderName { get; private set; } = "unknown";
    public static KeyCode MenuKey { get; private set; } = KeyCode.F1;
    public static NetworkManager NetworkManager { get; private set; }
    public static ChatInput ChatInput { get; private set; }

    private static GameObject _networkManagerObject;
    private static GameObject _hudObject;
    private static bool _started;

    /// <summary>Call once after logger is initialized.</summary>
    public static void Run(string loaderName)
    {
        if (_started) return;
        _started = true;
        LoaderName = loaderName ?? "unknown";

        var config = ModConfig.Load();
        ModLogger.ApplyConfig(config);

        if (config.WorldSeed != 0)
            Patches.WorldGenSeed_Patch.Apply(config.WorldSeed);
        else
            ModLogger.Warning("World", "WorldSeed=0 — worlds will diverge. Set same non-zero seed on all peers and start NEW games.");

        if (!Enum.TryParse(config.MenuKey, true, out KeyCode menuKey))
        {
            ModLogger.Warning("Boot", $"Unknown MenuKey '{config.MenuKey}', falling back to F1");
            menuKey = KeyCode.F1;
        }
        MenuKey = menuKey;

        SpectatorModeController.EnsureExists();

        try
        {
            _networkManagerObject = new GameObject("DarkwoodMP_NetworkManager");
            UnityEngine.Object.DontDestroyOnLoad(_networkManagerObject);
            NetworkManager = _networkManagerObject.AddComponent<NetworkManager>();
            NetworkManager.Initialize();

            _hudObject = new GameObject("DarkwoodMP_HUD");
            UnityEngine.Object.DontDestroyOnLoad(_hudObject);
            _hudObject.AddComponent<HUDOverlay>();
            ChatInput = _hudObject.AddComponent<ChatInput>();

            ModLogger.BootBanner(LoaderName, config.ConfigPath, config.WorldSeed);
            ModLogger.Msg("Boot", $"Keys: {MenuKey}=menu | LeftCtrl+C=chat | F4=spectate cycle (night death)");
        }
        catch (Exception ex)
        {
            ModLogger.Error("Boot", $"Failed to initialize: {ex.Message}");
        }
    }

    public static void Tick()
    {
        NetworkManager?.Update();
        MultiplayerMenu.OnUpdate();
        HandleKeybinds();
    }

    private static void HandleKeybinds()
    {
        if (Input.GetKeyDown(MenuKey))
            HUDOverlay.MenuVisible = !HUDOverlay.MenuVisible;

        if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.C))
            ToggleChat();
    }

    private static void ToggleChat()
    {
        if (NetworkManager == null || !NetworkManager.IsConnected)
        {
            ModLogger.Msg("Not connected to a server");
            return;
        }
        ChatInput?.Toggle(!ChatInput.IsActive);
    }
}
