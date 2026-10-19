using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeBindings.Parser;

/// <summary>A header requested by an <c>[Include]</c> attribute, optionally narrowed to named macros.</summary>
internal sealed record HeaderRequest(string Header, IReadOnlyList<string>? Macros)
{
    public string Key => Macros is null ? Header : Header + "?" + string.Join(' ', Macros);
}

internal static class RequestCollector
{
    public static async Task<IReadOnlyList<HeaderRequest>> CollectAsync(IEnumerable<string> sources, IEnumerable<string> symbols)
    {
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithPreprocessorSymbols(symbols);
        var requests = new SortedDictionary<string, HeaderRequest>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var syntax = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(source), options, source);
            foreach (var attribute in (await syntax.GetRootAsync()).DescendantNodes().OfType<AttributeSyntax>())
            {
                if (attribute.Name.ToString().Split('.').Last() is not ("Include" or "IncludeAttribute") ||
                    attribute.Parent?.Parent is not TypeDeclarationSyntax)
                    continue;
                var arguments = attribute.ArgumentList?.Arguments ?? default;
                var header = arguments.FirstOrDefault() is { NameEquals: null, NameColon: null } first ? Literal(first.Expression) : null;
                if (header is null)
                    throw new NotSupportedException($"{Location(attribute)}: Include requires a string literal header path.");
                if (header.Length == 0 || header.StartsWith('/') || header.Split('/').Any(part => part is "" or "." or "..") ||
                    header.Any(character => character is '\\' or '"' or '<' or '>' or '\r' or '\n' || char.IsControl(character)))
                    throw new ArgumentException($"{Location(attribute)}: Invalid include path '{header}'. Use a relative path with '/' separators.");

                IReadOnlyList<string>? macros = null;
                if (arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "Macros") is { } named)
                {
                    var names = Elements(named.Expression)?.Select(Literal).ToArray();
                    if (names is null || names.Length == 0 || names.Any(name => name is null || !IsCIdentifier(name)))
                        throw new NotSupportedException($"{Location(attribute)}: Include.Macros requires a non-empty array of literal C identifiers.");
                    macros = names.Select(name => name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                }
                var request = new HeaderRequest(header, macros);
                requests.TryAdd(request.Key, request);
            }
        }
        return requests.Values.ToArray();
    }

    private static string? Literal(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    private static IEnumerable<ExpressionSyntax?>? Elements(ExpressionSyntax expression) => expression switch
    {
        ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
        ImplicitArrayCreationExpressionSyntax creation => creation.Initializer.Expressions,
        CollectionExpressionSyntax collection => collection.Elements.Select(element => (element as ExpressionElementSyntax)?.Expression),
        _ => null
    };

    private static bool IsCIdentifier(string name) =>
        name.Length != 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string Location(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }
}
