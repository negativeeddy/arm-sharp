using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ArmMedia.OvidProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmMedia.OvidProvider;

/// <summary>
/// HTTP client for the OVID REST API.
/// Wraps GET /v1/disc/{fingerprint} and related endpoints.
/// </summary>
public sealed class OvidApiClient
{
    private readonly HttpClient _httpClient;
    private readonly OvidProviderOptions _options;
    private readonly ILogger<OvidApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Initialises the OVID API client.</summary>
    /// <param name="httpClient">The typed HTTP client instance.</param>
    /// <param name="options">OVID provider configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public OvidApiClient(
        HttpClient httpClient,
        IOptions<OvidProviderOptions> options,
        ILogger<OvidApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Look up a disc by its OVID fingerprint.
    /// </summary>
    /// <param name="fingerprint">The OVID fingerprint (e.g., "dvd1-a3f92c1b...").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disc data, or <c>null</c> if no record is found.</returns>
    public async Task<OvidDiscLookupResponse?> LookupByFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            _logger.LogDebug("OVID lookup skipped: empty fingerprint");
            return null;
        }

        var baseUrl = _options.ApiUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1/disc/{Uri.EscapeDataString(fingerprint)}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            _logger.LogDebug("OVID lookup: GET {Url}", url);

            var response = await _httpClient.GetAsync(url, cts.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("OVID lookup: 404 for fingerprint {Fingerprint}", fingerprint);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("OVID API rate limited while looking up {Fingerprint}", fingerprint);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OvidDiscLookupResponse>(JsonOptions, cts.Token);
            if (result is not null)
            {
                _logger.LogInformation(
                    "OVID lookup found: {Title} ({Year}) for fingerprint {Fingerprint}",
                    result.Release?.Title, result.Release?.Year, fingerprint);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OVID lookup timed out for fingerprint {Fingerprint}", fingerprint);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "OVID lookup HTTP error for fingerprint {Fingerprint}", fingerprint);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "OVID lookup JSON parse error for fingerprint {Fingerprint}", fingerprint);
            return null;
        }
    }

    /// <summary>
    /// Look up discs by UPC/barcode.
    /// </summary>
    public async Task<List<OvidDiscLookupResponse>> LookupByUpcAsync(
        string upc,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.ApiUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1/disc/upc/{Uri.EscapeDataString(upc)}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var response = await _httpClient.GetAsync(url, cts.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];

            response.EnsureSuccessStatusCode();

            var wrapper = await response.Content.ReadFromJsonAsync<UpcLookupWrapper>(JsonOptions, cts.Token);
            return wrapper?.Results ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OVID UPC lookup failed for {Upc}", upc);
            return [];
        }
    }

    /// <summary>
    /// Register a disc fingerprint with the OVID database (POST /v1/disc/register).
    /// This is a lightweight submission that records the disc exists, without
    /// requiring full release metadata. Requires a valid ApiToken.
    /// </summary>
    /// <param name="fingerprint">The OVID fingerprint to register.</param>
    /// <param name="format">Disc format ("DVD", "Blu-ray", etc.).</param>
    /// <param name="discLabel">Optional disc label from blkid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (success, message, statusCode).</returns>
    public async Task<(bool Success, string Message, int StatusCode)> RegisterFingerprintAsync(
        string fingerprint,
        string format,
        string? discLabel = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return (false, "Empty fingerprint", 0);

        var token = _options.ApiToken;
        if (string.IsNullOrWhiteSpace(token))
            return (false, "No OVID API token configured (OvidProvider:ApiToken)", 0);

        var baseUrl = _options.ApiUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1/disc/register";

        try
        {
            var body = new
            {
                fingerprint,
                format,
                disc_label = discLabel ?? string.Empty
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, mediaType: MediaTypeHeaderValue.Parse("application/json"))
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            _logger.LogDebug("OVID register: POST {Url} for fingerprint {Fingerprint}", url, fingerprint);

            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Created)
            {
                _logger.LogInformation("OVID registered fingerprint {Fingerprint} successfully", fingerprint);
                return (true, "Registered", 201);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogDebug("OVID fingerprint {Fingerprint} already registered", fingerprint);
                return (true, "Already registered", 409);
            }

            _logger.LogWarning("OVID register failed ({Status}): {Body}", response.StatusCode, responseBody);
            return (false, $"HTTP {response.StatusCode}: {responseBody}", (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OVID register timed out for fingerprint {Fingerprint}", fingerprint);
            return (false, "Timeout", 0);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "OVID register HTTP error for fingerprint {Fingerprint}", fingerprint);
            return (false, ex.Message, 0);
        }
    }

    /// <summary>
    /// Submit a complete disc with full metadata to the OVID database (POST /v1/disc).
    /// Requires a valid ApiToken.
    /// </summary>
    public async Task<(bool Success, string Message, int StatusCode)> SubmitDiscAsync(
        string fingerprint,
        string format,
        string title,
        int? year,
        string contentType,
        int? tmdbId = null,
        string? imdbId = null,
        string? upc = null,
        string? editionName = null,
        string? discLabel = null,
        CancellationToken cancellationToken = default)
    {
        var token = _options.ApiToken;
        if (string.IsNullOrWhiteSpace(token))
            return (false, "No OVID API token configured (OvidProvider:ApiToken)", 0);

        var baseUrl = _options.ApiUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1/disc";

        try
        {
            var body = new
            {
                fingerprint,
                format,
                upc,
                disc_label = discLabel,
                edition_name = editionName,
                release = new
                {
                    title,
                    year,
                    content_type = contentType,
                    tmdb_id = tmdbId,
                    imdb_id = imdbId
                }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            _logger.LogDebug("OVID submit: POST {Url} for {Title} ({Year})", url, title, year);

            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogInformation("OVID submit for {Title}: {Status}", title, response.StatusCode);
                return (true, responseBody, (int)response.StatusCode);
            }

            _logger.LogWarning("OVID submit failed ({Status}): {Body}", response.StatusCode, responseBody);
            return (false, $"HTTP {response.StatusCode}: {responseBody}", (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OVID submit timed out for {Title}", title);
            return (false, "Timeout", 0);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "OVID submit HTTP error for {Title}", title);
            return (false, ex.Message, 0);
        }
    }

    /// <summary>
    /// Health check — verify the API is reachable.
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.ApiUrl.TrimEnd('/');
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private sealed class UpcLookupWrapper
    {
        public List<OvidDiscLookupResponse> Results { get; set; } = [];
    }
}
