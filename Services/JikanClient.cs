// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Client for looking up MyAnimeList IDs via the Jikan API (api.jikan.moe).
/// Searches by anime title and caches results to respect Jikan's rate limits.
/// </summary>
public sealed class JikanClient
{
    /// <summary>Default timeout for Jikan requests, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.jikan.moe/v4";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<JikanClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JikanClient"/> class.
    /// </summary>
    public JikanClient(HttpClient httpClient, ILogger<JikanClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    /// <summary>
    /// Look up the MyAnimeList ID for an anime series by title.
    /// Results are cached in memory for the lifetime of the plugin.
    /// </summary>
    /// <param name="seriesTitle">The series title to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The MAL ID, or 0 if not found.</returns>
    public async Task<int> GetMalIdAsync(string seriesTitle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return 0;
        }

        var requestUri = new Uri(
            $"{BaseUrl}/anime?q={Uri.EscapeDataString(seriesTitle)}&type=tv&limit=5&sfw=false"
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogDebug(
                "Jikan returned {Status} for title '{Title}'.",
                response.StatusCode, seriesTitle
            );
            return 0;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Jikan request failed for title '{Title}' with status {Status}.",
                seriesTitle, response.StatusCode
            );
            return 0;
        }

#if EMBY
        using var payloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var payloadStream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#endif

        var payload = await JsonSerializer
            .DeserializeAsync<JikanSearchResponse>(payloadStream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Data is null || payload.Data.Count == 0)
        {
            return 0;
        }

        var malId = FindBestMatch(seriesTitle, payload.Data);

        if (malId > 0)
        {
            _logger.LogInformation(
                "Jikan resolved '{Title}' to MAL ID {MalId}.",
                seriesTitle, malId
            );
        }
        else
        {
            _logger.LogDebug(
                "Jikan could not resolve '{Title}' to a MAL ID.",
                seriesTitle
            );
        }

        return malId;
    }

    private static int FindBestMatch(string query, List<JikanAnime> results)
    {
        var normalizedQuery = query.Trim();

        // Prefer exact title match (English or romanized)
        foreach (var anime in results)
        {
            if (
                string.Equals(anime.Title, normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(anime.TitleEnglish, normalizedQuery, StringComparison.OrdinalIgnoreCase)
            )
            {
                return anime.MalId;
            }
        }

        // Check synonyms for exact match
        foreach (var anime in results)
        {
            if (anime.TitleSynonyms is null)
            {
                continue;
            }

            foreach (var synonym in anime.TitleSynonyms)
            {
                if (string.Equals(synonym, normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    return anime.MalId;
                }
            }
        }

        // Fall back to first result (Jikan ranks by relevance)
        return results[0].MalId;
    }

    private sealed class JikanSearchResponse
    {
        [JsonPropertyName("data")]
        public List<JikanAnime>? Data { get; set; }
    }

    private sealed class JikanAnime
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("title_english")]
        public string? TitleEnglish { get; set; }

        [JsonPropertyName("title_synonyms")]
        public List<string>? TitleSynonyms { get; set; }
    }
}
