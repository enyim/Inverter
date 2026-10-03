using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Enyim;

using Xunit;

public class InverterTests
{
	interface IAlpha { }
	interface IBeta { }

	class Alpha : IAlpha { }
	class Beta : IBeta { }

	class AlphaWithRequired : IAlpha
	{
		public IBeta Dep { get; }
		public AlphaWithRequired(IBeta dep) => Dep = dep;
	}

	class AlphaWithOptional : IAlpha
	{
		public IBeta? Dep { get; }
		public string Tag { get; }
		public AlphaWithOptional(IBeta? dep = null, string tag = "default") { Dep = dep; Tag = tag; }
	}

	class DisposableAlpha : IAlpha, IDisposable
	{
		public bool Disposed { get; private set; }
		public void Dispose() => Disposed = true;
	}

	class AsyncDisposableAlpha : IAlpha, IAsyncDisposable
	{
		public bool Disposed { get; private set; }
		public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
	}

	class DualDisposableAlpha : IAlpha, IDisposable, IAsyncDisposable
	{
		public bool SyncDisposed { get; private set; }
		public bool AsyncDisposed { get; private set; }
		public void Dispose() => SyncDisposed = true;
		public ValueTask DisposeAsync() { AsyncDisposed = true; return ValueTask.CompletedTask; }
	}

	class FactoryAlpha : IAlpha
	{
		public int Id { get; }
		public IBeta Dep { get; }
		public FactoryAlpha(int id, IBeta dep) { Id = id; Dep = dep; }
	}

	class TwoArgAlpha : IAlpha
	{
		public int Id { get; }
		public string Name { get; }
		public TwoArgAlpha(int id, string name) { Id = id; Name = name; }
	}

	interface IRepository<T> { }

	class Repository<T> : IRepository<T> { }

	class DisposableRepository<T> : IRepository<T>, IDisposable
	{
		public bool Disposed { get; private set; }
		public void Dispose() => Disposed = true;
	}

	class AsyncDisposableRepository<T> : IRepository<T>, IAsyncDisposable
	{
		public bool Disposed { get; private set; }
		public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
	}

	class AlphaWithOptionalValue : IAlpha
	{
		public int Count { get; }
		public AlphaWithOptionalValue(int count = 7) => Count = count;
	}

	class BetaWithAlphas : IBeta
	{
		public IAlpha[] Alphas { get; }
		public BetaWithAlphas(IEnumerable<IAlpha> alphas) => Alphas = [.. alphas];
	}

	static IServiceProvider Build(Action<Inverter> configure)
	{
		var inv = new Inverter();
		configure(inv);
		return inv.Build();
	}

