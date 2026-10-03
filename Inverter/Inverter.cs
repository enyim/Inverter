using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

using X = System.Linq.Expressions.Expression;

namespace Enyim;

public class Inverter : IWiring
{
	private readonly Dictionary<Type, List<Registration>> registrations = new();

	private void Register(Type serviceType, Registration registration)
	{
		if (!registrations.TryGetValue(serviceType, out var list))
			registrations[serviceType] = list = [];
		list.Add(registration);
	}

	public IServiceProvider Build()
	{
		var snapshot = registrations.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Select(r => r.Clone()).ToList());
		return new ServiceProviderImpl(snapshot);
	}

	public void AddOpenGeneric(Type serviceOpenGeneric, Type implementationOpenGeneric, Lifecycle lifecycle = Lifecycle.Transient)
	{
		ArgumentNullException.ThrowIfNull(serviceOpenGeneric);
		ArgumentNullException.ThrowIfNull(implementationOpenGeneric);

		if (!serviceOpenGeneric.IsGenericTypeDefinition)
			throw new ArgumentException($"{serviceOpenGeneric} is not an open generic type definition", nameof(serviceOpenGeneric));
		if (!implementationOpenGeneric.IsGenericTypeDefinition)
			throw new ArgumentException($"{implementationOpenGeneric} is not an open generic type definition", nameof(implementationOpenGeneric));

		Register(serviceOpenGeneric, new OpenGenericRegistration(implementationOpenGeneric, lifecycle));
	}

	public void Add<TService>(Func<IServiceProvider, TService> resolver, Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
	{
		ArgumentNullException.ThrowIfNull(resolver);

		Register(typeof(TService), new DelegateRegistration<TService>(resolver, lifecycle));
	}

	public void Add<TService, TImplementation>(Lifecycle lifecycle = Lifecycle.Transient)
		where TService : class
		where TImplementation : class, TService
	{
		Register(typeof(TService), new GeneratedRegistration<TService, TImplementation>(lifecycle));
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

		Register(typeof(TService), new InstanceRegistration<TService>(instance));
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
		Register(typeof(TFunc), new OpenArgFuncRegistration<TImplementation, TFunc>(lifecycle));
	}

	private class ServiceProviderImpl : IServiceProvider, IDisposable, IAsyncDisposable
	{
		private readonly FrozenDictionary<Type, List<Registration>> registrations;
		private readonly Dictionary<Type, List<Registration>> closedGenericCache = new();
		private bool disposed;

		public ServiceProviderImpl(FrozenDictionary<Type, List<Registration>> registrations)
		{
			this.registrations = registrations;
		}

		object? IServiceProvider.GetService(Type serviceType)
		{
			ArgumentNullException.ThrowIfNull(serviceType);
			if (disposed) throw new ObjectDisposedException(nameof(ServiceProviderImpl));

			if (Find(serviceType) is { } list)
				return list[^1].Create(this);

			if (serviceType.IsConstructedGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
			{
				var elementType = serviceType.GetGenericArguments()[0];
				if (Find(elementType) is { } elementList)
				{
					var array = Array.CreateInstance(elementType, elementList.Count);
					for (var i = 0; i < elementList.Count; i++)
						array.SetValue(elementList[i].Create(this), i);
					return array;
				}
				return Array.CreateInstance(elementType, 0);
			}

			return null;
		}

		// exact registrations win over the ones closed from an open generic
		private List<Registration>? Find(Type serviceType)
		{
			if (registrations.TryGetValue(serviceType, out var list))
				return list[^1] is OpenGenericRegistration ? null : list;

			if (serviceType.IsConstructedGenericType
				&& registrations.TryGetValue(serviceType.GetGenericTypeDefinition(), out var openList))
			{
				if (!closedGenericCache.TryGetValue(serviceType, out var closedList))
				{
					closedList = openList.OfType<OpenGenericRegistration>().Select(open => open.BuildClosed(serviceType)).ToList();
					closedGenericCache[serviceType] = closedList;
				}

				return closedList;
			}

			return null;
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;

			List<Exception>? errors = null;

			foreach (var reg in registrations.Values.Concat(closedGenericCache.Values).SelectMany(l => l))
			{
				try { reg.Dispose(); }
				catch (Exception e) { (errors ??= []).Add(e); }
			}

			ThrowIfAny(errors);
		}

		public async ValueTask DisposeAsync()
		{
			if (disposed) return;
			disposed = true;

			List<Exception>? errors = null;

			foreach (var reg in registrations.Values.Concat(closedGenericCache.Values).SelectMany(l => l))
			{
				try { await reg.DisposeAsync(); }
				catch (Exception e) { (errors ??= []).Add(e); }
			}

			ThrowIfAny(errors);
		}

		private static void ThrowIfAny(List<Exception>? errors)
		{
			if (errors is null) return;
			if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();

			throw new AggregateException(errors);
		}
	}

	private abstract class Registration : IDisposable, IAsyncDisposable
	{
		protected readonly Lifecycle lifecycle;
		private object? cachedInstance;

		protected Registration(Lifecycle lifecycle)
		{
			this.lifecycle = lifecycle;
		}

		public virtual void Dispose()
		{
			var instance = cachedInstance;
			cachedInstance = null;

			if (instance is IDisposable d)
			{
				d.Dispose();
			}
			else if (instance is IAsyncDisposable)
			{
				throw new InvalidOperationException($"instance of {instance.GetType()} implements {nameof(IAsyncDisposable)} please use {nameof(IAsyncDisposable.DisposeAsync)}");
			}
		}

		public virtual async ValueTask DisposeAsync()
		{
			var instance = cachedInstance;
			cachedInstance = null;

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

			return cachedInstance ??= CreateInstance(services);
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

	private sealed class OpenGenericRegistration : Registration
	{
		private readonly Type implType;

		public OpenGenericRegistration(Type implType, Lifecycle lifecycle) : base(lifecycle)
		{
			this.implType = implType;
		}

		public override Registration Clone() => new OpenGenericRegistration(implType, lifecycle);

		public Registration BuildClosed(Type constructedType) =>
			new DelegateRegistration<object>(GeneratedRegistration.BuildFactory(implType.MakeGenericType(constructedType.GenericTypeArguments)), lifecycle);

		protected override object CreateInstance(IServiceProvider services) =>
			throw new NotSupportedException("open generic registration is a template and cannot be resolved directly");
	}

	private static class GeneratedRegistration
	{
		public static Func<IServiceProvider, object> BuildFactory(Type implType)
		{
			var ctor = implType.GetConstructors().MaxBy(c => c.GetParameters().Length) ?? throw new InvalidOperationException($"{implType} has no accessible constructor");
			var services = X.Parameter(typeof(IServiceProvider), "services");

			var lambda = X.Lambda<Func<IServiceProvider, object>>(
				X.Convert(
					X.New(
						ctor,
						ctor.GetParameters()
							.Select<ParameterInfo, X>(p => (p.ParameterType == typeof(IServiceProvider))
															? services
															: p.IsOptional
																? X.Call(Helpers.ResolveOptionalMethod.MakeGenericMethod(p.ParameterType), services, X.Constant(p.HasDefaultValue ? p.DefaultValue : null, typeof(object)))
																: X.Call(Helpers.ResolveRequiredMethod.MakeGenericMethod(p.ParameterType), services)
						)
					),
					typeof(object)),
				services);

			return lambda.Compile();
		}
	}

	private class GeneratedRegistration<TService, TImplementation> : DelegateRegistration<TService>
		where TService : class
		where TImplementation : class
	{
		private static Func<IServiceProvider, TService> GetFactory()
		{
			var factory = GeneratedRegistration.BuildFactory(typeof(TImplementation));
			return services => (TService)factory(services);
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
																? X.Call(Helpers.ResolveOptionalMethod.MakeGenericMethod(p.ParameterType), services, X.Constant(p.HasDefaultValue ? p.DefaultValue : null, typeof(object)))
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
		{
			return services.GetService(typeof(TDependency)) is TDependency retval
					? retval
					: throw new InvalidOperationException($"Cannot resolve required service {typeof(TDependency)}");
		}

		private static TDependency? ResolveOptional<TDependency>(IServiceProvider services, object? defaultValue)
		{
			var tmp = services.GetService(typeof(TDependency)) ?? defaultValue;

			return tmp switch
			{
				null => default,
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
	void AddOpenGeneric(Type serviceOpenGeneric, Type implementationOpenGeneric, Lifecycle lifecycle = Lifecycle.Transient);
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

			return tmp != null ? (T)tmp : throw new InvalidOperationException($"Service {typeof(T)} is not registered");
		}
	}
}