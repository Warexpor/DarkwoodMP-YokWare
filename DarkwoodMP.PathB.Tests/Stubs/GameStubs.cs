// Minimal stand-ins for the game/Unity types that shipped wire-message files reference, so the
// message structs can be compiled and round-tripped without a Darkwood install. Only the members
// the linked files actually touch are declared; nothing here is behaviour.
#pragma warning disable CS0649

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public class Transform
    {
        public Vector3 position;
    }

    public class Component
    {
        public Transform transform = new Transform();
    }
}

namespace DWMPHorde.Sync
{
    public static class DreamSession
    {
        public static bool IsActive;
        public static string PresetName;
        public static int SessionId;
        public static byte LevelBits => 0;
        public static string[] GetCompletedPresets() => System.Array.Empty<string>();
    }
}

// Game classes live in the global namespace.
public class Dreams
{
    public static Dreams Instance;
    public bool dreaming;
    public UnityEngine.Component dreamLocation;
}

public class ItemUpgrade
{
    public string name;
}

public class InvItemClass
{
    public System.Collections.Generic.List<ItemUpgrade> upgrades;
    public static bool isNull(InvItemClass item) => item == null;
}

public class ItemsDatabase
{
    public ItemUpgrade getUpgrade(string name) => null;
}

public class Singleton<T> where T : class
{
    public static T Instance;
}

public class Character
{
    public enum Behaviour { idle, walking, running, defensive, chasingTarget, escaping, listening, following }
}
