using System.Collections.Immutable;
using System.Text;
using AtomUI.Modularity.Generators.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AtomUI.Modularity.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class ModuleCatalogGenerator : IIncrementalGenerator
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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                static (context, cancellationToken) => CreateModuleModel(context, cancellationToken))
            .Where(static module => module is not null)
            .Select(static (module, _) => module!);
        var serializedModuleIdUsages = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (context, cancellationToken) => CreateSerializedModuleIdDiagnostic(context, cancellationToken))
            .Where(static diagnostic => diagnostic is not null)
            .Select(static (diagnostic, _) => diagnostic!);

        var catalogOptions = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ModuleCatalogOptions.From(provider.GlobalOptions));

        context.RegisterSourceOutput(
            serializedModuleIdUsages,
            static (productionContext, diagnostic) => ReportDiagnostic(productionContext, diagnostic));
        context.RegisterSourceOutput(
            modules.Collect().Combine(catalogOptions),
            static (productionContext, input) => EmitCatalog(productionContext, input.Left, input.Right));
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

        var dependencies = ImmutableArray.CreateBuilder<ModuleDependencyModel>();

        AddDependencies(moduleAttribute, "Dependencies");

        void AddDependencies(AttributeData attribute, string propertyName)
        {
            foreach (var dependencyType in GetNamedTypeArray(attribute, propertyName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();
                dependencies.Add(new ModuleDependencyModel(
                    dependencyType,
                    dependencyType.ToDisplayString(TypeNameFormat),
                    IsModule(dependencyType),
                    HasModuleAttribute(dependencyType),
                    location));
            }
        }

        var typeName = typeSymbol.ToDisplayString(TypeNameFormat);
        var id = CreateModuleId(typeSymbol.ContainingAssembly.Name, typeName);
        var displayName = GetNamedString(moduleAttribute, "DisplayName", InferDisplayName(typeSymbol.Name));

        return new ModuleModel(
            typeSymbol,
            id,
            displayName,
            typeName,
            moduleAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation(),
            typeDeclaration.Identifier.GetLocation(),
            IsModule(typeSymbol),
            GetUnsupportedShape(typeSymbol, context.SemanticModel.Compilation),
            HasAccessibleParameterlessConstructor(typeSymbol),
            SatisfiesRequiredMembers(typeSymbol),
            dependencies
                .OrderBy(dependency => dependency.TypeName, StringComparer.Ordinal)
                .ToImmutableArray());
    }

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
            invocation.GetLocation(),
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

    private static void EmitCatalog(
        SourceProductionContext context,
        ImmutableArray<ModuleModel> modules,
        ModuleCatalogOptions options)
    {
        foreach (var diagnostic in options.Diagnostics)
        {
            ReportDiagnostic(context, diagnostic);
        }

        var moduleArray = modules
            .GroupBy(module => module.Symbol, SymbolEqualityComparer.Default)
            .Select(group => group.First())
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .ThenBy(module => module.TypeName, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = ValidateModules(moduleArray);
        foreach (var diagnostic in diagnostics)
        {
            ReportDiagnostic(context, diagnostic);
        }

        if (HasErrors(diagnostics))
        {
            return;
        }

        var orderedModules = OrderModules(moduleArray);

        context.AddSource(
            $"{options.TypeName}.g.cs",
            SourceText.From(EmitModuleCatalog(orderedModules, options), Encoding.UTF8));
    }

    private static IReadOnlyList<ModuleModel> OrderModules(
        IReadOnlyList<ModuleModel> modules)
    {
        var modulesBySymbol = CreateModuleMap(modules);
        var ordered = new List<ModuleModel>();
        var states = new Dictionary<INamedTypeSymbol, VisitState>(SymbolEqualityComparer.Default);

        foreach (var module in modules)
        {
            Visit(module, modulesBySymbol, states, ordered);
        }

        return ordered;
    }

    private static IReadOnlyList<ModuleGeneratorDiagnostic> ValidateModules(
        IReadOnlyList<ModuleModel> modules)
    {
        var diagnostics = new List<ModuleGeneratorDiagnostic>();
        var duplicateId = modules
            .Select(module => new ModuleIdUse(module.Id, module.Location))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            foreach (var duplicate in duplicateId.Skip(1))
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.DuplicateModuleId,
                    duplicate.Location,
                    duplicate.Id));
            }

            return diagnostics;
        }

        foreach (var module in modules)
        {
            if (!module.IsModule)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.ModuleTypeDoesNotImplementModule,
                    module.Location,
                    module.TypeName));
                continue;
            }

            if (module.UnsupportedShape is not null)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.UnsupportedModuleType,
                    module.TypeLocation,
                    module.TypeName,
                    module.UnsupportedShape));
                continue;
            }

            if (!module.HasAccessibleParameterlessConstructor)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.ModuleFactoryConstructorMissing,
                    module.Location,
                    module.TypeName));
            }
            else if (!module.SatisfiesRequiredMembers)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.ModuleRequiredMembersNotSatisfied,
                    module.TypeLocation,
                    module.TypeName));
            }
        }

        if (HasErrors(diagnostics))
        {
            return diagnostics;
        }

        foreach (var module in modules)
        {
            foreach (var dependency in module.Dependencies)
            {
                if (HasOpenTypeParameters(dependency.Symbol))
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.UnsupportedDependencyType,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName));
                    continue;
                }

                if (!dependency.IsModule)
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.DependencyTypeIsNotModule,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName));
                    continue;
                }

                if (!dependency.HasModuleAttribute)
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.MissingRequiredDependency,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName));
                }
            }
        }

        if (HasErrors(diagnostics))
        {
            return diagnostics;
        }

        var modulesBySymbol = CreateModuleMap(modules);
        var states = new Dictionary<INamedTypeSymbol, VisitState>(SymbolEqualityComparer.Default);
        var path = new Stack<ModuleModel>();

        foreach (var module in modules)
        {
            VisitForCycle(module, modulesBySymbol, states, path, diagnostics);
            if (HasErrors(diagnostics))
            {
                return diagnostics;
            }
        }

        return diagnostics;
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

    private static void ReportDiagnostic(SourceProductionContext context, ModuleGeneratorDiagnostic diagnostic)
    {
        context.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, diagnostic.Arguments));
    }

    private static bool HasErrors(IEnumerable<ModuleGeneratorDiagnostic> diagnostics)
    {
        return diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
    }

    private static void Visit(
        ModuleModel module,
        IReadOnlyDictionary<INamedTypeSymbol, ModuleModel> modulesBySymbol,
        IDictionary<INamedTypeSymbol, VisitState> states,
        ICollection<ModuleModel> ordered)
    {
        if (states.TryGetValue(module.Symbol, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            return;
        }

        states.Add(module.Symbol, VisitState.Visiting);

        foreach (var dependency in module.Dependencies)
        {
            if (!modulesBySymbol.TryGetValue(dependency.Symbol, out var dependencyModule))
            {
                continue;
            }

            Visit(dependencyModule, modulesBySymbol, states, ordered);
        }

        states[module.Symbol] = VisitState.Visited;
        ordered.Add(module);
    }

    private static void VisitForCycle(
        ModuleModel module,
        IReadOnlyDictionary<INamedTypeSymbol, ModuleModel> modulesBySymbol,
        IDictionary<INamedTypeSymbol, VisitState> states,
        Stack<ModuleModel> path,
        ICollection<ModuleGeneratorDiagnostic> diagnostics)
    {
        if (states.TryGetValue(module.Symbol, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            diagnostics.Add(new ModuleGeneratorDiagnostic(
                ModuleGeneratorDiagnostics.DependencyCycle,
                module.Location,
                CreateCyclePath(path, module)));
            return;
        }

        states.Add(module.Symbol, VisitState.Visiting);
        path.Push(module);

        foreach (var dependency in module.Dependencies)
        {
            if (!modulesBySymbol.TryGetValue(dependency.Symbol, out var dependencyModule))
            {
                continue;
            }

            VisitForCycle(dependencyModule, modulesBySymbol, states, path, diagnostics);
            if (HasErrors(diagnostics))
            {
                return;
            }
        }

        path.Pop();
        states[module.Symbol] = VisitState.Visited;
    }

    private static Dictionary<INamedTypeSymbol, ModuleModel> CreateModuleMap(IEnumerable<ModuleModel> modules)
    {
        var map = new Dictionary<INamedTypeSymbol, ModuleModel>(SymbolEqualityComparer.Default);
        foreach (var module in modules)
        {
            map.Add(module.Symbol, module);
        }

        return map;
    }

    private static string CreateCyclePath(IEnumerable<ModuleModel> path, ModuleModel repeatedModule)
    {
        var orderedPath = path.Reverse().ToArray();
        var cycleStart = Array.FindIndex(orderedPath,
            module => SymbolEqualityComparer.Default.Equals(module.Symbol, repeatedModule.Symbol));
        return string.Join(" -> ", orderedPath.Skip(Math.Max(0, cycleStart))
            .Append(repeatedModule).Select(module => module.TypeName));
    }

    private static string EmitModuleCatalog(
        IReadOnlyList<ModuleModel> modules,
        ModuleCatalogOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        if (!string.IsNullOrWhiteSpace(options.Namespace))
        {
            builder.Append("namespace ").Append(options.Namespace).AppendLine(";");
            builder.AppendLine();
        }

        builder.Append("internal static partial class ").AppendLine(options.TypeName);
        builder.AppendLine("{");
        builder.AppendLine(
            "    public static global::System.Collections.Generic.IReadOnlyList<global::AtomUI.Modularity.ModuleRegistration> CreateRegistrations()");
        builder.AppendLine("    {");
        builder.AppendLine("        return");
        builder.AppendLine("        [");

        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            var suffix = i == modules.Count - 1 ? string.Empty : ",";
            builder.Append("            global::AtomUI.Modularity.ModuleRegistration.For<")
                .Append(module.CodeTypeName).AppendLine(">(");
            builder.Append("                global::AtomUI.Modularity.ModuleDescriptor.For<")
                .Append(module.CodeTypeName).AppendLine(">(");
            builder.Append("                    ").Append(StringLiteral(module.DisplayName)).AppendLine(",");
            EmitDependencies(builder, module);
            builder.AppendLine("                ),");
            builder.Append("                static () => new ").Append(module.CodeTypeName).AppendLine("())" + suffix);
        }

        builder.AppendLine("        ];");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitDependencies(
        StringBuilder builder,
        ModuleModel module)
    {
        if (module.Dependencies.Length == 0)
        {
            builder.AppendLine(
                "                    dependencies: global::System.Array.Empty<global::AtomUI.Modularity.ModuleDependencyDescriptor>()");
            return;
        }

        builder.AppendLine(
            "                    dependencies: new global::AtomUI.Modularity.ModuleDependencyDescriptor[]");
        builder.AppendLine("                    {");
        for (var i = 0; i < module.Dependencies.Length; i++)
        {
            var dependency = module.Dependencies[i];
            var suffix = i == module.Dependencies.Length - 1 ? string.Empty : ",";
            builder.Append("                        global::AtomUI.Modularity.ModuleDependencyDescriptor.")
                .Append("Required")
                .Append("<")
                .Append(dependency.CodeTypeName)
                .AppendLine(">()" + suffix);
        }

        builder.AppendLine("                    }");
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

    private static ImmutableArray<INamedTypeSymbol> GetNamedTypeArray(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (!string.Equals(argument.Key, name, StringComparison.Ordinal))
            {
                continue;
            }

            return argument.Value.Values
                .Select(value => value.Value as INamedTypeSymbol)
                .Where(value => value is not null)
                .Cast<INamedTypeSymbol>()
                .ToImmutableArray();
        }

        return ImmutableArray<INamedTypeSymbol>.Empty;
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

    private static string StringLiteral(string value)
    {
        return SymbolDisplay.FormatLiteral(value, quote: true);
    }

    private static string InferDisplayName(string moduleTypeName)
    {
        var name = moduleTypeName;
        if (name.StartsWith("DataGrid", StringComparison.Ordinal) && name.Length > "DataGrid".Length)
        {
            name = name.Substring("DataGrid".Length);
        }

        if (name.EndsWith("Module", StringComparison.Ordinal) && name.Length > "Module".Length)
        {
            name = name.Substring(0, name.Length - "Module".Length);
        }

        if (name.Length == 0)
        {
            return moduleTypeName;
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

    private sealed class ModuleModel
    {
        public ModuleModel(
            INamedTypeSymbol symbol,
            string id,
            string displayName,
            string typeName,
            Location? location,
            Location typeLocation,
            bool isModule,
            string? unsupportedShape,
            bool hasAccessibleParameterlessConstructor,
            bool satisfiesRequiredMembers,
            ImmutableArray<ModuleDependencyModel> dependencies)
        {
            Symbol = symbol;
            Id = id;
            DisplayName = displayName;
            TypeName = typeName;
            Location = location;
            TypeLocation = typeLocation;
            IsModule = isModule;
            UnsupportedShape = unsupportedShape;
            SatisfiesRequiredMembers = satisfiesRequiredMembers;
            HasAccessibleParameterlessConstructor = hasAccessibleParameterlessConstructor;
            Dependencies = dependencies;
        }

        public INamedTypeSymbol Symbol { get; }

        public string CodeTypeName => Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        public Location TypeLocation { get; }

        public string? UnsupportedShape { get; }

        public bool SatisfiesRequiredMembers { get; }

        public string Id { get; }

        public string DisplayName { get; }

        public string TypeName { get; }

        public Location? Location { get; }

        public bool IsModule { get; }

        public bool HasAccessibleParameterlessConstructor { get; }

        public ImmutableArray<ModuleDependencyModel> Dependencies { get; }
    }

    private sealed class ModuleCatalogOptions
    {
        private const string DefaultTypeName = "ModuleGeneratedCatalog";

        private ModuleCatalogOptions(
            string? @namespace,
            string typeName,
            ImmutableArray<ModuleGeneratorDiagnostic> diagnostics)
        {
            Namespace = @namespace;
            TypeName = typeName;
            Diagnostics = diagnostics;
        }

        public string? Namespace { get; }

        public string TypeName { get; }

        public ImmutableArray<ModuleGeneratorDiagnostic> Diagnostics { get; }

        public static ModuleCatalogOptions From(AnalyzerConfigOptions options)
        {
            var diagnostics = ImmutableArray.CreateBuilder<ModuleGeneratorDiagnostic>();
            var hasNamespace = options.TryGetValue(
                "build_property.AtomUIModularityCatalogNamespace",
                out var namespaceValue);
            var hasTypeName = options.TryGetValue(
                "build_property.AtomUIModularityCatalogTypeName",
                out var typeNameValue);

            string? @namespace = null;
            if (hasNamespace && !string.IsNullOrEmpty(namespaceValue))
            {
                if (!string.IsNullOrWhiteSpace(namespaceValue) && IsValidNamespace(namespaceValue))
                {
                    @namespace = namespaceValue;
                }
                else
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.InvalidCatalogOption,
                        null,
                        "AtomUIModularityCatalogNamespace",
                        namespaceValue ?? string.Empty,
                        "<global namespace>"));
                }
            }

            var typeName = DefaultTypeName;
            if (hasTypeName && !string.IsNullOrEmpty(typeNameValue))
            {
                if (IsValidIdentifier(typeNameValue))
                {
                    typeName = typeNameValue!;
                }
                else
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.InvalidCatalogOption,
                        null,
                        "AtomUIModularityCatalogTypeName",
                        typeNameValue ?? string.Empty,
                        DefaultTypeName));
                }
            }

            return new ModuleCatalogOptions(@namespace, typeName, diagnostics.ToImmutable());
        }

        private static bool IsValidNamespace(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value!.Split('.').All(static part => IsValidIdentifier(part));
        }

        private static bool IsValidIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !SyntaxFacts.IsValidIdentifier(value) ||
                SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None ||
                SyntaxFacts.GetContextualKeywordKind(value) != SyntaxKind.None)
            {
                return false;
            }

            return true;
        }
    }

    private sealed class ModuleIdUse
    {
        public ModuleIdUse(string id, Location? location)
        {
            Id = id;
            Location = location;
        }

        public string Id { get; }

        public Location? Location { get; }
    }

    private sealed class ModuleDependencyModel
    {
        public ModuleDependencyModel(
            INamedTypeSymbol symbol,
            string typeName,
            bool isModule,
            bool hasModuleAttribute,
            Location? location)
        {
            Symbol = symbol;
            TypeName = typeName;
            IsModule = isModule;
            HasModuleAttribute = hasModuleAttribute;
            Location = location;
        }

        public INamedTypeSymbol Symbol { get; }

        public string CodeTypeName => Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        public string TypeName { get; }

        public bool IsModule { get; }

        public bool HasModuleAttribute { get; }

        public Location? Location { get; }
    }

    private sealed class ModuleGeneratorDiagnostic
    {
        public ModuleGeneratorDiagnostic(DiagnosticDescriptor descriptor, Location? location, params object[] arguments)
        {
            Descriptor = descriptor;
            Location = location;
            Arguments = arguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location? Location { get; }

        public object[] Arguments { get; }
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
