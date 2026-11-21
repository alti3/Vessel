using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Vessel.Application.Operations.Updates;

namespace Vessel.Infrastructure.Operations;

/// <summary>
///     Fetches the release manifest and its SHA-256 checksum list (OPS-01). The named client goes through the outbound
///     URL guard and the address-pinning handler (SSRF), follows no redirects, times out after 20 seconds and reads at
///     most <see cref="MaxResponseBytes" /> per document.
/// </summary>
public sealed class HttpVersionManifestClient(IHttpClientFactory httpClients) : IVersionManifestClient
{
    public const string HttpClientName = "vessel-version-manifest";

    public const int MaxResponseBytes = 1024 * 1024;

    public async Task<VersionManifestDocument> FetchAsync(Uri manifestUrl, Uri checksumsUrl,
        CancellationToken cancellationToken = default)
    {
        HttpClient client = httpClients.CreateClient(HttpClientName);
        var manifest = await GetBoundedAsync(client, manifestUrl, cancellationToken);
        var checksums = await GetBoundedAsync(client, checksumsUrl, cancellationToken);
        return new VersionManifestDocument(manifestUrl, manifest, Encoding.UTF8.GetString(checksums));
    }

    internal static async Task<byte[]> GetBoundedAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using HttpResponseMessage response =
            await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ManifestVerificationException(
                $"The release server answered {(int)response.StatusCode} for '{ManifestChecksumVerifier.FileNameOf(url)}'.");
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new ManifestVerificationException("A release document is larger than 1 MiB.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new ManifestVerificationException("A release document is larger than 1 MiB.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>The informational version of the entry assembly (set by the release build), else its assembly version.</summary>
public sealed class AssemblyVesselVersionProvider : IVesselVersionProvider
{
    public string CurrentVersion { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AssemblyVesselVersionProvider).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Trim();
        Version? version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
