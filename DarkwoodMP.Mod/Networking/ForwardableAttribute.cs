using System;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Mark a NetMessageType as forwardable to other peers as raw payload.
    /// The message is sent to all other peers without wrapping (position/state types).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class ForwardableAttribute : Attribute { }

    /// <summary>
    /// Mark a NetMessageType as forwardable with player-id wrapping.
    /// The message is wrapped in RemotePlayerForwardMessage so the receiver
    /// knows which player originated it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class ForwardablePlayerAttribute : Attribute { }
}
