using System;

namespace DWMPHorde.Harmony
{
    /// <summary>
    /// Marks a Harmony patch class as cosmetic: if it fails to apply, the failure is
    /// logged and reported but hosting/joining stays allowed. Unmarked classes are
    /// critical (a failure makes <see cref="ModRuntime.PatchingHealthy"/> false).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class OptionalPatchAttribute : Attribute
    {
    }
}
