using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 元数据刷新 runner，统一去重并限制插件触发的元数据刷新并发。
    /// </summary>
    public static class MetaDataRunner
    {
        /// <summary>
        /// runner 当前队列状态，用于插件设置页展示。
        /// </summary>
        public sealed class QueueStats
        {
            /// <summary>已进入 runner 但尚未获取并发槽的任务数。</summary>
            public int Waiting { get; set; }

            /// <summary>已获取并发槽并正在执行的任务数。</summary>
            public int Running { get; set; }

            /// <summary>当前配置允许的最大并发数。</summary>
            public int MaxConcurrent { get; set; }

            /// <summary>尚未完成的任务总数。</summary>
            public int Total => Waiting + Running;
        }

        private sealed class RefreshRequest
        {
            public long InternalId { get; set; }

            public MetadataRefreshOptions Options { get; set; }

            public CancellationToken CancellationToken { get; set; }

            public TaskCompletionSource<bool> Completion { get; set; }
        }

        private static readonly object QueueSync = new object();
        private static readonly Queue<RefreshRequest> RefreshQueue =
            new Queue<RefreshRequest>();
        private static readonly Dictionary<long, RefreshRequest> InFlightRefreshes =
            new Dictionary<long, RefreshRequest>();

        private static int maxConcurrentCount = 3;
        private static int activeCount;
        private static int waitingCount;

        /// <summary>
        /// 更新元数据 runner 的运行配置，避免执行热路径反复读取插件配置。
        /// </summary>
        /// <param name="maxConcurrent">最大并发数。</param>
        public static void Configure(int maxConcurrent)
        {
            Volatile.Write(ref maxConcurrentCount, Math.Max(1, maxConcurrent));

            lock (QueueSync)
            {
                StartWorkersInsideLock();
            }
        }

        /// <summary>
        /// 获取元数据刷新 runner 的实时队列状态。
        /// </summary>
        public static QueueStats GetQueueStats()
        {
            return new QueueStats
            {
                Waiting = Math.Max(0, Volatile.Read(ref waitingCount)),
                Running = Math.Max(0, Volatile.Read(ref activeCount)),
                MaxConcurrent = GetMaxConcurrent()
            };
        }

        /// <summary>
        /// 使用指定刷新选项刷新单个条目的元数据，并受插件元数据并发设置限制。
        /// </summary>
        /// <param name="internalId">Emby 条目内部 ID。</param>
        /// <param name="options">Emby 原始刷新选项。</param>
        /// <param name="cancellationToken">取消标记。</param>
        public static async Task RefreshMetaDataAsync(
            long internalId,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken = default)
        {
            if (internalId <= 0 || options == null)
            {
                return;
            }

            var item = Plugin.LibraryManager?.GetItemById(internalId) as BaseItem;
            if (item == null)
            {
                return;
            }

            var refreshTask = EnqueueRefresh(internalId, options, cancellationToken);
            await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static Task EnqueueRefresh(
            long internalId,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            lock (QueueSync)
            {
                if (InFlightRefreshes.TryGetValue(internalId, out var existing))
                {
                    return existing.Completion.Task;
                }

                var request = new RefreshRequest
                {
                    InternalId = internalId,
                    Options = options,
                    CancellationToken = cancellationToken,
                    Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                };

                InFlightRefreshes[internalId] = request;
                RefreshQueue.Enqueue(request);
                UpdateWaitingCountInsideLock();
                StartWorkersInsideLock();
                return request.Completion.Task;
            }
        }

        private static void StartWorkersInsideLock()
        {
            var maxConcurrent = GetMaxConcurrent();
            while (activeCount < maxConcurrent && RefreshQueue.Count > 0)
            {
                activeCount++;
                Volatile.Write(ref activeCount, activeCount);
                _ = Task.Run(ProcessQueueAsync);
            }
        }

        private static async Task ProcessQueueAsync()
        {
            while (true)
            {
                RefreshRequest request;
                lock (QueueSync)
                {
                    if (RefreshQueue.Count == 0 || activeCount > GetMaxConcurrent())
                    {
                        activeCount = Math.Max(0, activeCount - 1);
                        Volatile.Write(ref activeCount, activeCount);
                        return;
                    }

                    request = RefreshQueue.Dequeue();
                    UpdateWaitingCountInsideLock();
                }

                await ExecuteRefreshRequestAsync(request).ConfigureAwait(false);
            }
        }

        private static async Task ExecuteRefreshRequestAsync(RefreshRequest request)
        {
            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();

                var item = Plugin.LibraryManager?.GetItemById(request.InternalId) as BaseItem;
                if (item != null)
                {
                    await RefreshMetaDataCoreAsync(item, request.Options, request.CancellationToken)
                        .ConfigureAwait(false);
                }

                request.Completion.TrySetResult(true);
            }
            catch (OperationCanceledException ex)
            {
                request.Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                var displayName = Plugin.LibraryManager?.GetItemById(request.InternalId) is BaseItem item
                    ? item.FileName ?? item.Path ?? item.Name
                    : request.InternalId.ToString();
                Plugin.SharedLogger?.Error($"元数据刷新失败 item={displayName}");
                Plugin.SharedLogger?.Error(ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                request.Completion.TrySetException(ex);
            }
            finally
            {
                lock (QueueSync)
                {
                    if (InFlightRefreshes.TryGetValue(request.InternalId, out var current) &&
                        ReferenceEquals(current, request))
                    {
                        InFlightRefreshes.Remove(request.InternalId);
                    }

                    StartWorkersInsideLock();
                }
            }
        }

        private static async Task RefreshMetaDataCoreAsync(
            BaseItem item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            if (ShouldExpandRecursive(options) && item is Folder folder)
            {
                await RefreshRecursiveFolderAsync(folder, options, cancellationToken).ConfigureAwait(false);
                return;
            }

            await RefreshSingleMetaDataAsync(item, options, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task RefreshRecursiveFolderAsync(
            Folder folder,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var itemOptions = new MetadataRefreshOptions(options)
            {
                Recursive = false
            };
            await RefreshSingleMetaDataAsync(folder, itemOptions, cancellationToken)
                .ConfigureAwait(false);

            foreach (var child in folder.GetRecursiveChildren())
            {
                if (child == null || child.InternalId == folder.InternalId)
                {
                    continue;
                }

                var childOptions = new MetadataRefreshOptions(options)
                {
                    Recursive = false
                };
                _ = EnqueueRefresh(child.InternalId, childOptions, CancellationToken.None);
            }
        }

        private static async Task RefreshSingleMetaDataAsync(
            BaseItem item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            if (item is Video || item is Audio)
            {
                Plugin.SharedLogger?.Info("刷新元数据 {0}", item.FileName);
            }

            await Plugin.MetaDataService
                .RefreshMetaDataAsync(item, options, cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool ShouldExpandRecursive(MetadataRefreshOptions options)
        {
            return options.Recursive &&
                   (options.MetadataRefreshMode != MetadataRefreshMode.Default ||
                    options.ImageRefreshMode != MetadataRefreshMode.Default ||
                    options.ReplaceAllMetadata ||
                    options.ReplaceAllImages);
        }

        /// <summary>
        /// 使用插件默认的入库元数据刷新选项刷新单个条目。
        /// </summary>
        /// <param name="internalId">Emby 条目内部 ID。</param>
        /// <param name="cancellationToken">取消标记。</param>
        public static async Task RefreshMetaDataAsync(
            long internalId,
            CancellationToken cancellationToken = default)
        {
            var item = Plugin.LibraryManager?.GetItemById(internalId) as BaseItem;
            if (item == null)
            {
                return;
            }

            var displayName = item.FileName ?? item.Path ?? item.Name;
            var logger = Plugin.SharedLogger;
            var refreshOptions = GetRefreshOptions();

            try
            {
                await RefreshMetaDataAsync(internalId, refreshOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.Error($"入库元数据: 刷新失败 item={displayName}");
                logger?.Error(ex.Message);
                logger?.Debug(ex.StackTrace);
            }
        }

        private static MetadataRefreshOptions GetRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(Plugin.SharedLogger, Plugin.FileSystem))
            {
                EnableRemoteContentProbe = false,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                EnableThumbnailImageExtraction = Plugin.Instance?.Options?.MetaData?.EnableImageCapture ?? true,
                EnableSubtitleDownloading = false
            };
        }

        private static void UpdateWaitingCountInsideLock()
        {
            waitingCount = RefreshQueue.Count;
            Volatile.Write(ref waitingCount, waitingCount);
        }

        private static int GetMaxConcurrent()
        {
            return Math.Max(1, Volatile.Read(ref maxConcurrentCount));
        }
    }
}