	[Fact]
	public void Resolve_RegisteredImplementation_ReturnsCorrectType()
	{
		var sp = Build(i => i.Add<IAlpha, Alpha>());
		Assert.IsType<Alpha>(sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Resolve_UnregisteredService_ReturnsNull()
	{
		var sp = new Inverter().Build();
		Assert.Null(sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Resolve_DelegateRegistration_UsesDelegate()
	{
		var expected = new Alpha();
		var sp = Build(i => i.Add<IAlpha>(_ => expected));
		Assert.Same(expected, sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Resolve_InstanceRegistration_AlwaysReturnsSameInstance()
	{
		var instance = new Alpha();
		var sp = Build(i => i.Add<IAlpha>(instance));

		Assert.Same(instance, sp.GetService(typeof(IAlpha)));
		Assert.Same(instance, sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Resolve_SelfRegistration_ReturnsImplementation()
	{
		var sp = Build(i => i.Add<Alpha>());
		Assert.IsType<Alpha>(sp.GetService(typeof(Alpha)));
	}

	[Fact]
	public void Lifecycle_Singleton_ReturnsSameInstance()
	{
		var sp = Build(i => i.Add<IAlpha, Alpha>(Lifecycle.Singleton));
		Assert.Same(sp.GetService(typeof(IAlpha)), sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Lifecycle_Transient_ReturnsDifferentInstances()
	{
		var sp = Build(i => i.Add<IAlpha, Alpha>(Lifecycle.Transient));
		Assert.NotSame(sp.GetService(typeof(IAlpha)), sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Resolve_RequiredDependency_InjectedFromContainer()
	{
		var sp = Build(i =>
		{
			i.Add<IBeta, Beta>();
			i.Add<IAlpha, AlphaWithRequired>();
		});

		var result = Assert.IsType<AlphaWithRequired>(sp.GetService(typeof(IAlpha)));
		Assert.IsType<Beta>(result.Dep);
	}

	[Fact]
	public void Resolve_OptionalDependency_UsesDefaultWhenNotRegistered()
	{
		var sp = Build(i => i.Add<IAlpha, AlphaWithOptional>());

		var result = Assert.IsType<AlphaWithOptional>(sp.GetService(typeof(IAlpha)));
		Assert.Null(result.Dep);
		Assert.Equal("default", result.Tag);
	}

	[Fact]
	public void Resolve_RequiredDependencyMissing_ThrowsInvalidOperation()
	{
		var sp = Build(i => i.Add<IAlpha, AlphaWithRequired>()); // IBeta not registered
		Assert.Throws<InvalidOperationException>(() => sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Build_SnapshotIsolation_RegistrationAfterBuildNotVisible()
	{
		var inverter = new Inverter();
		var sp = inverter.Build();
		inverter.Add<IAlpha, Alpha>(); // registered after Build()

		Assert.Null(sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Add_SecondRegistration_LatestWins()
	{
		var sp = Build(i =>
		{
			i.Add<IAlpha, Alpha>();
			i.Add<IAlpha, AlphaWithOptional>();
		});

		Assert.IsType<AlphaWithOptional>(sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Dispose_SingletonIDisposable_IsDisposed()
	{
		var sp = Build(i => i.Add<IAlpha, DisposableAlpha>(Lifecycle.Singleton));
		var instance = (DisposableAlpha)sp.GetService(typeof(IAlpha))!;

		((IDisposable)sp).Dispose();

		Assert.True(instance.Disposed);
	}

	[Fact]
	public void Dispose_TransientIDisposable_IsNotDisposedByContainer()
	{
		var sp = Build(i => i.Add<IAlpha, DisposableAlpha>(Lifecycle.Transient));
		var instance = (DisposableAlpha)sp.GetService(typeof(IAlpha))!;

		((IDisposable)sp).Dispose();

		Assert.False(instance.Disposed); // container doesn't track transient lifetimes
	}

	[Fact]
	public void GetService_AfterDispose_ThrowsObjectDisposed()
	{
		var sp = new Inverter().Build();
		((IDisposable)sp).Dispose();

		Assert.Throws<ObjectDisposedException>(() => sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void Add_NullDelegate_ThrowsArgumentNull()
	{
		var inverter = new Inverter();
		Assert.Throws<ArgumentNullException>(() => inverter.Add<IAlpha>((Func<IServiceProvider, IAlpha>)null!));
	}

	[Fact]
	public void Add_NullInstance_ThrowsArgumentNull()
	{
		var inverter = new Inverter();
		Assert.Throws<ArgumentNullException>(() => inverter.Add<IAlpha>((IAlpha)null!));
	}

	[Fact]
	public void AutoWire_OneArg_FactoryInjectsContainerDependencies()
	{
		var sp = Build(i =>
		{
			i.Add<IBeta, Beta>();
			i.AutoWire<IAlpha, FactoryAlpha, int>();
		});

		var factory = (Func<int, IAlpha>)sp.GetService(typeof(Func<int, IAlpha>))!;
		var result = Assert.IsType<FactoryAlpha>(factory(42));
		Assert.Equal(42, result.Id);
		Assert.IsType<Beta>(result.Dep);
	}

	[Fact]
	public void AutoWire_TwoArgs_FactoryPassesBothArgs()
	{
		var sp = Build(i => i.AutoWire<IAlpha, TwoArgAlpha, int, string>());

		var factory = (Func<int, string, IAlpha>)sp.GetService(typeof(Func<int, string, IAlpha>))!;
		var result = Assert.IsType<TwoArgAlpha>(factory(7, "hello"));
		Assert.Equal(7, result.Id);
		Assert.Equal("hello", result.Name);
	}

	[Fact]
	public async Task AsyncDispose_SingletonIAsyncDisposable_IsDisposed()
	{
		var sp = Build(i => i.Add<IAlpha, AsyncDisposableAlpha>(Lifecycle.Singleton));
		var instance = (AsyncDisposableAlpha)sp.GetService(typeof(IAlpha))!;

		await ((IAsyncDisposable)sp).DisposeAsync();

		Assert.True(instance.Disposed);
	}

	[Fact]
	public async Task AsyncDispose_SingletonSyncDisposableOnly_FallsBackToSyncDispose()
	{
		var sp = Build(i => i.Add<IAlpha, DisposableAlpha>(Lifecycle.Singleton));
		var instance = (DisposableAlpha)sp.GetService(typeof(IAlpha))!;

		await ((IAsyncDisposable)sp).DisposeAsync();

		Assert.True(instance.Disposed);
	}

	[Fact]
	public async Task AsyncDispose_DualDisposable_PrefersAsyncPath()
	{
		var sp = Build(i => i.Add<IAlpha, DualDisposableAlpha>(Lifecycle.Singleton));
		var instance = (DualDisposableAlpha)sp.GetService(typeof(IAlpha))!;

		await ((IAsyncDisposable)sp).DisposeAsync();

		Assert.True(instance.AsyncDisposed);
		Assert.False(instance.SyncDisposed);
	}

	[Fact]
	public async Task AsyncDispose_InstanceRegistration_IsNotDisposed()
	{
		var alpha = new DualDisposableAlpha();
		var sp = Build(i => i.Add<IAlpha>(alpha));

		await ((IAsyncDisposable)sp).DisposeAsync();

		Assert.False(alpha.AsyncDisposed);
		Assert.False(alpha.SyncDisposed);
	}

	[Fact]
	public async Task GetService_AfterAsyncDispose_ThrowsObjectDisposed()
	{
		var sp = new Inverter().Build();
		await ((IAsyncDisposable)sp).DisposeAsync();

		Assert.Throws<ObjectDisposedException>(() => sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void OpenGeneric_Transient_ResolvesClosedTypeAndIsDistinctPerCall()
	{
		var sp = Build(i => i.AddOpenGeneric(typeof(IRepository<>), typeof(Repository<>)));

		var first = Assert.IsType<Repository<Alpha>>(sp.GetService(typeof(IRepository<Alpha>)));
		var second = Assert.IsType<Repository<Alpha>>(sp.GetService(typeof(IRepository<Alpha>)));
		Assert.NotSame(first, second);

		Assert.IsType<Repository<Beta>>(sp.GetService(typeof(IRepository<Beta>)));
	}

	[Fact]
	public void OpenGeneric_Singleton_ReturnsSameInstancePerConstructedType()
	{
		var sp = Build(i => i.AddOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifecycle.Singleton));

		Assert.Same(sp.GetService(typeof(IRepository<Alpha>)), sp.GetService(typeof(IRepository<Alpha>)));
		Assert.NotSame(sp.GetService(typeof(IRepository<Alpha>)), sp.GetService(typeof(IRepository<Beta>)));
	}

	[Fact]
	public void OpenGeneric_BareDefinition_ResolvesToNull()
	{
		var sp = Build(i => i.AddOpenGeneric(typeof(IRepository<>), typeof(Repository<>)));

		Assert.Null(sp.GetService(typeof(IRepository<>)));
	}

	[Fact]
	public void Dispose_SingletonOpenGeneric_IsDisposed()
	{
		var sp = Build(i => i.AddOpenGeneric(typeof(IRepository<>), typeof(DisposableRepository<>), Lifecycle.Singleton));
		var instance = (DisposableRepository<Alpha>)sp.GetService(typeof(IRepository<Alpha>))!;

		((IDisposable)sp).Dispose();

		Assert.True(instance.Disposed);
	}

	[Fact]
	public async Task AsyncDispose_SingletonOpenGeneric_IsDisposed()
	{
		var sp = Build(i => i.AddOpenGeneric(typeof(IRepository<>), typeof(AsyncDisposableRepository<>), Lifecycle.Singleton));
		var instance = (AsyncDisposableRepository<Alpha>)sp.GetService(typeof(IRepository<Alpha>))!;

		await ((IAsyncDisposable)sp).DisposeAsync();

		Assert.True(instance.Disposed);
	}

	[Fact]
	public void ResolveEnumerable_MultipleRegistrations_ReturnsAll()
	{
		var sp = Build(i =>
		{
			i.Add<IAlpha, Alpha>();
			i.Add<IAlpha, AlphaWithOptional>();
		});

		var result = (System.Collections.Generic.IEnumerable<IAlpha>)sp.GetService(typeof(System.Collections.Generic.IEnumerable<IAlpha>))!;
		Assert.Collection(result,
			x => Assert.IsType<Alpha>(x),
			x => Assert.IsType<AlphaWithOptional>(x));
	}

	[Fact]
	public void ResolveEnumerable_NoneRegistered_ReturnsEmptyArray()
	{
		var sp = new Inverter().Build();

		var result = sp.GetService(typeof(System.Collections.Generic.IEnumerable<IAlpha>));
		Assert.NotNull(result);
		Assert.Empty((System.Collections.IEnumerable)result);
	}

	[Fact]
	public void ResolveEnumerable_ExplicitRegistrationWins()
	{
		var explicit_ = new Alpha();
		var sp = Build(i =>
		{
			i.Add<IAlpha, AlphaWithOptional>();
			i.Add<System.Collections.Generic.IEnumerable<IAlpha>>(_ => [explicit_]);
		});

		var result = (System.Collections.Generic.IEnumerable<IAlpha>)sp.GetService(typeof(System.Collections.Generic.IEnumerable<IAlpha>))!;
		Assert.Same(explicit_, Assert.Single(result));
	}

	[Fact]
	public void ResolveEnumerable_SingleRegistration_ReturnsOneElement()
	{
		var sp = Build(i => i.Add<IAlpha, Alpha>());

		var result = (System.Collections.Generic.IEnumerable<IAlpha>)sp.GetService(typeof(System.Collections.Generic.IEnumerable<IAlpha>))!;
		Assert.IsType<Alpha>(Assert.Single(result));
	}

	[Fact]
	public void ResolveEnumerable_ConstructorParameter_InjectsAll()
	{
		var sp = Build(i =>
		{
			i.Add<IAlpha, Alpha>();
			i.Add<IAlpha, AlphaWithOptionalValue>();
			i.Add<IBeta, BetaWithAlphas>();
		});

		var result = Assert.IsType<BetaWithAlphas>(sp.GetService(typeof(IBeta)));
		Assert.Collection(result.Alphas,
			x => Assert.IsType<Alpha>(x),
			x => Assert.IsType<AlphaWithOptionalValue>(x));
	}

	[Fact]
	public void ResolveEnumerable_MultipleInstances_ReturnsAllInOrder()
	{
		var first = new Alpha();
		var second = new Alpha();
		var sp = Build(i =>
		{
			i.Add<IAlpha>(first);
			i.Add<IAlpha>(second);
		});

		var result = (IEnumerable<IAlpha>)sp.GetService(typeof(IEnumerable<IAlpha>))!;
		Assert.Collection(result,
			x => Assert.Same(first, x),
			x => Assert.Same(second, x));
		Assert.Same(second, sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void ResolveEnumerable_Singleton_SameInstanceAsSingleResolve()
	{
		var sp = Build(i =>
		{
			i.Add<IAlpha, Alpha>(Lifecycle.Singleton);
			i.Add<IAlpha, AlphaWithOptional>(Lifecycle.Singleton);
		});

		var single = sp.GetService(typeof(IAlpha));
		var first = (IAlpha[])sp.GetService(typeof(IEnumerable<IAlpha>))!;
		var second = (IAlpha[])sp.GetService(typeof(IEnumerable<IAlpha>))!;

		Assert.Same(single, first[1]);
		Assert.Same(first[0], second[0]);
		Assert.Same(first[1], second[1]);
	}

	[Fact]
	public void ResolveEnumerable_ExplicitRegisteredFirst_StillWins()
	{
		var explicit_ = new Alpha();
		var sp = Build(i =>
		{
			i.Add<IEnumerable<IAlpha>>(_ => [explicit_]);
			i.Add<IAlpha, AlphaWithOptional>();
		});

		var result = (IEnumerable<IAlpha>)sp.GetService(typeof(IEnumerable<IAlpha>))!;
		Assert.Same(explicit_, Assert.Single(result));
	}

	[Fact]
	public void ResolveEnumerable_TwoExplicitRegistrations_LatestWins()
	{
		var first = new Alpha();
		var second = new Alpha();
		var sp = Build(i =>
		{
			i.Add<IEnumerable<IAlpha>>(_ => [first]);
			i.Add<IEnumerable<IAlpha>>(_ => [second]);
		});

		var result = (IEnumerable<IAlpha>)sp.GetService(typeof(IEnumerable<IAlpha>))!;
		Assert.Same(second, Assert.Single(result));
	}

	[Fact]
	public void ResolveEnumerable_RegisteredAfterBuild_NotVisible()
	{
		var inverter = new Inverter();
		inverter.Add<IAlpha, Alpha>();
		var sp = inverter.Build();
		inverter.Add<IAlpha, AlphaWithOptional>(); // registered after Build()

		var result = (IEnumerable<IAlpha>)sp.GetService(typeof(IEnumerable<IAlpha>))!;
		Assert.IsType<Alpha>(Assert.Single(result));
	}

	[Fact]
	public void ResolveEnumerable_OpenGenericDefinition_ReturnsNull()
	{
		var sp = Build(i => i.Add<IAlpha, Alpha>());
		Assert.Null(sp.GetService(typeof(IEnumerable<>)));
	}

	[Fact]
	public void Dispose_MultipleSingletonRegistrations_AllDisposed()
	{
		var sp = Build(i =>
		{
			i.Add<IAlpha, DisposableAlpha>(Lifecycle.Singleton);
			i.Add<IAlpha, DisposableAlpha>(Lifecycle.Singleton);
		});
		var instances = (IAlpha[])sp.GetService(typeof(IEnumerable<IAlpha>))!;

		((IDisposable)sp).Dispose();

		Assert.NotSame(instances[0], instances[1]);
		Assert.All(instances, x => Assert.True(((DisposableAlpha)x).Disposed));
	}

	[Fact]
	public void Dispose_AsyncOnlySingleton_ThrowsButDisposesTheRest()
	{
		var sp = Build(i =>
		{
			i.Add<IAlpha, AsyncDisposableAlpha>(Lifecycle.Singleton);
			i.Add<IAlpha, DisposableAlpha>(Lifecycle.Singleton);
		});
		var instances = (IAlpha[])sp.GetService(typeof(IEnumerable<IAlpha>))!;

		Assert.Throws<InvalidOperationException>(() => ((IDisposable)sp).Dispose());

		Assert.False(((AsyncDisposableAlpha)instances[0]).Disposed);
		Assert.True(((DisposableAlpha)instances[1]).Disposed);
	}

	[Fact]
	public void Dispose_DualDisposableSingleton_UsesSyncDispose()
	{
		var sp = Build(i => i.Add<IAlpha, DualDisposableAlpha>(Lifecycle.Singleton));
		var instance = (DualDisposableAlpha)sp.GetService(typeof(IAlpha))!;

		((IDisposable)sp).Dispose();

		Assert.True(instance.SyncDisposed);
		Assert.False(instance.AsyncDisposed);
	}

	[Fact]
	public void Resolve_OptionalValueTypeParameter_UsesDefault()
	{
		var sp = Build(i => i.Add<IAlpha, AlphaWithOptionalValue>());

		var result = Assert.IsType<AlphaWithOptionalValue>(sp.GetService(typeof(IAlpha)));
		Assert.Equal(7, result.Count);
	}

	[Fact]
	public void Resolve_RequiredValueTypeParameter_ThrowsInvalidOperation()
	{
		var sp = Build(i => i.Add<IAlpha, TwoArgAlpha>()); // registering must not throw

		Assert.Throws<InvalidOperationException>(() => sp.GetService(typeof(IAlpha)));
	}

	[Fact]
	public void GetRequiredService_Unregistered_ThrowsInvalidOperation()
	{
		var sp = new Inverter().Build();

		Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IAlpha>());
	}
}
