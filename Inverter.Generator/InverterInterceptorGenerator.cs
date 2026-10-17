using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Enyim.Generators;

/// <summary>Rewrites <c>Add&lt;TService, TImplementation&gt;()</c>, <c>Add&lt;TService&gt;()</c> and <c>AutoWire&lt;...&gt;()</c>
/// calls on <c>Inverter</c>/<c>IWiring</c> into delegate registrations, so no reflection is needed to build the services.
/// Calls it cannot see a concrete type at (e.g. inside a generic method) keep using the reflection path.</summary>
/// <remarks>The consuming project must opt in with <c>&lt;InterceptorsNamespaces&gt;$(InterceptorsNamespaces);Enyim.SourceGeneration&lt;/InterceptorsNamespaces&gt;</c>.</remarks>
[Generator(LanguageNames.CSharp)]
public sealed class InverterInterceptorGenerator : IIncrementalGenerator
{
	public const string GeneratedNamespace = "Enyim.SourceGeneration";

	private const string InverterType = "global::Enyim.Inverter";
	private const string WiringType = "global::Enyim.IWiring";
	private const string LifecycleType = "global::Enyim.Lifecycle";
	private const string ServiceProviderType = "global::System.IServiceProvider";

	private static readonly DiagnosticDescriptor NoConstructor = new(
		"INV001", "No usable constructor", "{0}", "Inverter", DiagnosticSeverity.Error, isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor AmbiguousConstructor = new(
		"INV002", "Ambiguous constructor", "{0}", "Inverter", DiagnosticSeverity.Warning, isEnabledByDefault: true);

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var calls = context.SyntaxProvider
			.CreateSyntaxProvider(static (node, _) => IsCandidate(node), static (ctx, ct) => Transform(ctx, ct))
			.Where(static call => call is not null)
			.Select(static (call, _) => call!);

		context.RegisterSourceOutput(calls.Collect(), static (spc, items) => Emit(spc, items));
	}

	private static bool IsCandidate(SyntaxNode node)
		=> node is InvocationExpressionSyntax invocation
			&& GetName(invocation) is GenericNameSyntax { Identifier.ValueText: "Add" or "AutoWire" };

	private static SimpleNameSyntax? GetName(InvocationExpressionSyntax invocation) => invocation.Expression switch
	{
		MemberAccessExpressionSyntax member => member.Name,
		MemberBindingExpressionSyntax binding => binding.Name,
		_ => null
	};

	private static Call? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
	{
		var invocation = (InvocationExpressionSyntax)ctx.Node;
		if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return null;

		var receiver = Display(method.ContainingType);
		if (receiver is not (InverterType or WiringType)) return null;

		// only the overloads that would build the service with reflection: (Lifecycle lifecycle)
		if (method.Parameters.Length != 1 || Display(method.Parameters[0].Type) != LifecycleType) return null;

		var typeArgs = method.TypeArguments;
		ITypeSymbol service, implementation;
		ImmutableArray<ITypeSymbol> openArgs;

		switch (method.Name)
		{
			case "Add" when typeArgs.Length is 1 or 2:
				service = typeArgs[0];
				implementation = typeArgs[typeArgs.Length - 1];
				openArgs = [];
				break;

			case "AutoWire" when typeArgs.Length >= 3:
				service = typeArgs[0];
				implementation = typeArgs[1];
				openArgs = typeArgs.RemoveRange(0, 2);
				break;

			default: return null;
		}

		var compilation = ctx.SemanticModel.Compilation;

		// left to the reflection path
		if (implementation is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } impl) return null;
		if (!IsUsable(service, compilation) || !IsUsable(impl, compilation) || openArgs.Any(a => !IsUsable(a, compilation))) return null;

		var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
		if (location == null) return null;

		var ctor = SelectConstructor(impl, openArgs, out var ambiguous);
		if (ctor == null)
		{
			var message = openArgs.IsEmpty
							? $"{Display(impl)} has no public constructor"
							: $"{Display(impl)} has no public constructor starting with ({string.Join(", ", openArgs.Select(Display))})";

			return new Call(Diagnostic: DiagnosticInfo.Create(NoConstructor.Id, message, invocation));
		}

		if (ctor.Parameters.Any(p => p.RefKind != RefKind.None || !IsUsable(p.Type, compilation))) return null;

		var arguments = ctor.Parameters.Select((p, i) => i < openArgs.Length ? $"arg_{i + 1}" : Resolve(p));
		var create = $"new {Display(impl)}({string.Join(", ", arguments)})";

		var registered = openArgs.IsEmpty
							? Display(service)
							: $"global::System.Func<{string.Join(", ", openArgs.Append(service).Select(Display))}>";

		var body = openArgs.IsEmpty
					? $"static sp => {create}"
					: $"static sp => ({string.Join(", ", openArgs.Select((_, i) => $"arg_{i + 1}"))}) => {create}";

