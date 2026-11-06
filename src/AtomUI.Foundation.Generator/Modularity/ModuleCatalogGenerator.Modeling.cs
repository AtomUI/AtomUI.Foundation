using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using AtomUI.Modularity.Generators.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AtomUI.Modularity.Generators;

public sealed partial class ModuleCatalogGenerator
{
    private const string ModuleAttributeName = "AtomUI.Modularity.ModuleAttribute";
    private const string ModuleSerializedIdBoundaryAttributeName = "AtomUI.Modularity.ModuleSerializedIdBoundaryAttribute";
    private const string ModuleBaseName = "AtomUI.Modularity.ModuleBase";
    private const string ModuleInterfaceName = "AtomUI.Modularity.IModule";
    private static readonly string[] SerializedIdTypeNames =
    [
        "AtomUI.Modularity.ModuleId",
        "AtomUI.Modularity.ModulePhaseId"
    ];

    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Assembly/alias index of a compilation's references, built once per compilation instead of once
    /// per dependency. Weakly keyed, so it never extends a compilation's lifetime.
    /// </summary>
    private static readonly ConditionalWeakTable<Compilation, ReferenceIndex> ReferenceIndexes = new();

    private sealed class ReferenceIndex
    {
        public ReferenceIndex(Compilation compilation)
        {
            References = compilation.References.Select(reference => (
                    Assembly: compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol,
                    Aliases: reference.Properties.Aliases.IsDefaultOrEmpty
                        ? ImmutableArray.Create("global") : reference.Properties.Aliases))
                .Where(reference => reference.Assembly is not null)
                .Select(reference => (reference.Assembly!, reference.Aliases))
                .ToArray();
            AvailableAliases = References
                .SelectMany(reference => reference.Aliases).Append("global")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(alias => alias == "global" ? 0 : 1)
                .ThenBy(alias => alias, StringComparer.Ordinal)
                .ToArray();
        }

        public (IAssemblySymbol Assembly, ImmutableArray<string> Aliases)[] References { get; }
        public string[] AvailableAliases { get; }
    }

    private static ModuleModel? CreateModuleModel(
        GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Node is not TypeDeclarationSyntax typeDeclaration
            || context.SemanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            return null;
        }

        var attributes = typeSymbol.GetAttributes();
        var moduleAttribute = attributes.FirstOrDefault(static attribute => IsAttribute(attribute, ModuleAttributeName));
        if (moduleAttribute is null)
        {
            return null;
        }

        var compilation = context.SemanticModel.Compilation;
        var dependencies = new List<ModuleDependencyModel>();
        var attributeLocation = LocationInfo.From(moduleAttribute.ApplicationSyntaxReference, cancellationToken);
        foreach (var dependency in GetDependencyValues(moduleAttribute, "Dependencies"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependencyType = dependency.Value as INamedTypeSymbol;
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            var codeTypeName = dependencyType is null ? null : CreateTypeReference(dependencyType, compilation, aliases);
            dependencies.Add(new ModuleDependencyModel(
                dependencyType is null ? null : SymbolKey(dependencyType),
                (dependency.Value as ITypeSymbol)?.ToDisplayString(TypeNameFormat) ?? "null",
                codeTypeName,
                ToEquatable(aliases.OrderBy(alias => alias, StringComparer.Ordinal)),
                dependencyType is not null && IsModule(dependencyType),
                dependencyType is not null && HasModuleAttribute(dependencyType),
                dependencyType is not null && HasOpenTypeParameters(dependencyType),
                dependencyType?.ContainingAssembly.Identity.ToString(),
                attributeLocation));
        }

        var typeName = typeSymbol.ToDisplayString(TypeNameFormat);
        var id = CreateModuleId(typeSymbol.ContainingAssembly.Name, typeName);
        var displayName = GetNamedString(moduleAttribute, "DisplayName", InferDisplayName(typeSymbol.Name));

        return new ModuleModel(
            SymbolKey(typeSymbol),
            id,
            displayName,
            typeName,
            typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            attributeLocation,
            LocationInfo.From(typeDeclaration.Identifier.GetLocation()),
            IsModule(typeSymbol),
            GetUnsupportedShape(typeSymbol, compilation),
            HasAccessibleParameterlessConstructor(typeSymbol),
            SatisfiesRequiredMembers(typeSymbol),
            ToEquatable(dependencies
                .OrderBy(dependency => dependency.TypeName, StringComparer.Ordinal)
                .ThenBy(dependency => dependency.AssemblyIdentity, StringComparer.Ordinal)));
    }

