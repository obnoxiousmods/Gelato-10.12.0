// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Config;
using Gelato.Services;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

/// <summary>
/// Gelato media segment provider backed by IntroDB or TheIntroDB.
/// Returns intro, outro (credits), and recap segments depending on what the chosen provider has.
/// </summary>
public class IntroDbSegmentProvider : IMediaSegmentProvider
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";

    private static readonly Regex ImdbIdRegex = new(
        ImdbIdPattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex SeasonEpisodeRegex = new(
        SeasonEpisodePattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private readonly ILibraryManager _libraryManager;
    private readonly IntroDbClient _introDbClient;
    private readonly TheIntroDbClient _theIntroDbClient;
    private readonly AniSkipClient _aniSkipClient;
    private readonly JikanClient _jikanClient;
    private readonly ILogger<IntroDbSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbSegmentProvider"/> class.
    /// </summary>
    public IntroDbSegmentProvider(
        ILibraryManager libraryManager,
        IntroDbClient introDbClient,
        TheIntroDbClient theIntroDbClient,
        AniSkipClient aniSkipClient,
        JikanClient jikanClient,
        ILogger<IntroDbSegmentProvider> logger
    )
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(introDbClient);
        ArgumentNullException.ThrowIfNull(theIntroDbClient);
        ArgumentNullException.ThrowIfNull(aniSkipClient);
        ArgumentNullException.ThrowIfNull(jikanClient);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _introDbClient = introDbClient;
        _theIntroDbClient = theIntroDbClient;
        _aniSkipClient = aniSkipClient;
        _jikanClient = jikanClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Gelato IntroDB";

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        Debug.Assert(
            request.ItemId != Guid.Empty,
            "Media segment request should contain an item id."
        );

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is not Episode episode)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetImdbId(episode, out var imdbId))
        {
            _logger.LogDebug(
                "Skipping segment lookup for {ItemId}: IMDb id missing.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var seasonNumber, out var episodeNumber))
        {
            _logger.LogDebug(
                "Skipping segment lookup for {ItemId}: invalid season/episode number.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        var config = GelatoPlugin.Instance?.Configuration;
        IReadOnlyList<IntroDbSegmentResult> results;
        try
        {
            if (config?.IntroDbProvider == IntroDbProvider.TheIntroDB)
            {
                _logger.LogInformation(
                    "Routing segment lookup to TheIntroDB for {ImdbId} S{Season}E{Episode}.",
                    imdbId, seasonNumber, episodeNumber
                );
                results = await _theIntroDbClient
                    .GetSegmentsAsync(imdbId, seasonNumber, episodeNumber, config.IntroDbApiKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "Routing segment lookup to IntroDB for {ImdbId} S{Season}E{Episode}.",
                    imdbId, seasonNumber, episodeNumber
                );
                results = await _introDbClient
                    .GetSegmentsAsync(imdbId, seasonNumber, episodeNumber, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Segment lookup failed for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId, imdbId, seasonNumber, episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        // Optionally supplement with AniSkip for anime (identified by MAL ID)
        if (config?.AniSkipEnabled == true)
        {
            int malId = 0;

            if (!TryGetMalId(episode, out malId))
            {
                // Fall back to Jikan live lookup by series title
                var seriesTitle = episode.SeriesId != Guid.Empty
                    && _libraryManager.GetItemById(episode.SeriesId) is Series jikanSeries
                    ? jikanSeries.Name
                    : null;

                if (!string.IsNullOrWhiteSpace(seriesTitle))
                {
                    try
                    {
                        malId = await _jikanClient
                            .GetMalIdAsync(seriesTitle, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(
                            exception,
                            "Jikan MAL ID lookup failed for '{Title}' — skipping AniSkip.",
                            seriesTitle
                        );
                    }
                }
            }

            if (malId > 0)
            {
                try
                {
                    _logger.LogInformation(
                        "Supplementing with AniSkip for MAL {MalId} E{Episode}.",
                        malId, episodeNumber
                    );
                    var aniSkipResults = await _aniSkipClient
                        .GetSegmentsAsync(malId, episodeNumber, cancellationToken)
                        .ConfigureAwait(false);

                    if (aniSkipResults.Count > 0)
                    {
                        // Merge: primary provider takes precedence; AniSkip fills in missing types
                        var coveredTypes = new HashSet<MediaSegmentType>();
                        foreach (var r in results)
                        {
                            coveredTypes.Add(r.Type);
                        }

                        var merged = new List<IntroDbSegmentResult>(results);
                        foreach (var r in aniSkipResults)
                        {
                            if (coveredTypes.Add(r.Type))
                            {
                                merged.Add(r);
                            }
                        }

                        results = merged;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "AniSkip lookup failed for MAL {MalId} E{Episode} — continuing without it.",
                        malId, episodeNumber
                    );
                }
            }
        }

        if (results.Count == 0)
        {
            _logger.LogInformation(
                "No segments returned for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId, imdbId, seasonNumber, episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        var dtos = new List<MediaSegmentDto>(results.Count);
        foreach (var result in results)
        {
            var startTicks = (long)(result.StartSeconds * TicksPerSecond);

            // EndSeconds == -1 signals "end of episode" — use runtime if known, else skip
            long endTicks;
            if (result.EndSeconds < 0)
            {
                if (episode.RunTimeTicks is null or 0)
                {
                    _logger.LogDebug(
                        "Skipping {Type} segment for {ItemId}: no end time and runtime unknown.",
                        result.Type, request.ItemId
                    );
                    continue;
                }

                endTicks = episode.RunTimeTicks.Value;
            }
            else
            {
                endTicks = (long)(result.EndSeconds * TicksPerSecond);
            }

            if (endTicks <= startTicks)
            {
                _logger.LogWarning(
                    "Invalid {Type} segment for {ItemId}: start={Start} >= end={End}.",
                    result.Type, request.ItemId, startTicks, endTicks
                );
                continue;
            }

            if (
                episode.RunTimeTicks is > 0 &&
                endTicks > episode.RunTimeTicks.Value &&
                result.EndSeconds >= 0
            )
            {
                _logger.LogWarning(
                    "{Type} segment beyond duration for {ItemId}.",
                    result.Type, request.ItemId
                );
                continue;
            }

            dtos.Add(new MediaSegmentDto
            {
                ItemId = request.ItemId,
                StartTicks = startTicks,
                EndTicks = endTicks,
                Type = result.Type,
            });
        }

        return dtos;
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode);

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        if (
            episode.SeriesId != Guid.Empty
            && _libraryManager.GetItemById(episode.SeriesId) is Series series
        )
        {
            if (
                series.ProviderIds.TryGetValue(
                    MetadataProvider.Imdb.ToString(),
                    out var seriesImdbId
                ) && !string.IsNullOrWhiteSpace(seriesImdbId)
            )
            {
                imdbId = seriesImdbId;
                return true;
            }
        }

        if (
            episode.ProviderIds.TryGetValue(
                MetadataProvider.Imdb.ToString(),
                out var providerImdbId
            ) && !string.IsNullOrWhiteSpace(providerImdbId)
        )
        {
            imdbId = providerImdbId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = ImdbIdRegex.Match(episode.Path);
            if (match.Success)
            {
                imdbId = match.Value;
                return true;
            }
        }

        imdbId = string.Empty;
        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(
        Episode episode,
        out int seasonNumber,
        out int episodeNumber
    )
    {
        seasonNumber = episode.AiredSeasonNumber ?? episode.ParentIndexNumber ?? 0;
        episodeNumber = episode.IndexNumber ?? 0;

        if (seasonNumber > 0 && episodeNumber > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = SeasonEpisodeRegex.Match(episode.Path);
            if (
                match.Success
                && int.TryParse(match.Groups["season"].Value, out var parsedSeason)
                && int.TryParse(match.Groups["episode"].Value, out var parsedEpisode)
            )
            {
                seasonNumber = parsedSeason;
                episodeNumber = parsedEpisode;
                return seasonNumber > 0 && episodeNumber > 0;
            }
        }

        return seasonNumber > 0 && episodeNumber > 0;
    }

    private bool TryGetMalId(Episode episode, out int malId)
    {
        malId = 0;

        // Check series provider IDs first
        if (
            episode.SeriesId != Guid.Empty
            && _libraryManager.GetItemById(episode.SeriesId) is Series series
        )
        {
            foreach (var key in new[] { "MyAnimeList", "mal", "MAL" })
            {
                if (
                    series.ProviderIds.TryGetValue(key, out var id)
                    && !string.IsNullOrWhiteSpace(id)
                    && int.TryParse(id, out malId)
                )
                {
                    return true;
                }
            }
        }

        // Fall back to episode-level provider IDs
        foreach (var key in new[] { "MyAnimeList", "mal", "MAL" })
        {
            if (
                episode.ProviderIds.TryGetValue(key, out var id)
                && !string.IsNullOrWhiteSpace(id)
                && int.TryParse(id, out malId)
            )
            {
                return true;
            }
        }

        return false;
    }
}