		return new Call(
			Receiver: receiver,
			Registered: registered,
			Body: body,
			Attribute: location.GetInterceptsLocationAttributeSyntax(),
			Diagnostic: ambiguous
							? DiagnosticInfo.Create(AmbiguousConstructor.Id, $"{Display(impl)} has more than one public constructor with {ctor.Parameters.Length} parameters, the first one is used", invocation)
							: null);
	}

	// must match Inverter's GeneratedRegistration.BuildFactory and FuncFactory.GetFactory:
	// the public constructor with the most parameters (for AutoWire: whose leading parameters are the open arguments)
	private static IMethodSymbol? SelectConstructor(INamedTypeSymbol impl, ImmutableArray<ITypeSymbol> openArgs, out bool ambiguous)
	{
		var candidates = impl.InstanceConstructors
			.Where(c => c.DeclaredAccessibility == Accessibility.Public)
			.Where(c => c.Parameters.Length >= openArgs.Length
						&& openArgs.Select((a, i) => SymbolEqualityComparer.Default.Equals(a, c.Parameters[i].Type)).All(b => b))
			.ToList();

		ambiguous = false;
		if (candidates.Count == 0) return null;

		var max = candidates.Max(c => c.Parameters.Length);
		var best = candidates.Where(c => c.Parameters.Length == max).ToList();
		ambiguous = best.Count > 1;

		return best[0];
	}

	// must match Inverter's Helpers.Resolve
	private static string Resolve(IParameterSymbol p)
	{
		var type = Display(p.Type);
		if (type == ServiceProviderType) return "sp";
		if (!p.IsOptional) return $"Resolve.Required<{type}>(sp)";

		return $"Resolve.Optional<{type}>(sp, {DefaultValue(p, type)})";
	}

	private static string DefaultValue(IParameterSymbol p, string type)
	{
		if (!p.HasExplicitDefaultValue || p.ExplicitDefaultValue is not { } value) return $"default({type})";

		var literal = value switch
		{
			double d when double.IsNaN(d) => "double.NaN",
			double d when double.IsPositiveInfinity(d) => "double.PositiveInfinity",
			double d when double.IsNegativeInfinity(d) => "double.NegativeInfinity",
			float f when float.IsNaN(f) => "float.NaN",
			float f when float.IsPositiveInfinity(f) => "float.PositiveInfinity",
			float f when float.IsNegativeInfinity(f) => "float.NegativeInfinity",
			decimal => SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false) + "m",
			_ => SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)
		};

		// the cast covers enums (the value is the underlying number), nullables and narrower numeric types
		return $"({type})({literal})";
	}

	private static bool IsUsable(ITypeSymbol type, Compilation compilation) => type switch
	{
		ITypeParameterSymbol or IPointerTypeSymbol or IFunctionPointerTypeSymbol or IErrorTypeSymbol => false,
		IArrayTypeSymbol array => IsUsable(array.ElementType, compilation),
		INamedTypeSymbol named => compilation.IsSymbolAccessibleWithin(named, compilation.Assembly)
									&& named.TypeArguments.All(t => IsUsable(t, compilation)),
		_ => compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)
	};

	private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

	private static void Emit(SourceProductionContext spc, ImmutableArray<Call> calls)
	{
		foreach (var call in calls)
		{
			if (call.Diagnostic is { } d)
				spc.ReportDiagnostic(d.ToDiagnostic(d.Id == NoConstructor.Id ? NoConstructor : AmbiguousConstructor));
		}

		// identical registrations share one interceptor
		var groups = calls
			.Where(c => c.Body != null)
			.GroupBy(c => (c.Receiver, c.Registered, c.Body))
			.ToList();

		if (groups.Count == 0) return;

		var sb = new StringBuilder();
		sb.AppendLine($$"""
			// <auto-generated/>
			#nullable disable

			namespace System.Runtime.CompilerServices
			{
				[global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
				file sealed class InterceptsLocationAttribute : global::System.Attribute
				{
					public InterceptsLocationAttribute(int version, string data) { }
				}
			}

			namespace {{GeneratedNamespace}}
			{
				file static class InverterInterceptors
				{
			""");

		var index = 0;

		foreach (var group in groups)
		{
			foreach (var call in group)
				sb.Append("\t\t").AppendLine(call.Attribute);

			var (receiver, registered, body) = group.Key;

			sb.AppendLine($"\t\tpublic static void Intercept{index++}(this {receiver} self, {LifecycleType} lifecycle)");
			sb.AppendLine($"\t\t\t=> self.Add<{registered}>({body}, lifecycle);");
			sb.AppendLine();
		}

		sb.Append("""
				}

				file static class Resolve
				{
					public static T Required<T>(global::System.IServiceProvider services)
						=> services.GetService(typeof(T)) is T retval
							? retval
							: throw new global::System.InvalidOperationException($"Cannot resolve required service {typeof(T)}");

					public static T Optional<T>(global::System.IServiceProvider services, T defaultValue)
						=> services.GetService(typeof(T)) switch
						{
							null => defaultValue,
							T retval => retval,
							var other => throw new global::System.InvalidOperationException($"Service registered as {typeof(T)} is a {other.GetType()}")
						};
				}
			}
			""");

		spc.AddSource("Inverter.Interceptors.g.cs", sb.ToString());
	}

	private sealed record Call(
		string? Receiver = null,
		string? Registered = null,
		string? Body = null,
		string? Attribute = null,
		DiagnosticInfo? Diagnostic = null);

	// Location is not equatable, so the pipeline carries its parts
	private sealed record DiagnosticInfo(string Id, string Message, string Path, TextSpan Span, LinePositionSpan LineSpan)
	{
		public static DiagnosticInfo Create(string id, string message, SyntaxNode node)
		{
			var location = node.GetLocation();

			return new(id, message, location.SourceTree?.FilePath ?? "", location.SourceSpan, location.GetLineSpan().Span);
		}

		public Diagnostic ToDiagnostic(DiagnosticDescriptor descriptor)
			=> Diagnostic.Create(descriptor, Location.Create(Path, Span, LineSpan), Message);
	}
}
