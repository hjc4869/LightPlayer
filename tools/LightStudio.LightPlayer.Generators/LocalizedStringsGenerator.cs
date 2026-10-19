using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace LightStudio.LightPlayer.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class LocalizedStringsGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidResource = new(
        "LPL001",
        "Invalid localization resource",
        "Cannot generate localization sources from '{0}': {1}",
        "Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "Resources", "ResourceManager", "Culture", "resourceMan", "resourceCulture",
        "LocalizedStrings", "Instance", "PropertyChanged", "ApplyLanguage", "Refresh",
        "Register", "NotifyHosts", "Hosts", "SystemCulture", "AllPropertiesChanged", "Item"
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var resources = context.AdditionalTextsProvider
            .Where(file => string.Equals(Path.GetFileName(file.Path), "Resources.resx", StringComparison.Ordinal))
            .Select((file, cancellationToken) => ReadResources(file, cancellationToken));

        context.RegisterSourceOutput(resources, (productionContext, resource) =>
        {
            if (resource.Error is not null)
            {
                productionContext.ReportDiagnostic(Diagnostic.Create(
                    InvalidResource, Location.None, resource.Path, resource.Error));
                return;
            }

            var accessors = new StringBuilder(ResourceHeader);
            var bindings = new StringBuilder(LocalizedHeader);
            foreach (var name in resource.Names)
            {
                var identifier = SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
                accessors.Append("    public static string ").Append(identifier)
                    .Append(" => ResourceManager.GetString(")
                    .Append(SymbolDisplay.FormatLiteral(name, quote: true))
                    .Append(", resourceCulture)!;\n");
                bindings.Append("    public string ").Append(identifier)
                    .Append(" => Resources.").Append(identifier).Append(";\n");
            }

            accessors.Append("}\n");
            bindings.Append("}\n");
            productionContext.AddSource("Resources.g.cs", SourceText.From(
                accessors.ToString().Replace("\r\n", "\n"), Encoding.UTF8));
            productionContext.AddSource("LocalizedStrings.g.cs", SourceText.From(
                bindings.ToString().Replace("\r\n", "\n"), Encoding.UTF8));
        });
    }

    private static ResourceData ReadResources(AdditionalText file, CancellationToken cancellationToken)
    {
        var text = file.GetText(cancellationToken);
        if (text is null)
        {
            return new ResourceData(file.Path, Array.Empty<string>(), "the file could not be read");
        }

        XDocument document;
        try
        {
            using var input = new StringReader(text.ToString());
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            return new ResourceData(file.Path, Array.Empty<string>(), exception.Message);
        }

        if (document.Root?.Name != "root")
        {
            return new ResourceData(file.Path, Array.Empty<string>(), "expected a RESX root element");
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var data in document.Root.Elements("data"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = (string?)data.Attribute("name");
            if (string.IsNullOrEmpty(name) || !SyntaxFacts.IsValidIdentifier(name) || ReservedNames.Contains(name))
            {
                return new ResourceData(file.Path, Array.Empty<string>(),
                    $"resource key '{name}' must be a C# identifier that does not conflict with the localization API");
            }

            var type = (string?)data.Attribute("type");
            if (data.Attribute("mimetype") is not null ||
                (type is not null && type.Split(',')[0].Trim() != "System.String") ||
                data.Element("value") is null)
            {
                return new ResourceData(file.Path, Array.Empty<string>(),
                    $"resource '{name}' must have a string value");
            }

            if (!names.Add(name))
            {
                return new ResourceData(file.Path, Array.Empty<string>(), $"duplicate resource key '{name}'");
            }
        }

        return new ResourceData(file.Path, new List<string>(names).ToArray(), null);
    }

    private sealed class ResourceData(string path, string[] names, string? error)
    {
        public string Path { get; } = path;
        public string[] Names { get; } = names;
        public string? Error { get; } = error;
    }

    private const string ResourceHeader = """
        // <auto-generated />
        #nullable enable

        namespace LightStudio.LightPlayer.Strings;

        public class Resources
        {
            private static global::System.Resources.ResourceManager? resourceMan;
            private static global::System.Globalization.CultureInfo? resourceCulture;

            internal Resources()
            {
            }

            [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Advanced)]
            public static global::System.Resources.ResourceManager ResourceManager =>
                resourceMan ??= new global::System.Resources.ResourceManager(
                    "LightStudio.LightPlayer.Strings.Resources", typeof(Resources).Assembly);

            [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Advanced)]
            public static global::System.Globalization.CultureInfo? Culture
            {
                get => resourceCulture;
                set => resourceCulture = value;
            }

        """ + "\n";

    private const string LocalizedHeader = """
        // <auto-generated />
        #nullable enable

        namespace LightStudio.LightPlayer.Strings;

        public sealed partial class LocalizedStrings
        {
        """ + "\n";
}