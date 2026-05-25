using System;

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
	public void Add_SecondRegistration_OverwritesFirst()
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
}
