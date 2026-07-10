using System;
using DarkwoodMP.Packets;

namespace DarkwoodMP.Network;

/// <summary>
/// Multi-peer fan-out policy — Ironbark v2 facade over IronbarkRegistry / IronbarkMeta.
/// </summary>
public enum ForwardKind : byte
{
    None = 0,
    Direct = 1,
    Player = 2,
}

public static class ForwardPolicy
{
    public static ForwardKind ForPacketType(PacketType type)
    {
        switch (IronbarkMeta.Forward(type))
        {
            case IronbarkForward.None: return ForwardKind.None;
            case IronbarkForward.Player: return ForwardKind.Player;
            default: return ForwardKind.Direct;
        }
    }

    public static bool ShouldFanOut(PacketType type, string actionName = null)
    {
        return ForPacketType(type) != ForwardKind.None;
    }

    public static bool ShouldFanOut(ushort messageId)
    {
        return IronbarkRegistry.ShouldFanOut(messageId);
    }
}
