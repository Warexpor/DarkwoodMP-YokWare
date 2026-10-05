using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Vanilla <c>Light2D._mesh</c> is a public (serialized) field, so <c>Instantiate</c> of a live
    /// object copies the reference: the copy and the source draw into one Mesh. Each rebuilds its
    /// own vertex list and then writes colors sized to it, so every frame one of them sets colors
    /// that do not match the vertices the other just wrote ("Mesh.colors is out of bounds", an
    /// error per frame per shared light, and a light drawn with the other's shape). The remote
    /// player stand-in is a copy of the local player, lights included, and a remote match light is
    /// a copy of a live one. Clearing the reference makes the copy build its own mesh on its next
    /// draw (<c>Light2D.Draw</c> creates one when it is null).
    /// </summary>
    internal static class Light2DUnshare
    {
        internal static void Apply(GameObject root)
        {
            if (root == null)
                return;
            foreach (Light2D light in root.GetComponentsInChildren<Light2D>(true))
            {
                if (light == null)
                    continue;
                light._mesh = null;
                MeshFilter filter = light.GetComponent<MeshFilter>();
                if (filter != null)
                    filter.sharedMesh = null;
            }
        }
    }
}
