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
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Client for retrieving anime intro/outro timestamps from AniSkip (api.aniskip.com).
/// Requires a MyAnimeList (MAL) ID. No API key needed.
/// </summary>
public sealed class AniSkipClient
{
    /// <summary>Default timeout for AniSkip requests, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.aniskip.com/v2";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<AniSkipClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniSkipClient"/> class.
    /// </summary>
    public AniSkipClient(HttpClient httpClient, ILogger<AniSkipClient> logger)
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
    /// Fetch intro/outro segments for a specific anime episode from AniSkip.
    /// </summary>
    /// <param name="malId">MyAnimeList (MAL) series ID.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of segments (intro, outro). Empty if none found.</returns>
    public async Task<IReadOnlyList<IntroDbSegmentResult>> GetSegmentsAsync(
        int malId,
        int episodeNumber,
        CancellationToken cancellationToken
    )
    {
        var requestUri = new Uri(
            $"{BaseUrl}/skip-times/{malId}/{episodeNumber}?types[]=op&types[]=ed&types[]=recap&episodeLength=0"
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
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
                "AniSkip request failed for MAL {MalId} E{Episode} with status {Status}.",
                malId, episodeNumber, response.StatusCode
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
            .DeserializeAsync<AniSkipResponse>(payloadStream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null || !payload.Found || payload.Results is null)
        {
            return [];
        }

        var results = new List<IntroDbSegmentResult>();
        foreach (var result in payload.Results)
        {
            if (result.Interval is null)
            {
                continue;
            }

            var type = result.SkipType switch
            {
                "op" or "mixed-op" => (MediaSegmentType?)MediaSegmentType.Intro,
                "ed" or "mixed-ed" => MediaSegmentType.Outro,
                "recap" => MediaSegmentType.Recap,
                _ => null,
            };

            if (type is null)
            {
                continue;
            }

            var startSeconds = result.Interval.StartTime;
            var endSeconds = result.Interval.EndTime;

            if (endSeconds <= startSeconds)
            {
                _logger.LogWarning(
                    "AniSkip returned invalid {Type} timing for MAL {MalId} E{Episode}: {Start}-{End}s.",
                    result.SkipType, malId, episodeNumber, startSeconds, endSeconds
                );
                continue;
            }

            results.Add(new IntroDbSegmentResult(type.Value, startSeconds, endSeconds));
        }

        _logger.LogInformation(
            "AniSkip returned {Count} segment(s) for MAL {MalId} E{Episode}.",
            results.Count, malId, episodeNumber
        );

        return results;
    }

    private sealed class AniSkipResponse
    {
        [JsonPropertyName("found")]
        public bool Found { get; set; }

        [JsonPropertyName("results")]
        public List<AniSkipResult>? Results { get; set; }
    }

    private sealed class AniSkipResult
    {
        [JsonPropertyName("interval")]
        public AniSkipInterval? Interval { get; set; }

        [JsonPropertyName("skipType")]
        public string SkipType { get; set; } = string.Empty;
    }

    private sealed class AniSkipInterval
    {
        [JsonPropertyName("startTime")]
        public double StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public double EndTime { get; set; }
    }
}
