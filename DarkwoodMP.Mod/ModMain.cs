#if BEPINEX
using BepInEx;
using UnityEngine;
using DarkwoodMP.Network;

namespace DarkwoodMP;

/// <summary>BepInEx entry (default build). Shared body: <see cref="ModBootstrap"/>.</summary>
[BepInPlugin(ProductInfo.Guid, ProductInfo.Name, ProductInfo.Version)]
public class ModMain : BaseUnityPlugin
{
    /// <summary>Alias for product version (call sites / UI).</summary>
    public const string ModVersion = ProductInfo.Version;

    public static ModMain Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
        ModLogger.Initialize(Logger);
        ModBootstrap.Run("BepInEx");
    }

    private void Update() => ModBootstrap.Tick();
}
#endif
