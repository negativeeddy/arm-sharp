using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArmRipper.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Queries TheDiscDb GraphQL API to identify discs by their content hash.
/// The API is public and does not require authentication for queries.
///
/// Reference: https://github.com/TheDiscDb/data/blob/main/tools/ImportBuddy/source/ImportBuddy/TheDiscDb.Client/GraphQL/Queries/GetDiscDetailByContentHash.graphql
/// </summary>
public sealed class DiscDbQueryService(
    IHttpClientFactory httpClientFactory,
    IOptions<ArmSettings> settings,
    ILogger<DiscDbQueryService> logger) : IDiscDbQueryService
{
    private const string DefaultApiBaseUrl = "https://thediscdb.com/graphql";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ApiBaseUrl => settings.Value.DiscDbApiBaseUrl ?? DefaultApiBaseUrl;

    private static readonly string Query = """
        query GetDiscDetailByContentHash($hash: String) {
          mediaItems(
            where: {
              releases: { some: { discs: { some: { contentHash: { eq: $hash } } } } }
            }
          ) {
            nodes {
              id
              title
              year
              slug
              imageUrl
              type
              releases {
                slug
                title
                discs(order: { index: ASC }) {
                  index
                  name
                  format
                  slug
                  titles(order: { index: ASC }) {
                    index
                    duration
                    displaySize
                    sourceFile
                    size
                    segmentMap
                    item {
                      title
                      season
                      episode
                      type
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<DiscDbMediaResult?> QueryByHashAsync(string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            logger.LogWarning("DiscDb query: empty hash");
            return null;
        }

        try
        {
            var url = ApiBaseUrl;
            var payload = new
            {
                query = Query,
                variables = new { hash }
            };

            using var http = httpClientFactory.CreateClient("TheDiscDb");
            http.Timeout = TimeSpan.FromSeconds(10);

            var hashPreview = hash[..Math.Min(8, hash.Length)];
            logger.LogInformation("DiscDb query: looking up hash {Hash}... at {Url}", hashPreview, url);

            var response = await http.PostAsJsonAsync(url, payload, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            // Read raw content first for potential debug logging
            var rawContent = await response.Content.ReadAsStringAsync(ct);

            var body = JsonSerializer.Deserialize<DiscDbGraphQlResponse>(rawContent, JsonOptions);

            // Log GraphQL errors (if any) — even if data is present, errors may indicate
            // partial failures or warnings
            if (body?.Errors is { Count: > 0 })
            {
                foreach (var err in body.Errors)
                {
                    var pathStr = err.Path is { Count: > 0 }
                        ? string.Join("/", err.Path.Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText()))
                        : "(none)";
                    logger.LogWarning(
                        "DiscDb query: GraphQL error for hash {Hash}...: {Message} (path: {Path})",
                        hashPreview, err.Message, pathStr);
                }
            }

            var result = body?.Data?.MediaItems?.Nodes?.FirstOrDefault();
            if (result is not null)
            {
                logger.LogInformation(
                    "DiscDb query: matched '{Title}' ({Year}) type={Type} id={Id}",
                    result.Title, result.Year, result.Type, result.Id);
            }
            else
            {
                logger.LogInformation("DiscDb query: no match for hash {Hash}...", hashPreview);

                // Log raw response at Debug level for troubleshooting "no match" issues
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var truncated = rawContent.Length > 2000
                        ? rawContent[..2000] + "... (truncated)"
                        : rawContent;
                    logger.LogDebug("DiscDb query: raw response body: {Raw}", truncated);
                }
            }

            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            logger.LogWarning("DiscDb query: rate limited (429) for hash {Hash}...", hash[..Math.Min(8, hash.Length)]);
            return null;
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("DiscDb query: timeout for hash {Hash}...", hash[..Math.Min(8, hash.Length)]);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DiscDb query: failed for hash {Hash}...", hash[..Math.Min(8, hash.Length)]);
            return null;
        }
    }
}
