using System.Collections;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AtomUI.Modularity.Generators;

public sealed partial class ModuleCatalogGenerator
{
    // Incremental pipeline models hold only strings, primitives, value-equal collections and source
    // locations. They never hold ISymbol, SemanticModel or Compilation instances, so an edit that does
    // not change a module declaration produces equal models and leaves the catalog output cached.

    /// <summary>A source location compared by syntax tree identity and span.</summary>
    /// <remarks>
    /// Unchanged files keep the same <see cref="SyntaxTree"/> instance across compilations, so equality
    /// holds across unrelated edits. Reporting keeps a real source location, which preserves
    /// <c>#pragma</c> suppression; a file-path location would not.
    /// </remarks>
    private sealed record LocationInfo(SyntaxTree Tree, TextSpan Span)
    {
        public Location ToLocation() => Location.Create(Tree, Span);

        public static LocationInfo? From(Location? location) =>
            location is { SourceTree: { } tree } ? new LocationInfo(tree, location.SourceSpan) : null;

        public static LocationInfo? From(SyntaxReference? reference, CancellationToken cancellationToken) =>
            reference is null ? null : new LocationInfo(reference.SyntaxTree, reference.GetSyntax(cancellationToken).Span);
    }

    /// <summary>An immutable array with element-wise equality.</summary>
    private readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _items;

        public EquatableArray(ImmutableArray<T> items) => _items = items.IsDefault ? ImmutableArray<T>.Empty : items;

        public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);
        public int Length => _items.IsDefault ? 0 : _items.Length;
        public T this[int index] => _items[index];

        public bool Equals(EquatableArray<T> other)
        {
            var left = _items.IsDefault ? ImmutableArray<T>.Empty : _items;
            var right = other._items.IsDefault ? ImmutableArray<T>.Empty : other._items;
            if (left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
                if (!EqualityComparer<T>.Default.Equals(left[index], right[index])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = 17;
            foreach (var item in this) hash = unchecked(hash * 31 + (item?.GetHashCode() ?? 0));
            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items.IsDefault ? ImmutableArray<T>.Empty : _items)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static EquatableArray<T> ToEquatable<T>(IEnumerable<T> items) where T : IEquatable<T> => new(items.ToImmutableArray());

    /// <param name="Key">Stable symbol identity: containing assembly identity plus documentation id.</param>
    private sealed record ModuleModel(
        string Key,
        string Id,
        string DisplayName,
        string TypeName,
        string CodeTypeName,
        LocationInfo? Location,
        LocationInfo? TypeLocation,
        bool IsModule,
        string? UnsupportedShape,
        bool HasAccessibleParameterlessConstructor,
        bool SatisfiesRequiredMembers,
        EquatableArray<ModuleDependencyModel> Dependencies);

    /// <param name="Key">Symbol identity of a named dependency type, or null for a non-named type or null entry.</param>
    private sealed record ModuleDependencyModel(
        string? Key,
        string TypeName,
        string? CodeTypeName,
        EquatableArray<string> Aliases,
        bool IsModule,
        bool HasModuleAttribute,
        bool HasOpenTypeParameters,
        string? AssemblyIdentity,
        LocationInfo? Location);

    private sealed record ModuleGeneratorDiagnostic(DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string> Arguments)
    {
        public ModuleGeneratorDiagnostic(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
            : this(descriptor, location, ToEquatable(arguments))
        {
        }

        public Diagnostic ToDiagnostic() =>
            Diagnostic.Create(Descriptor, Location?.ToLocation(), Arguments.Cast<object>().ToArray());
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
