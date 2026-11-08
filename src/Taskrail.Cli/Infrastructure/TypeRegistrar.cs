using Spectre.Console.Cli;

namespace Taskrail.Cli.Infrastructure;

/// <summary>
/// Minimal DI for Spectre.Console.Cli: multiple registrations per service, <c>IEnumerable&lt;T&gt;</c> resolution,
/// and constructor injection for unregistered concrete types (commands).
/// </summary>
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly Dictionary<Type, List<Func<object>>> _factories = [];

    public void Register(Type service, Type implementation) => Add(service, () => Create(implementation));

    public void RegisterInstance(Type service, object implementation) => Add(service, () => implementation);

    public void RegisterLazy(Type service, Func<object> factory)
    {
        var lazy = new Lazy<object>(factory);
        Add(service, () => lazy.Value);
    }

    public ITypeResolver Build() => new Resolver(this);

    private void Add(Type service, Func<object> factory)
    {
        if (!_factories.TryGetValue(service, out var list))
        {
            _factories[service] = list = [];
        }

        list.Add(factory);
    }

    private object Create(Type type)
    {
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => Resolve(p.ParameterType)
            ?? throw new InvalidOperationException($"Cannot resolve {p.ParameterType} for {type}.")).ToArray();
        return ctor.Invoke(args);
    }

    private object? Resolve(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        if (_factories.TryGetValue(type, out var factories) && factories.Count > 0)
        {
            return factories[^1]();
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var element = type.GetGenericArguments()[0];
            var items = _factories.TryGetValue(element, out var all) ? all.Select(f => f()).ToArray() : [];
            var array = Array.CreateInstance(element, items.Length);
            Array.Copy(items, array, items.Length);
            return array;
        }

        return type is { IsAbstract: false, IsInterface: false } ? Create(type) : null;
    }

    private sealed class Resolver(TypeRegistrar registrar) : ITypeResolver
    {
        public object? Resolve(Type? type) => registrar.Resolve(type);
    }
}
