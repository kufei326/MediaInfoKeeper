using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public sealed class PrefetchService : IDisposable
    {
        private static readonly TimeSpan NextEpisodePrefetchDelay = TimeSpan.FromSeconds(5);
        private readonly ILibraryManager libraryManager;
        private readonly ISessionManager sessionManager;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<long, byte> extractingItemIds = new ConcurrentDictionary<long, byte>();
        private readonly ConcurrentDictionary<long, byte> prefetchingDanmuItemIds = new ConcurrentDictionary<long, byte>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> prefetchSessions = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private readonly object lifecycleLock = new object();
        private CancellationTokenSource disposeCancellationTokenSource = new CancellationTokenSource();
        private bool initialized;
        private bool disposed;

        public PrefetchService(
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.sessionManager = sessionManager;
            this.logger = logger;
        }

        public void Initialize()
        {
            lock (lifecycleLock)
            {
                if (disposed || initialized)
                {
                    return;
                }

                sessionManager.PlaybackStart += OnPlaybackStart;
                initialized = true;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource lifecycleCancellationTokenSource;
            lock (lifecycleLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (initialized)
                {
                    sessionManager.PlaybackStart -= OnPlaybackStart;
                    initialized = false;
                }

                lifecycleCancellationTokenSource = disposeCancellationTokenSource;
            }

            try
            {
                lifecycleCancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            foreach (var session in prefetchSessions.ToArray())
            {
                if (prefetchSessions.TryRemove(session.Key, out var sessionCancellationTokenSource))
                {
                    TryCancel(sessionCancellationTokenSource);
                }
            }

            extractingItemIds.Clear();
            prefetchingDanmuItemIds.Clear();
            lifecycleCancellationTokenSource.Dispose();
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true)
            {
                return;
            }

            var enableMediaInfoPrefetch = Plugin.Instance?.Options?.GetMediaInfoOptions()?.EnableMediaInfoPrefetch == true;
            var enableDanmuPrefetch = Plugin.Instance?.Options?.MetaData?.EnableDanmuPrefetch == true;
            if (!enableMediaInfoPrefetch && !enableDanmuPrefetch)
            {
                return;
            }

            if (e?.Item is not Episode episode)
            {
                return;
            }

            var nextEpisodeIds = Plugin.LibraryService.NextEpisodesId(episode, 1);
            if (nextEpisodeIds.Count == 0)
            {
                return;
            }

            var nextEpisode = libraryManager.GetItemById(nextEpisodeIds[0]) as Episode;
            if (nextEpisode == null)
            {
                return;
            }

            var sessionKey = !string.IsNullOrWhiteSpace(e.PlaySessionId)
                ? e.PlaySessionId
                : e.Session?.Id;
            ScheduleNextEpisodePrefetch(nextEpisode.InternalId, nextEpisode.FileName ?? nextEpisode.Name, sessionKey);
        }

        private void ScheduleNextEpisodePrefetch(long nextEpisodeId, string nextEpisodeName, string sessionKey)
        {
            if (nextEpisodeId <= 0 || string.IsNullOrWhiteSpace(sessionKey))
            {
                return;
            }

            CancellationToken lifecycleCancellationToken;
            lock (lifecycleLock)
            {
                if (disposed)
                {
                    return;
                }

                lifecycleCancellationToken = disposeCancellationTokenSource.Token;
            }

            var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(lifecycleCancellationToken);
            if (!prefetchSessions.TryAdd(sessionKey, cancellationTokenSource))
            {
                cancellationTokenSource.Dispose();
                return;
            }

            logger.Info($"下一集预加载: 5s后执行 {nextEpisodeName}");

            var cancellationToken = cancellationTokenSource.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(NextEpisodePrefetchDelay, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var nextEpisode = libraryManager.GetItemById(nextEpisodeId) as Episode;
                    if (nextEpisode == null)
                    {
                        return;
                    }

                    var prefetchTasks = new List<Task>(2);
                    if (Plugin.Instance?.Options?.GetMediaInfoOptions()?.EnableMediaInfoPrefetch == true)
                    {
                        prefetchTasks.Add(QueueMediaInfoPrefetchIfNeededAsync(nextEpisode, cancellationToken));
                    }

                    if (Plugin.Instance?.Options?.MetaData?.EnableDanmuPrefetch == true)
                    {
                        prefetchTasks.Add(QueueDanmuPrefetchIfNeededAsync(nextEpisode, cancellationToken));
                    }

                    if (prefetchTasks.Count > 0)
                    {
                        await Task.WhenAll(prefetchTasks).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (prefetchSessions.TryRemove(sessionKey, out var existing) && ReferenceEquals(existing, cancellationTokenSource))
                    {
                        TryCancelAndDispose(existing);
                    }
                    else
                    {
                        TryCancelAndDispose(cancellationTokenSource);
                    }
                }
            });
        }

        private async Task EnsurePlaybackMediaInfoAsync(long itemId, string source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workItem = libraryManager.GetItemById(itemId);
            if (workItem is not Video && workItem is not Audio)
            {
                return;
            }

            var hasMediaInfo = Plugin.MediaInfoService.HasMediaInfo(workItem);
            if (hasMediaInfo)
            {
                return;
            }

            var displayName = workItem.Path ?? workItem.Name ?? workItem.Id.ToString();
            var logPrefix = $"{source} 媒体信息提取";
            var restoreResult = Plugin.MediaSourceInfoStore.ApplyToItem(workItem);
            if (workItem is Video)
            {
                Plugin.ChaptersStore.ApplyToItem(workItem);
            }
            else if (workItem is Audio)
            {
                Plugin.EmbeddedInfoStore.ApplyToItem(workItem);
            }

            if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored ||
                restoreResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists)
            {
                logger.Info($"{logPrefix}: 命中已保存 MediaInfo，跳过提取 {workItem.FileName ?? workItem.Name ?? displayName}");
                return;
            }

            logger.Info($"{logPrefix}: 开始 {workItem.FileName ?? workItem.Name ?? displayName}");

            var refreshOptions = Plugin.MediaInfoService.GetMediaInfoRefreshOptions();
            refreshOptions.ImageRefreshMode = MetadataRefreshMode.ValidationOnly;
            refreshOptions.ReplaceAllImages = true;
            refreshOptions.EnableThumbnailImageExtraction = true;
            var collectionFolders = libraryManager.GetCollectionFolders(workItem).Cast<BaseItem>().ToArray();
            var libraryOptions = libraryManager.GetLibraryOptions(workItem);

            cancellationToken.ThrowIfCancellationRequested();

            using (FfProcessGuard.Allow())
            {
                workItem.DateLastRefreshed = new DateTimeOffset();
                await RefreshTaskRunner.RunAsync(
                        () => Plugin.ProviderManager.RefreshSingleItem(
                            workItem,
                            refreshOptions,
                            collectionFolders,
                            libraryOptions,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!Plugin.MediaInfoService.HasMediaInfo(workItem))
            {
                logger.Warn($"{logPrefix}: 提取后仍无 MediaInfo {displayName}");
                return;
            }
            
            logger.Info($"{logPrefix}: 完成 {workItem.FileName ?? workItem.Name ?? displayName}");
        }

        private Task QueueMediaInfoPrefetchIfNeededAsync(BaseItem item, CancellationToken cancellationToken)
        {
            const string source = "媒体信息预加载";

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (item is not Video && item is not Audio)
            {
                return Task.CompletedTask;
            }

            if (Plugin.MediaInfoService.HasMediaInfo(item))
            {
                logger.Info($"{source}: 跳过，已存在媒体信息 {item.FileName ?? item.Name}");
                return Task.CompletedTask;
            }

            if (!extractingItemIds.TryAdd(item.InternalId, 0))
            {
                logger.Info($"{source}: 跳过，提取中 {item.FileName ?? item.Name}");
                return Task.CompletedTask;
            }

            logger.Info($"{source}: 开始 {item.FileName ?? item.Name}");

            return Task.Run(async () =>
            {
                try
                {
                    await EnsurePlaybackMediaInfoAsync(item.InternalId, source, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.Error($"{source} 媒体信息提取: 失败 {item.FileName ?? item.Name}");
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }
                finally
                {
                    extractingItemIds.TryRemove(item.InternalId, out _);
                }
            });
        }

        private Task QueueDanmuPrefetchIfNeededAsync(BaseItem item, CancellationToken cancellationToken)
        {
            const string source = "弹幕预加载";

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (item is not Episode && item is not MediaBrowser.Controller.Entities.Movies.Movie)
            {
                return Task.CompletedTask;
            }

            if (Plugin.DanmuService?.IsEnabled != true || Plugin.DanmuService.IsSupportedItem(item) != true)
            {
                return Task.CompletedTask;
            }

            if (Plugin.DanmuService.ShouldSkipAutoDownload(item))
            {
                logger.Info($"{source}: 跳过，已存在弹幕文件 {item.FileName ?? item.Name}");
                return Task.CompletedTask;
            }

            if (!prefetchingDanmuItemIds.TryAdd(item.InternalId, 0))
            {
                logger.Info($"{source}: 跳过，拉取中 {item.FileName ?? item.Name}");
                return Task.CompletedTask;
            }

            var networkFirst = string.Equals(
                Plugin.Instance?.Options?.MetaData?.DanmuFetchMode,
                MetaDataOptions.DanmuFetchModeOption.NetworkFirst.ToString(),
                StringComparison.Ordinal);

            logger.Info($"{source}: 开始 {item.FileName ?? item.Name}");

            return Task.Run(async () =>
            {
                try
                {
                    var result = await Plugin.DanmuService
                        .QueueDownloadWithReasonAsync(item.InternalId, networkFirst, cancellationToken)
                        .ConfigureAwait(false);
                    if (result?.Succeeded == true)
                    {
                        logger.Info($"{source}: 完成 {item.FileName ?? item.Name}");
                    }
                    else
                    {
                        var reason = string.IsNullOrWhiteSpace(result?.Reason) ? "未获取到内容" : result.Reason;
                        logger.Info($"{source}: 跳过，{reason} {item.FileName ?? item.Name}");
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.Error($"{source}: 失败 {item.FileName ?? item.Name}");
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }
                finally
                {
                    prefetchingDanmuItemIds.TryRemove(item.InternalId, out _);
                }
            });
        }

        private static void TryCancel(CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void TryCancelAndDispose(CancellationTokenSource cancellationTokenSource)
        {
            TryCancel(cancellationTokenSource);
            cancellationTokenSource.Dispose();
        }

    }
}
