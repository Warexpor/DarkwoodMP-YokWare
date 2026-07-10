using System;
using System.Collections.Generic;

namespace DarkwoodMP.DependencyInjection;

/// <summary>
/// Manual DI container — zero external dependencies, works on Mono .NET 3.5.
/// Simple service locator for the mod's sync modules.
/// </summary>
public static class ServiceLocator
{
    private static readonly Dictionary<Type, object> _services = new();

    public static void Initialize() { }

    public static void Register(Type type, object instance)
    {
        _services[type] = instance;
        ModLogger.Msg($"[ServiceLocator] Registered {type.Name}");
    }

    public static T Resolve<T>()
    {
        if (_services.TryGetValue(typeof(T), out var service) && service is T result)
            return result;
        ModLogger.Error($"[ServiceLocator] Service not registered: {typeof(T).Name}");
        return default;
    }

    public static object Resolve(Type type)
    {
        if (_services.TryGetValue(type, out var service))
            return service;
        ModLogger.Error($"[ServiceLocator] Service not registered: {type.Name}");
        return null;
    }

    public static bool IsRegistered<T>() => _services.ContainsKey(typeof(T));
}
