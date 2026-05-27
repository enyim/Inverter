using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using X = System.Linq.Expressions.Expression;

namespace Enyim;

public class Inverter : IWiring
{
	private readonly Dictionary<Type, Registration> registrations = new();

	public IServiceProvider Build()
	{
		var snapshot = registrations.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Clone());
		return new ServiceProviderImpl(snapshot);
	}

	public void Add<TService>(Func<IServiceProvider, TService> resolver, Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
	{
		ArgumentNullException.ThrowIfNull(resolver);

		registrations[typeof(TService)] = new DelegateRegistration<TService>(resolver, lifecycle);
	}

	public void Add<TService, TImplementation>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService
	{
		registrations[typeof(TService)] = new GeneratedRegistration<TService, TImplementation>(lifecycle);
	}

	public void Add<TService>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
	{
		Add<TService, TService>(lifecycle);
	}

	public void Add<TService>(TService instance)
		where TService : class
	{
		ArgumentNullException.ThrowIfNull(instance);

		registrations[typeof(TService)] = new InstanceRegistration<TService>(instance);
	}

	public void AutoWire<TService, TImplementation, TArg1>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService
	{
		AutoWire<TImplementation, Func<TArg1, TService>>(lifecycle);
	}

	public void AutoWire<TService, TImplementation, TArg1, TArg2>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService
	{
		AutoWire<TImplementation, Func<TArg1, TArg2, TService>>(lifecycle);
	}

	public void AutoWire<TService, TImplementation, TArg1, TArg2, TArg3>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService
	{
		AutoWire<TImplementation, Func<TArg1, TArg2, TArg3, TService>>(lifecycle);
	}

	public void AutoWire<TService, TImplementation, TArg1, TArg2, TArg3, TArg4>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService
	{
		AutoWire<TImplementation, Func<TArg1, TArg2, TArg3, TArg4, TService>>(lifecycle);
	}

	private void AutoWire<TImplementation, TFunc>(Lifecycle lifecycle)
	{
		registrations[typeof(TFunc)] = new OpenArgFuncRegistration<TImplementation, TFunc>(lifecycle);
	}

	private class ServiceProviderImpl : IServiceProvider, IDisposable, IAsyncDisposable
	{
		private readonly FrozenDictionary<Type, Registration> registrations;
		private bool disposed;

		public ServiceProviderImpl(FrozenDictionary<Type, Registration> registrations)
		{
			this.registrations = registrations;
		}

		object? IServiceProvider.GetService(Type serviceType)
		{
			ArgumentNullException.ThrowIfNull(serviceType);
			if (disposed) throw new ObjectDisposedException(nameof(ServiceProviderImpl));

			if (registrations.TryGetValue(serviceType, out var resolver))
			{
				return resolver.Create(this);
			}

			return null;
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;

			foreach (var reg in registrations.Values)
			{
				reg.Dispose();
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (disposed) return;
			disposed = true;

			foreach (var reg in registrations.Values)
			{
				await reg.DisposeAsync();
			}
		}
	}

	private abstract class Registration : IDisposable, IAsyncDisposable
	{
		protected readonly Lifecycle lifecycle;
		private volatile object? cachedInstance;
		private readonly object singletonLock = new();

		protected Registration(Lifecycle lifecycle)
		{
			this.lifecycle = lifecycle;
		}

		public virtual void Dispose()
		{
			object? instance;
			lock (singletonLock)
			{
				instance = cachedInstance;
				if (instance is IAsyncDisposable)
					throw new InvalidOperationException($"instance of {instance.GetType()} implements {nameof(IAsyncDisposable)} please use {nameof(IAsyncDisposable.DisposeAsync)}");

				cachedInstance = null;
			}

			(instance as IDisposable)?.Dispose();
		}

		public virtual async ValueTask DisposeAsync()
		{
			object? instance;
			lock (singletonLock)
			{
				instance = cachedInstance;
				cachedInstance = null;
			}

			if (instance is IAsyncDisposable ad)
			{
				await ad.DisposeAsync();
			}
			else
			{
				(instance as IDisposable)?.Dispose();
			}
		}

		public abstract Registration Clone();

		protected abstract object CreateInstance(IServiceProvider services);

		public object Create(IServiceProvider services)
		{
			if (lifecycle == Lifecycle.Transient)
				return CreateInstance(services); // TODO track IDisposables (?)

			if (cachedInstance is not null)
				return cachedInstance;

			lock (singletonLock)
			{
				cachedInstance ??= CreateInstance(services);
				return cachedInstance;
			}
		}
	}

	private class DelegateRegistration<T> : Registration
		where T : class
	{
		private readonly Func<IServiceProvider, T> resolver;

		public DelegateRegistration(Func<IServiceProvider, T> resolver, Lifecycle lifecycle)
			: base(lifecycle)
		{
			ArgumentNullException.ThrowIfNull(resolver);

			this.resolver = resolver;
		}

		public override Registration Clone() => new DelegateRegistration<T>(resolver, lifecycle);

		protected override object CreateInstance(IServiceProvider services) => resolver(services) ?? throw new InvalidOperationException("service cannot be null");
	}

	private class InstanceRegistration<TService> : Registration
		where TService : class
	{
		private readonly TService instance;

		public InstanceRegistration(TService instance)
			: base(Lifecycle.Singleton)
		{
			ArgumentNullException.ThrowIfNull(instance);

			this.instance = instance;
		}

		public override Registration Clone() => this;

		protected override object CreateInstance(IServiceProvider services) => instance;

		public override void Dispose() { }
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private class GeneratedRegistration<TService, TImplementation> : DelegateRegistration<TService>
		where TService : class
		where TImplementation : class
	{
		private static Func<IServiceProvider, TService> GetFactory()
		{
			var ctor = typeof(TImplementation).GetConstructors().MaxBy(c => c.GetParameters().Length) ?? throw new InvalidOperationException($"{typeof(TImplementation)} has no accessible constructor");
			var services = X.Parameter(typeof(IServiceProvider), "services");

			var lambda = X.Lambda<Func<IServiceProvider, TService>>(
				X.New(
					ctor,
					ctor.GetParameters()
						.Select<ParameterInfo, X>(p => (p.ParameterType == typeof(IServiceProvider))
														? services
														: p.IsOptional
															? X.Call(Helpers.ResolveOptionalMethod.MakeGenericMethod(p.ParameterType), services, X.Constant(p.DefaultValue))
															: X.Call(Helpers.ResolveRequiredMethod.MakeGenericMethod(p.ParameterType), services)
					)
				), services);

			return lambda.Compile();
		}

		public override Registration Clone() => new GeneratedRegistration<TService, TImplementation>(lifecycle);

		public GeneratedRegistration(Lifecycle lifecycle) : base(GetFactory(), lifecycle) { }
	}

	private class OpenArgFuncRegistration<TImplementation, TFactory> : Registration
	{
		private readonly Func<IServiceProvider, TFactory> func;

		public OpenArgFuncRegistration(Lifecycle lifecycle) : base(lifecycle)
		{
			func = FuncFactory.GetFactory<TImplementation, TFactory>();
		}

		public override Registration Clone() => new OpenArgFuncRegistration<TImplementation, TFactory>(lifecycle);

		protected override object CreateInstance(IServiceProvider services) => func(services) ?? throw new InvalidOperationException("factory func should not have been null");
	}

	private static class FuncFactory
	{
		public static Func<IServiceProvider, TFactory> GetFactory<TImplementation, TFactory>()
		{
			var funcType = typeof(TFactory);
			if (!funcType.IsGenericType) throw new InvalidOperationException();

			var funcArgs = funcType.GetGenericArguments();
			if (funcArgs.Length < 2) throw new InvalidOperationException("Func must have at least one argument");
			if (X.GetFuncType(funcArgs) != funcType) throw new InvalidOperationException($"{typeof(TFactory)} must be System.Func<>");

			// last arg is return type, rest are input params
			var returnType = funcArgs[^1];
			var openArgs = funcArgs[0..^1];

			var ctor = (from candidate in typeof(TImplementation).GetConstructors()
						let ctorArgs = candidate.GetParameters()
						where ctorArgs.Take(openArgs.Length).Select(p => p.ParameterType).SequenceEqual(openArgs)
						orderby ctorArgs.Length descending
						select candidate)
					   .FirstOrDefault() ?? throw new InvalidOperationException($"{typeof(TImplementation)} has no accessible constructor");

			// return (sp) => (...) => new Service(..., sp.GetService, sp.GetService...);
			var services = X.Parameter(typeof(IServiceProvider), "services");
			var constParams = openArgs.Select((pt, i) => X.Parameter(pt, $"arg_{i + 1}")).ToArray();

			var innerLambda = X.Lambda(funcType,
				X.New(
					ctor,
					constParams.Concat(
						ctor.GetParameters()
							.Skip(constParams.Length)
							.Select<ParameterInfo, X>(p => (p.ParameterType == typeof(IServiceProvider))
															? services
															: p.IsOptional
																? X.Call(Helpers.ResolveOptionalMethod.MakeGenericMethod(p.ParameterType), services, X.Constant(p.DefaultValue))
																: X.Call(Helpers.ResolveRequiredMethod.MakeGenericMethod(p.ParameterType), services)
						)
					)
				), constParams);

			var outerLambda = X.Lambda<Func<IServiceProvider, TFactory>>(innerLambda, services);

			return outerLambda.Compile();
		}
	}

	private static class Helpers
	{
		public static readonly MethodInfo ResolveRequiredMethod = GetMethod(nameof(Helpers.ResolveRequired));
		public static readonly MethodInfo ResolveOptionalMethod = GetMethod(nameof(Helpers.ResolveOptional));

		private static MethodInfo GetMethod(string name) => typeof(Helpers).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException($"Cannot find {nameof(Helpers)}.{name}");

		private static TDependency ResolveRequired<TDependency>(IServiceProvider services)
			where TDependency : class
		{
			return services.GetService(typeof(TDependency)) as TDependency
					?? throw new InvalidOperationException($"Cannot resolve required service {typeof(TDependency)}");
		}

		private static TDependency? ResolveOptional<TDependency>(IServiceProvider services, object? defaultValue)
			where TDependency : class
		{
			var tmp = services.GetService(typeof(TDependency)) ?? defaultValue;

			return tmp switch
			{
				null => null,
				TDependency retval => retval,
				_ => throw new InvalidOperationException(
					$"Optional parameter of type {typeof(TDependency)} has a default value " +
					$"of type {tmp!.GetType()} which cannot be used as {typeof(TDependency)}")
			};
		}
	}
}

public interface IWiring
{
	void Add<TService, TImplementation>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService;
	void Add<TService>(Lifecycle lifecycle = Lifecycle.Transient) where TService : class;
	void Add<TService>(Func<IServiceProvider, TService> resolver, Lifecycle lifecycle = Lifecycle.Transient) where TService : class;
	void Add<TService>(TService instance) where TService : class;
	void AutoWire<TService, TImplementation, TArg1, TArg2, TArg3, TArg4>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService;
	void AutoWire<TService, TImplementation, TArg1, TArg2, TArg3>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService;
	void AutoWire<TService, TImplementation, TArg1, TArg2>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService;
	void AutoWire<TService, TImplementation, TArg1>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService;
}

public enum Lifecycle { Singleton = 0, Transient = 1 };

public static class SPX
{
	extension(IServiceProvider sp)
	{
		public T? GetService<T>()
		{
			var tmp = sp.GetService(typeof(T));

			return tmp == null ? default : (T)tmp;
		}

		public T GetRequiredService<T>()
		{
			var tmp = sp.GetService(typeof(T));

			return tmp != null ? (T)tmp: throw new InvalidOperationException($"Service {typeof(T)} is not registered");
		}
	}
}