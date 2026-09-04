using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Agent.Cli.Infrastructure;

/// <summary>Bridges Spectre.Console.Cli to Microsoft.Extensions.DependencyInjection.</summary>
public sealed class DependencyInjectionRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    public DependencyInjectionRegistrar(IServiceCollection services) => _services = services;

    public ITypeResolver Build() => new DependencyInjectionResolver(_services.BuildServiceProvider());

    public void Register(Type service, Type implementation) => _services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => _services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => _services.AddSingleton(service, _ => factory());

    private sealed class DependencyInjectionResolver : ITypeResolver, IDisposable
    {
        private readonly ServiceProvider _provider;

        public DependencyInjectionResolver(ServiceProvider provider) => _provider = provider;

        public object? Resolve(Type? type) => type is null ? null : _provider.GetService(type);

        public void Dispose() => _provider.Dispose();
    }
}