    /// <summary>Symbol identity that survives recompilation: containing assembly plus documentation id.</summary>
    private static string SymbolKey(INamedTypeSymbol type) =>
        type.ContainingAssembly.Identity + "|" + (type.GetDocumentationCommentId() ?? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    private static ModuleGeneratorDiagnostic? CreateSerializedModuleIdDiagnostic(
        GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Node is not InvocationExpressionSyntax invocation)
        {
            return null;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            !string.Equals(method.Name, "FromSerializedValue", StringComparison.Ordinal) ||
            !SerializedIdTypeNames.Contains(
                method.ContainingType.ToDisplayString(TypeNameFormat),
                StringComparer.Ordinal))
        {
            return null;
        }

        if (IsInsideSerializedIdBoundary(context.SemanticModel, invocation, cancellationToken))
        {
            return null;
        }

        var argumentValue = invocation.ArgumentList.Arguments.Count > 0
            ? context.SemanticModel
                .GetConstantValue(invocation.ArgumentList.Arguments[0].Expression, cancellationToken)
                .Value as string
            : null;
        return new ModuleGeneratorDiagnostic(
            ModuleGeneratorDiagnostics.SerializedModuleIdUsage,
            LocationInfo.From(invocation.GetLocation()),
            argumentValue ?? "<non-constant>");
    }

