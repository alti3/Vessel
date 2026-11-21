using System.Reflection;
using System.Text;

namespace Vessel.Application.ManagedServices.Templates;

/// <summary>
///     The curated Coolify templates shipped with Vessel as embedded resources
///     (<c>ManagedServices/Templates/Coolify/*.yaml</c>, Apache-2.0, see <c>LICENSE-coolify.txt</c> next to them).
/// </summary>
public sealed class EmbeddedServiceTemplateSource : IServiceTemplateSource
{
    public const string ResourcePrefix = "Vessel.Templates.Coolify.";

    private static readonly Lazy<IReadOnlyList<ServiceTemplateDocument>> Documents = new(() => LoadDocuments());

    public IReadOnlyList<ServiceTemplateDocument> Load()
    {
        return Documents.Value;
    }

    private static List<ServiceTemplateDocument> LoadDocuments()
    {
        Assembly assembly = typeof(EmbeddedServiceTemplateSource).Assembly;
        var documents = new List<ServiceTemplateDocument>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                                    name.EndsWith(".yaml", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            documents.Add(new ServiceTemplateDocument(resource[ResourcePrefix.Length..^".yaml".Length],
                reader.ReadToEnd()));
        }

        return documents;
    }
}
