using System;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace AzdoScanner
{
    public class TypeRegistrar : ITypeRegistrar
    {
        private readonly IServiceCollection _builder;
        private IServiceProvider? _provider;

        public TypeRegistrar(IServiceProvider provider)
        {
            _provider = provider;
            _builder = new ServiceCollection();
        }

        public ITypeResolver Build()
        {
            return new TypeResolver(_provider!);
        }

        public void Register(Type service, Type implementation)
        {
            _builder.AddSingleton(service, implementation);
        }

        public void RegisterInstance(Type service, object implementation)
        {
            _builder.AddSingleton(service, implementation);
        }

        public void RegisterLazy(Type service, Func<object> factory)
        {
            _builder.AddSingleton(service, _ => factory());
        }
    }

    public class TypeResolver : ITypeResolver
    {
        private readonly IServiceProvider _provider;
        public TypeResolver(IServiceProvider provider)
        {
            _provider = provider;
        }
        public object? Resolve(Type? type)
        {
            return type == null ? null : _provider.GetService(type);
        }
    }
}
