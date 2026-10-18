using System.Net.Http.Headers;
using System.Text.Json;

namespace PixelMmo.Server.Transport;

public sealed class SupabaseAuthService(HttpClient httpClient, IConfiguration configuration)
{
    private readonly string _url = configuration["Supabase:Url"]?.TrimEnd('/') ?? string.Empty;
    private readonly string _publishableKey = configuration["Supabase:PublishableKey"] ?? string.Empty;

    public async Task<Guid?> ValidateAccessTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_publishableKey) || string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_url}/auth/v1/user");
        request.Headers.TryAddWithoutValidation("apikey", _publishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("id", out var idElement) && Guid.TryParse(idElement.GetString(), out var userId)
            ? userId
            : null;
    }
}
