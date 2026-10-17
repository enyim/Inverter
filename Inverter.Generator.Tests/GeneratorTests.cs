using System;
using System.IO;
using System.Linq;

using Enyim.Generators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Xunit;

public class GeneratorTests
{
	private static readonly MetadataReference[] References =
	[
		.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Select(path => MetadataReference.CreateFromFile(path)),
		MetadataReference.CreateFromFile(typeof(Enyim.Inverter).Assembly.Location)
	];

	private static (string? Generated, Diagnostic[] Diagnostics) Run(string source)
	{
		var options = new CSharpParseOptions(LanguageVersion.Preview)
			.WithFeatures([new("InterceptorsNamespaces", InverterInterceptorGenerator.GeneratedNamespace)]);

		var compilation = CSharpCompilation.Create(
			"Test",
			[CSharpSyntaxTree.ParseText(source, options, path: "Test.cs")],
			References,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		CSharpGeneratorDriver.Create(new InverterInterceptorGenerator())
			.WithUpdatedParseOptions(options)
			.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

		var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
		var generated = output.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("Inverter.Interceptors.g.cs"));

		return (generated?.ToString(), [.. generatorDiagnostics, .. errors]);
	}

	private const string Types = """
		using System;
		using Enyim;

		public interface IAlpha { }
		public interface IBeta { }
		public class Beta : IBeta { }
		public enum Mode { First, Second }
		""";

	[Fact]
	public void PicksConstructorWithMostParameters()
	{
		var (generated, diagnostics) = Run(Types + """
			public class Alpha : IAlpha
			{
				public Alpha() { }
				public Alpha(IBeta beta, IServiceProvider services) { }
			}

			public static class Setup { public static void Run(Inverter i) => i.Add<IAlpha, Alpha>(); }
			""");

		Assert.Empty(diagnostics);
		Assert.Contains("new global::Alpha(Resolve.Required<global::IBeta>(sp), sp)", generated);
	}

	[Fact]
	public void OptionalParameters_KeepTheirDefaults()
	{
		var (generated, diagnostics) = Run(Types + """
			public class Alpha : IAlpha
			{
				public Alpha(IBeta? beta = null, Mode mode = Mode.Second, int? count = 3, decimal rate = 1.5m, string name = "x") { }
			}

			public static class Setup { public static void Run(Inverter i) => i.Add<IAlpha, Alpha>(); }
			""");

		Assert.Empty(diagnostics);
		Assert.Contains("Resolve.Optional<global::IBeta>(sp, default(global::IBeta))", generated);
		Assert.Contains("Resolve.Optional<global::Mode>(sp, (global::Mode)(1))", generated);
		Assert.Contains("Resolve.Optional<int?>(sp, (int?)(3))", generated);
		Assert.Contains("Resolve.Optional<decimal>(sp, (decimal)(1.5m))", generated);
		Assert.Contains("Resolve.Optional<string>(sp, (string)(\"x\"))", generated);
	}

	[Fact]
	public void AutoWire_PassesLeadingArguments()
	{
		var (generated, diagnostics) = Run(Types + """
			public class Alpha : IAlpha
			{
				public Alpha(IBeta beta) { }
				public Alpha(int id, IBeta beta) { }
			}

			public static class Setup { public static void Run(Inverter i) => i.AutoWire<IAlpha, Alpha, int>(); }
			""");

		Assert.Empty(diagnostics);
		Assert.Contains("self.Add<global::System.Func<int, global::IAlpha>>(static sp => (arg_1) => new global::Alpha(arg_1, Resolve.Required<global::IBeta>(sp)), lifecycle)", generated);
	}

	[Fact]
	public void WiringReceiver_IsIntercepted()
	{
		var (generated, diagnostics) = Run(Types + """
			public static class Setup { public static void Run(IWiring w) => w.Add<IBeta, Beta>(Lifecycle.Singleton); }
			""");

		Assert.Empty(diagnostics);
		Assert.Contains("(this global::Enyim.IWiring self, global::Enyim.Lifecycle lifecycle)", generated);
	}

	[Fact]
	public void GenericCallSite_IsLeftToReflection()
	{
		var (generated, diagnostics) = Run(Types + """
			public static class Setup
			{
				public static void Run<T>(Inverter i) where T : class, IBeta => i.Add<IBeta, T>();
			}
			""");

		Assert.Empty(diagnostics);
		Assert.Null(generated);
	}

	[Fact]
	public void NoPublicConstructor_ReportsError()
	{
		var (_, diagnostics) = Run(Types + """
			public class Alpha : IAlpha { private Alpha() { } }

			public static class Setup { public static void Run(Inverter i) => i.Add<IAlpha, Alpha>(); }
			""");

		Assert.Equal("INV001", Assert.Single(diagnostics).Id);
	}

	[Fact]
	public void TiedConstructors_ReportWarning()
	{
		var (generated, diagnostics) = Run(Types + """
			public class Alpha : IAlpha
			{
				public Alpha(IBeta beta) { }
				public Alpha(Mode mode) { }
			}

			public static class Setup { public static void Run(Inverter i) => i.Add<IAlpha, Alpha>(); }
			""");

		Assert.Equal("INV002", Assert.Single(diagnostics).Id);
		Assert.Contains("new global::Alpha(Resolve.Required<global::IBeta>(sp))", generated);
	}
}
