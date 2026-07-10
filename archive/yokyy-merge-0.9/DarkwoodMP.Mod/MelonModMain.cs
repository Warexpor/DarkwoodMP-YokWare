#if MELONLOADER
using MelonLoader;
using DarkwoodMP;

[assembly: MelonInfo(typeof(DarkwoodMP.MelonModMain), DarkwoodMP.ProductInfo.Name, DarkwoodMP.ProductInfo.Version, DarkwoodMP.ProductInfo.Authors)]
[assembly: MelonGame]

namespace DarkwoodMP;

/// <summary>MelonLoader 0.7.x entry. Shared body: <see cref="ModBootstrap"/>.</summary>
public class MelonModMain : MelonMod
{
    public override void OnInitializeMelon()
    {
        ModLogger.Initialize(
            m => MelonLogger.Msg(m),
            m => MelonLogger.Warning(m),
            m => MelonLogger.Error(m));
        ModBootstrap.Run("MelonLoader");
    }

    public override void OnUpdate() => ModBootstrap.Tick();
}
#endif
