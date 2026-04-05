// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Client for retrieving media segment timestamps from TheIntroDB (api.theintrodb.org).
/// Supports intro, credits (outro), and recap segment types.
/// </summary>
public sealed class TheIntroDbClient
{
    /// <summary>
    /// Default timeout for TheIntroDB requests, in seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.theintrodb.org/v2";
    private const string MediaPath = "/media";
    private const double MillisecondsPerSecond = 1000d;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<TheIntroDbClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TheIntroDbClient"/> class.
    /// </summary>
    public TheIntroDbClient(HttpClient httpClient, ILogger<TheIntroDbClient> logger)
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
    /// Fetch all available segments for a specific episode from TheIntroDB.
    /// </summary>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of segments (intro, credits, recap). Empty if none found.</returns>
    public async Task<IReadOnlyList<IntroDbSegmentResult>> GetSegmentsAsync(
        string imdbId,
        int seasonNumber,
        int episodeNumber,
        string? apiKey,
        CancellationToken cancellationToken
    )
    {
        Debug.Assert(!string.IsNullOrWhiteSpace(imdbId), "IMDb id must be provided.");
        Debug.Assert(seasonNumber > 0, "Season number must be positive.");
        Debug.Assert(episodeNumber > 0, "Episode number must be positive.");

        var requestUri = new Uri(
            $"{BaseUrl}{MediaPath}?imdb_id={Uri.EscapeDataString(imdbId)}&season={seasonNumber}&episode={episodeNumber}"
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TheIntroDB request failed for {ImdbId} S{Season}E{Episode} with status {Status}.",
                imdbId, seasonNumber, episodeNumber, response.StatusCode
            );
            return [];
        }

#if EMBY
        using var payloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var payloadStream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        var payload = await JsonSerializer
            .DeserializeAsync<MediaResponse>(payloadStream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null)
        {
            return [];
        }

        var results = new List<IntroDbSegmentResult>();

        AddSegments(results, payload.Intros, MediaSegmentType.Intro, imdbId, seasonNumber, episodeNumber, "intro");
        AddSegments(results, payload.Credits, MediaSegmentType.Outro, imdbId, seasonNumber, episodeNumber, "credits");
        AddSegments(results, payload.Recaps, MediaSegmentType.Recap, imdbId, seasonNumber, episodeNumber, "recap");

        _logger.LogInformation(
            "TheIntroDB returned {Count} segment(s) for {ImdbId} S{Season}E{Episode}.",
            results.Count, imdbId, seasonNumber, episodeNumber
        );

        return results;
    }

    private void AddSegments(
        List<IntroDbSegmentResult> results,
        List<Segment>? segments,
        MediaSegmentType type,
        string imdbId,
        int season,
        int episode,
        string typeName
    )
    {
        if (segments is null || segments.Count == 0)
        {
            return;
        }

        foreach (var seg in segments)
        {
            // null start_ms means the segment begins at the start of the episode
            var startMs = seg.StartMs ?? 0L;

            // null end_ms means the segment runs to the end of the episode;
            // signal this with -1 so the provider can substitute episode runtime
            var endMs = seg.EndMs ?? -1L;

            if (endMs != -1 && endMs <= startMs)
            {
                _logger.LogWarning(
                    "TheIntroDB returned invalid {Type} timing for {ImdbId} S{Season}E{Episode}: {StartMs}-{EndMs}ms.",
                    typeName, imdbId, season, episode, startMs, endMs
                );
                continue;
            }

            results.Add(new IntroDbSegmentResult(
                type,
                startMs / MillisecondsPerSecond,
                endMs == -1 ? -1d : endMs / MillisecondsPerSecond
            ));
        }
    }

    private sealed class MediaResponse
    {
        [JsonPropertyName("intro")]
        public List<Segment>? Intros { get; set; }

        [JsonPropertyName("credits")]
        public List<Segment>? Credits { get; set; }

        [JsonPropertyName("recap")]
        public List<Segment>? Recaps { get; set; }
    }

    private sealed class Segment
    {
        [JsonPropertyName("start_ms")]
        public long? StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long? EndMs { get; set; }
    }
}
