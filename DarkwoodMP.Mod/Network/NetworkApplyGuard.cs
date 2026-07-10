using System;
using DarkwoodMP.GameLogic;

namespace DarkwoodMP.Network;

/// <summary>
/// Nested-safe scope for applying remote state. Horde-style depth counting
/// so inner handlers cannot clear the outer receive scope early.
/// Sets <see cref="RemoteApply.Active"/> for existing patches/modules.
/// </summary>
public readonly struct NetworkApplyGuard : IDisposable
{
    private readonly bool _entered;

    /// <summary>True while any NetworkApplyGuard is on the stack (or Active depth &gt; 0).</summary>
    public static bool IsActive => RemoteApply.Active;

    public NetworkApplyGuard(bool enter = true)
    {
        _entered = enter;
        if (enter)
            RemoteApply.PushApply();
    }

    public void Dispose()
    {
        if (_entered)
            RemoteApply.PopApply();
    }

    /// <summary>Force-clear depth + apply flags (network stop / emergency).</summary>
    public static void ResetDepth()
    {
        RemoteApply.ResetApplyDepth();
    }
}
