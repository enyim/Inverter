using System;
using System.Linq.Expressions;

using Enyim;

// See https://aka.ms/new-console-template for more information

var inverter = new Inverter();

//inverter.Add<string>(_ => "lofasz");
inverter.Add<IService1, BasicImpl1>();
inverter.Add<IService2, BasicImpl2>();
//inverter.Add<IService3, BasicImpl3>();
inverter.Add<IService4, BasicImpl4>();

inverter.AutoWire<IService1, OpenImpl1, int>();

var sp = inverter.Build();

//inverter.Add<IService1, FancyImpl1>();
//var a = sp.GetService(typeof(IService1));

var a = sp.GetService<Func<int, IService1>>();
var aa = a(1234);


Console.WriteLine(a);
interface IService1 { }
interface IService2 { }
interface IService3 { }
interface IService4 { }

class BasicImpl1 : IService1 { }
class BasicImpl2 : IService2 { }
class BasicImpl3 : IService3 { }
class BasicImpl4 : IService4 { }

class FancyImpl1 : IService1
{
	public FancyImpl1(IService2 a, IService3 b = null, IService2? c = null, string name = "pina")
	{
	}
}

class OpenImpl1 : IService1
{
	public OpenImpl1(int counter, IService2 a, IService3 b = null, IService2? c = null, string name = "pina")
	{
	}
}

class HasDefaults
{
	public void A(string lofasz = null) { }
	public void B(bool pina = true, string? lofasz2 = null) { }
}

static class SPX
{
	public static T? GetService<T>(this IServiceProvider p)
		where T : class
		=> (T?)p.GetService(typeof(T));
}

