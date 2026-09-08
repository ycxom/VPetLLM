using System.Net.Http;
using VPetLLM.Utils.Common;

namespace VPetLLM;

public partial class VPetLLM
{
    /// <summary>
    /// Sends an official built-in service request through VPetLLM's native
    /// authenticated and encrypted transport. Plugins provide only the original
    /// business request; Steam identity, device signals and protocol keys never
    /// pass through the plugin assembly.
    /// </summary>
    /// <remarks>
    /// This entry point is reserved for official built-in services. Third-party
    /// APIs must continue to use their own HttpClient and authentication scheme.
    /// Protocol v2 authenticates independent 64 KiB AEAD frames. Response
    /// plaintext is exposed incrementally only after each frame passes GCM
    /// authentication, while an authenticated final frame detects truncation.
    /// </remarks>
    public Task<HttpResponseMessage> SendAuthenticatedServiceRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
            throw new ArgumentException("An absolute service URL is required.", nameof(request));
        if (!string.Equals(request.RequestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Official service requests require HTTPS.", nameof(request));

        return SecureCommunicationBridge.SendAsync(
            client, request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
