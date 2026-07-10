using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.DependencyInjection;
using DarkwoodMP.Packets;

namespace DarkwoodMP.Network;

/// <summary>
/// Chat system for DarkwoodMP - manages chat messages and display with history and input.
/// </summary>
public class ChatManager
{
    private static ChatManager? _instance;
    private readonly List<ChatEntry> _chatLog = new();
    private readonly float _maxLogAge = 300f; // Remove entries older than 5 minutes
    private const int MaxLogEntries = 50;

    public static ChatManager Instance => _instance ??= new ChatManager();

    private ChatManager() { }

    public void AddMessage(string senderName, string message, int senderId = -1)
    {
        var entry = new ChatEntry
        {
            SenderId = senderId,
            SenderName = senderName ?? "Unknown",
            Message = message ?? "",
            Timestamp = Time.time
        };

        _chatLog.Add(entry);
        while (_chatLog.Count > MaxLogEntries)
            _chatLog.RemoveAt(0);

        ModLogger.Msg($"[CHAT] {entry.SenderName}: {entry.Message}");
    }

    public void AddSystemMessage(string message)
    {
        AddMessage("[System]", message, -1);
    }

    public void SendMessage(string message)
    {
        if (string.IsNullOrEmpty(message) || NetworkManager.Instance == null) return;

        if (!NetworkManager.Instance.IsConnected)
        {
            ModLogger.Msg("[CHAT] Not connected to any session");
            return;
        }

        var config = ModConfig.Load();
        var packet = new ChatMessagePacket
        {
            SenderId = NetworkManager.Instance.LocalPlayerId >= 0 ? NetworkManager.Instance.LocalPlayerId : 0,
            SenderName = config.PlayerName,
            Message = message,
            Timestamp = Time.time
        };
        SendMessage(packet);

        // Show own message immediately; the server echo is deduplicated in OnChatReceived
        AddMessage(packet.SenderName, packet.Message, packet.SenderId);
        ShowSpeechBubble(packet.SenderId, packet.Message);
    }

    public void SendMessage(ChatMessagePacket packet)
    {
        if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;

        var network = ServiceLocator.Resolve<NetworkLayer>();
        if (network == null) return;

        network.SendReliable(packet);
    }

    public void OnChatReceived(ChatMessagePacket packet)
    {
        // Own messages are already shown locally on send
        if (NetworkManager.Instance != null && packet.SenderId == NetworkManager.Instance.LocalPlayerId)
            return;
        AddMessage(packet.SenderName, packet.Message, packet.SenderId);
        ShowSpeechBubble(packet.SenderId, packet.Message);
    }

    /// <summary>
    /// Immersive chat (v0.5): render the message as in-game character speech
    /// above the speaker, exactly like NPC barks. The game's own path is
    /// CharBase.displayMessage -> Core.displayMessage(msg, transform, 1f,
    /// false) (verified IL; CharacterMessage types the text out and follows
    /// the transform by itself). The chat overlay still logs everything -
    /// the bubble is a bonus and must never break chat.
    /// </summary>
    private void ShowSpeechBubble(int senderId, string message)
    {
        try
        {
            if (senderId < 0 || string.IsNullOrEmpty(message)) return;
            var manager = NetworkManager.Instance;
            if (manager == null) return;

            Transform speaker;
            if (senderId == manager.LocalPlayerId)
            {
                speaker = ServiceLocator.Resolve<GameLogic.PlayerSync>()?.LocalPlayerTransform;
            }
            else
            {
                var clone = manager.GetRemotePlayer(senderId);
                speaker = clone != null ? clone.transform : null;
            }
            if (speaker == null) return;

            // Very long messages would fill the screen - the overlay keeps
            // the full text, the bubble gets the spoken-line cut
            var line = message.Length > 160 ? message.Substring(0, 157) + "..." : message;
            Core.displayMessage(line, speaker, 1f, false);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[CHAT] Speech bubble failed (message still in log): {ex.Message}");
        }
    }

    public void OnSystemMessage(SystemMessagePacket packet)
    {
        AddSystemMessage(packet.Message);
    }

    public void OnPlayerJoined(int playerId, string playerName)
    {
        AddSystemMessage($"{playerName} (ID: {playerId}) has joined");
    }

    public void OnPlayerLeft(int playerId, string playerName)
    {
        AddSystemMessage($"{playerName} (ID: {playerId}) has left");
    }

    public List<ChatEntry> GetRecentMessages(int count)
    {
        // Remove old entries
        var now = Time.time;
        _chatLog.RemoveAll(e => (now - e.Timestamp) > _maxLogAge);

        // Return last N entries
        var result = new List<ChatEntry>();
        for (int i = _chatLog.Count - 1; i >= 0 && result.Count < count; i--)
            result.Add(_chatLog[i]);

        result.Reverse();
        return result;
    }
}

public class ChatEntry
{
    public int SenderId;
    public string SenderName = "Unknown";
    public string Message = "";
    public float Timestamp;
}
