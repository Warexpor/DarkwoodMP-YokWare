using System;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Handles horror event effects on the client side
/// </summary>
public class EventSync
{
    private readonly NetworkLayer _network;

    public EventSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnEventTrigger(EventTriggerPacket packet)
    {
        ModLogger.Msg($"[EventSync] Horror event '{packet.EventName}' at {packet.X:F1},{packet.Y:F1},{packet.Z:F1} (severity: {packet.Severity})");
    }
}
