using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace MediaInfoKeeper.Services
{
    internal static class TheIntroDbService
    {
        internal sealed class MarkerLookupResult
        {
            public bool Found { get; set; }

            public bool RateLimited { get; set; }

            public string Reason { get; set; }

            public long? IntroStartTicks { get; set; }

            public long? IntroEndTicks { get; set; }

            public long? CreditsStartTicks { get; set; }
        }

        private sealed class MediaResponse
        {
            public List<SegmentTimestamp> Intro { get; set; }

            public List<SegmentTimestamp> Credits { get; set; }
        }

        private sealed class SegmentTimestamp
        {
            public long? Start_Ms { get; set; }

            public long? End_Ms { get; set; }
        }

        private sealed class IntroSegment
        {
            public long StartTicks { get; set; }

            public long EndTicks { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly TimeSpan TheIntroDbSuccessCacheDuration = TimeSpan.FromHours(6);
        private const int TheIntroDbMarkerCacheSizeLimit = 128;
        private const string DefaultBaseUrl = "https://api.theintrodb.org/v3";
        private static readonly MemoryCache TheIntroDbMarkerCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = TheIntroDbMarkerCacheSizeLimit
        });

        public static Task<MarkerLookupResult> GetMarkersAsync(Movie movie, CancellationToken cancellationToken)
        {
            if (movie == null)
            {
                return Task.FromResult(NotFound("empty movie"));
            }

            var queryParts = BuildIdQueryParts(movie.GetProviderId(MetadataProviders.Tmdb), null, movie.GetProviderId(MetadataProviders.Imdb));
            if (queryParts.Count == 0)
            {
                return Task.FromResult(NotFound("missing movie ids"));
            }

            if (movie.RunTimeTicks.HasValue && movie.RunTimeTicks.Value > 0)
            {
                queryParts.Add("duration_ms=" + (movie.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond));
            }

            return GetMarkersAsync(queryParts, movie, FormatItemForLog(movie), cancellationToken);
        }

        public static Task<MarkerLookupResult> GetMarkersAsync(Episode episode, CancellationToken cancellationToken)
        {
            if (episode == null)
            {
                return Task.FromResult(NotFound("empty episode"));
            }

            var queryParts = BuildIdQueryParts(
                episode.Series?.GetProviderId(MetadataProviders.Tmdb.ToString())?.Trim(),
                episode.Series?.GetProviderId(MetadataProviders.Tvdb.ToString())?.Trim(),
                episode.Series?.GetProviderId(MetadataProviders.Imdb.ToString())?.Trim());
            if (queryParts.Count == 0 || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
            {
                return Task.FromResult(NotFound("missing series ids or episode numbers"));
            }

            queryParts.Add("season=" + episode.ParentIndexNumber.Value);
            queryParts.Add("episode=" + episode.IndexNumber.Value);
            if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
            {
                queryParts.Add("duration_ms=" + (episode.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond));
            }

            return GetMarkersAsync(queryParts, episode, FormatItemForLog(episode), cancellationToken);
        }

        internal static string FormatItemForLog(BaseItem item)
        {
            if (item is Episode episode)
            {
                var seriesName = episode.FindSeriesName();
                return $"{(string.IsNullOrWhiteSpace(seriesName) ? "<unknown>" : seriesName.Trim())} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
            }

            return item?.FileName ?? item?.Path ?? item?.Name ?? "<unknown>";
        }

        private static async Task<MarkerLookupResult> GetMarkersAsync(
            List<string> queryParts,
            BaseItem item,
            string detail,
            CancellationToken cancellationToken)
        {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null)
            {
                return NotFound("IHttpClient unavailable");
            }

            var configuredBaseUrl = Plugin.Instance?.Options?.IntroSkip?.TheIntroDbBaseUrl;
            var apiUrl = (string.IsNullOrWhiteSpace(configuredBaseUrl) ? DefaultBaseUrl : configuredBaseUrl.Trim()).TrimEnd('/') + "/media";
            var apiKey = Plugin.Instance?.Options?.IntroSkip?.TheIntroDbApiKey?.Trim();
            var cacheKey = apiUrl + "|" + (string.IsNullOrWhiteSpace(apiKey) ? "anonymous" : "authenticated") + "|" + string.Join("&", queryParts);
            if (TheIntroDbMarkerCache.TryGetValue(cacheKey, out MarkerLookupResult cachedResult))
            {
                LogHit(detail, cachedResult);
                return cachedResult;
            }

            var requestUrl = apiUrl + "?" + string.Join("&", queryParts);
            try
            {
                var requestOptions = new HttpRequestOptions
                {
                    Url = requestUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = 10000,
                    ThrowOnErrorResponse = false
                };

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    requestOptions.RequestHeaders["Authorization"] = "Bearer " + apiKey;
                }

                using var response = await httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);
                string body = null;
                if (response.Content != null)
                {
                    using var reader = new StreamReader(response.Content);
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 404)
                {
                    return NotFound("404 Not Found");
                }

                if (statusCode == 429)
                {
                    Plugin.SharedLogger?.Info("TheIntroDB 请求达到限制: {0}", detail);
                    return new MarkerLookupResult
                    {
                        Found = false,
                        RateLimited = true,
                        Reason = "rate limited"
                    };
                }

                if (statusCode < 200 || statusCode >= 300)
                {
                    Plugin.SharedLogger?.Info("TheIntroDB 请求失败: status={0}, {1}, body={2}", statusCode, detail, body);
                    return NotFound("http " + statusCode);
                }

                var media = JsonSerializer.Deserialize<MediaResponse>(body, JsonOptions);
                var intro = SelectFirstValidIntro(media?.Intro);
                var credits = SelectFirstValidCredits(media?.Credits, item);
                if (intro == null && credits == null)
                {
                    return NotFound("no usable segment");
                }

                var result = new MarkerLookupResult
                {
                    Found = true,
                    IntroStartTicks = intro?.StartTicks,
                    IntroEndTicks = intro?.EndTicks,
                    CreditsStartTicks = credits
                };
                TheIntroDbMarkerCache.Set(
                    cacheKey,
                    result,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TheIntroDbSuccessCacheDuration,
                        Size = 1
                    });
                LogHit(detail, result);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Info("TheIntroDB 查询异常: {0}, {1}", detail, ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                return NotFound(ex.Message);
            }
        }

        private static List<string> BuildIdQueryParts(string tmdbId, string tvdbId, string imdbId)
        {
            var queryParts = new List<string>();
            if (TryParsePositiveInt(tmdbId, out var parsedTmdbId))
            {
                queryParts.Add("tmdb_id=" + parsedTmdbId);
            }
            else if (TryParsePositiveInt(tvdbId, out var parsedTvdbId))
            {
                queryParts.Add("tvdb_id=" + parsedTvdbId);
            }
            else if (!string.IsNullOrWhiteSpace(imdbId))
            {
                queryParts.Add("imdb_id=" + Uri.EscapeDataString(imdbId.Trim()));
            }

            return queryParts;
        }

        private static IntroSegment SelectFirstValidIntro(IEnumerable<SegmentTimestamp> segments)
        {
            if (segments == null)
            {
                return null;
            }

            foreach (var segment in segments)
            {
                if (!segment.End_Ms.HasValue || segment.End_Ms.Value <= 0)
                {
                    continue;
                }

                var startMs = Math.Max(0, segment.Start_Ms ?? 0);
                if (segment.End_Ms.Value <= startMs)
                {
                    continue;
                }

                return new IntroSegment
                {
                    StartTicks = startMs * TimeSpan.TicksPerMillisecond,
                    EndTicks = segment.End_Ms.Value * TimeSpan.TicksPerMillisecond
                };
            }

            return null;
        }

        private static long? SelectFirstValidCredits(IEnumerable<SegmentTimestamp> segments, BaseItem item)
        {
            if (segments == null)
            {
                return null;
            }

            foreach (var segment in segments)
            {
                if (!segment.Start_Ms.HasValue || segment.Start_Ms.Value <= 0)
                {
                    continue;
                }

                var startTicks = segment.Start_Ms.Value * TimeSpan.TicksPerMillisecond;
                if (item?.RunTimeTicks.HasValue == true && startTicks >= item.RunTimeTicks.Value)
                {
                    continue;
                }

                return startTicks;
            }

            return null;
        }

        private static void LogHit(string detail, MarkerLookupResult result)
        {
            if (result?.Found != true)
            {
                return;
            }

            Plugin.SharedLogger?.Info(
                "TheIntroDB 命中: {0} intro={1}-{2}, creditsStart={3}",
                detail,
                FormatTicks(result.IntroStartTicks),
                FormatTicks(result.IntroEndTicks),
                FormatTicks(result.CreditsStartTicks));
        }

        private static bool TryParsePositiveInt(string value, out int number)
        {
            return int.TryParse(value, out number) && number > 0;
        }

        private static string FormatTicks(long? ticks)
        {
            return ticks.HasValue ? new TimeSpan(ticks.Value).ToString(@"hh\:mm\:ss\.fff") : "<none>";
        }

        private static MarkerLookupResult NotFound(string reason)
        {
            return new MarkerLookupResult
            {
                Found = false,
                Reason = reason
            };
        }
    }
}
