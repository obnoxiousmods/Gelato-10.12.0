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
/// Client for retrieving media segment timestamps from IntroDB (api.introdb.app).
/// </summary>
public sealed class IntroDbClient
{
    /// <summary>
    /// Default timeout for IntroDB requests, in seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.introdb.app";
    private const string IntroPath = "/intro";
    private const double MillisecondsPerSecond = 1000d;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<IntroDbClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbClient"/> class.
    /// </summary>
    public IntroDbClient(HttpClient httpClient, ILogger<IntroDbClient> logger)
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
    /// Fetch segments for a specific episode from IntroDB.
    /// </summary>
    public async Task<IReadOnlyList<IntroDbSegmentResult>> GetSegmentsAsync(
        string imdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken
    )
    {
        Debug.Assert(!string.IsNullOrWhiteSpace(imdbId), "IMDb id must be provided.");
        Debug.Assert(seasonNumber > 0, "Season number must be positive.");
        Debug.Assert(episodeNumber > 0, "Episode number must be positive.");

        var baseUri = _httpClient.BaseAddress ?? new Uri(BaseUrl, UriKind.Absolute);
        var requestUri = new UriBuilder(new Uri(baseUri, IntroPath))
        {
            Query = $"imdb_id={Uri.EscapeDataString(imdbId)}&season={seasonNumber}&episode={episodeNumber}",
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "IntroDB request failed for {ImdbId} S{Season}E{Episode} with status {Status}.",
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
            .DeserializeAsync<IntroDbResponse>(payloadStream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null || payload.EndMs <= payload.StartMs || payload.StartMs < 0)
        {
            if (payload is not null)
            {
                _logger.LogWarning(
                    "IntroDB returned invalid timing for {ImdbId} S{Season}E{Episode}: {StartMs}-{EndMs}ms.",
                    imdbId, seasonNumber, episodeNumber, payload.StartMs, payload.EndMs
                );
            }

            return [];
        }

        return
        [
            new IntroDbSegmentResult(
                MediaSegmentType.Intro,
                payload.StartMs / MillisecondsPerSecond,
                payload.EndMs / MillisecondsPerSecond
            ),
        ];
    }

    private sealed class IntroDbResponse
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("submission_count")]
        public int SubmissionCount { get; set; }
    }
}

/// <summary>
/// A single timed segment returned by an intro database provider.
/// <para>
/// <see cref="EndSeconds"/> may be <c>-1</c> when the provider did not supply an end time
/// (e.g. credits running to the end of the episode). The caller is responsible for
/// substituting the episode runtime in that case.
/// </para>
/// </summary>
public sealed record IntroDbSegmentResult(
    MediaSegmentType Type,
    double StartSeconds,
    double EndSeconds
);