    private static bool IsInsideSerializedIdBoundary(
        SemanticModel semanticModel,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        for (var symbol = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
             symbol is not null;
             symbol = symbol.ContainingSymbol)
        {
            if (symbol.GetAttributes().Any(static attribute => IsAttribute(attribute, ModuleSerializedIdBoundaryAttributeName)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOpenTypeParameters(INamedTypeSymbol type)
    {
        if (type.IsUnboundGenericType || type.TypeArguments.Any(ContainsTypeParameter))
        {
            return true;
        }

        return type.ContainingType is not null && HasOpenTypeParameters(type.ContainingType);
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        return type.TypeKind == TypeKind.TypeParameter ||
               type is INamedTypeSymbol named && HasOpenTypeParameters(named);
    }

    private static string GetNamedString(AttributeData attribute, string name, string defaultValue)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, name, StringComparison.Ordinal) &&
                argument.Value.Value is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static ImmutableArray<TypedConstant> GetDependencyValues(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (!string.Equals(argument.Key, name, StringComparison.Ordinal))
            {
                continue;
            }

            // A null array has a default Values array; individual null/unsupported
            // entries must survive normalization so validation can report all of them.
            if (argument.Value.Kind != TypedConstantKind.Array)
            {
                return ImmutableArray.Create(argument.Value);
            }

            return argument.Value.IsNull || argument.Value.Values.IsDefault
                ? ImmutableArray<TypedConstant>.Empty
                : argument.Value.Values;
        }

        return ImmutableArray<TypedConstant>.Empty;
    }

    private static string? CreateTypeReference(ITypeSymbol type, Compilation compilation, ISet<string> aliases)
    {
        if (type is IArrayTypeSymbol)
        {
            // C# writes array rank specifiers from the outermost array inward.
            // Appending each rank after recursively formatting its element reverses
            // mixed-rank arrays, producing a different (but still legal) type.
            var ranks = new StringBuilder();
            while (type is IArrayTypeSymbol array)
            {
                ranks.Append('[').Append(',', array.Rank - 1).Append(']');
                type = array.ElementType;
            }

            var element = CreateTypeReference(type, compilation, aliases);
            return element is null ? null : element + ranks;
        }

        if (type is not INamedTypeSymbol named || HasOpenTypeParameters(named))
        {
            return null;
        }

        var definition = named.OriginalDefinition;
        var metadataName = definition.MetadataName;
        for (var containing = definition.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            metadataName = containing.MetadataName + "+" + metadataName;
        }

        // Metadata names contain raw identifiers, while emitted C# names escape
        // keywords. Never use a source display string for a metadata lookup.
        for (var containingNamespace = definition.ContainingNamespace;
             !containingNamespace.IsGlobalNamespace;
             containingNamespace = containingNamespace.ContainingNamespace)
        {
            metadataName = containingNamespace.MetadataName + "." + metadataName;
        }

        var index = ReferenceIndexes.GetValue(compilation, static compilation => new ReferenceIndex(compilation));
        string? selectedAlias = null;
        foreach (var alias in index.AvailableAliases)
        {
            // C# prefers a source type to referenced types with the same full name.
            var sourceType = alias == "global" ? compilation.Assembly.GetTypeByMetadataName(metadataName) : null;
            var candidates = index.References.Where(reference => reference.Aliases.Contains(alias))
                .Select(reference => reference.Assembly.GetTypeByMetadataName(metadataName)
                    ?? reference.Assembly.ResolveForwardedType(metadataName))
                .Where(symbol => symbol is not null)
                .Distinct(SymbolEqualityComparer.Default).ToArray();
            if (sourceType is not null
                ? SymbolEqualityComparer.Default.Equals(sourceType, definition)
                : candidates.Length == 1 && SymbolEqualityComparer.Default.Equals(candidates[0], definition))
            {
                selectedAlias = alias;
                break;
            }
        }

        if (selectedAlias is null)
        {
            return null;
        }

        string prefix;
        if (named.ContainingType is not null)
        {
            var containing = CreateTypeReference(named.ContainingType, compilation, aliases);
            if (containing is null)
            {
                return null;
            }

            prefix = containing + ".";
        }
        else
        {
            if (selectedAlias != "global")
            {
                aliases.Add(selectedAlias);
            }

            prefix = EscapeIdentifier(selectedAlias) + "::";
            if (!named.ContainingNamespace.IsGlobalNamespace)
            {
                prefix += named.ContainingNamespace.ToDisplayString(TypeNameFormat) + ".";
            }
        }

        var arguments = named.TypeArguments.Select(argument => CreateTypeReference(argument, compilation, aliases)).ToArray();
        return arguments.Any(argument => argument is null) ? null : prefix + EscapeIdentifier(named.Name)
            + (arguments.Length == 0 ? string.Empty : "<" + string.Join(", ", arguments) + ">");
    }

    private static string EscapeIdentifier(string identifier)
    {
        return identifier != "global" && (SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
                SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None)
            ? "@" + identifier : identifier;
    }

    private static bool IsAttribute(AttributeData attribute, string attributeName)
    {
        return string.Equals(
            attribute.AttributeClass?.ToDisplayString(TypeNameFormat),
            attributeName,
            StringComparison.Ordinal);
    }

    private static bool IsModule(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.ToDisplayString(TypeNameFormat), ModuleBaseName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return type.AllInterfaces.Any(
            @interface => string.Equals(
                @interface.ToDisplayString(TypeNameFormat),
                ModuleInterfaceName,
                StringComparison.Ordinal));
    }

    private static bool HasModuleAttribute(INamedTypeSymbol type)
    {
        return type.GetAttributes().Any(static attribute => IsAttribute(attribute, ModuleAttributeName));
    }

    private static string CreateModuleId(string assemblyName, string typeName)
    {
        return $"{assemblyName}/{typeName}";
    }

    private static string? GetUnsupportedShape(INamedTypeSymbol type, Compilation compilation)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
        {
            return "the type must be a non-abstract class";
        }

        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity != 0)
            {
                return "generic module types and generic containing types are unsupported";
            }

            if (current.IsFileLocal)
            {
                return "file-local module types and file-local containing types cannot be referenced from the catalog";
            }
        }

        return compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)
            ? null
            : "the type and all containing types must be accessible from the generated catalog";
    }

    private static bool SatisfiesRequiredMembers(INamedTypeSymbol type)
    {
        var constructor = type.InstanceConstructors.FirstOrDefault(static constructor =>
            constructor.Parameters.Length == 0 && IsCatalogAccessible(constructor.DeclaredAccessibility));
        if (constructor is null || constructor.GetAttributes().Any(static attribute =>
                IsAttribute(attribute, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")))
        {
            return true;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetMembers().Any(static member =>
                    member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCatalogAccessible(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }

    private static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type)
    {
        return type is { IsAbstract: false, TypeKind: TypeKind.Class } &&
               type.InstanceConstructors.Any(static constructor =>
                   constructor.Parameters.Length == 0 &&
                   IsCatalogAccessible(constructor.DeclaredAccessibility));
    }

    /// <summary>Removes a trailing <c>Module</c> suffix and splits camel case. No product-specific prefixes.</summary>
    private static string InferDisplayName(string moduleTypeName)
    {
        var name = moduleTypeName;
        if (name.EndsWith("Module", StringComparison.Ordinal) && name.Length > "Module".Length)
        {
            name = name.Substring(0, name.Length - "Module".Length);
        }

        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (i > 0 &&
                char.IsUpper(current) &&
                (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1])))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
