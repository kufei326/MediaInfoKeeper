using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 媒体信息提取 runner，统一去重并限制 ffprobe/ffmpeg 提取并发。
    /// </summary>
    public static class MediaInfoRunner
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

        private sealed class ExtractionRequest
        {
            public long InternalId { get; set; }

            public string Source { get; set; }

            public CancellationToken CancellationToken { get; set; }

            public MediaStreamType[] RequiredStreamTypes { get; set; }

            public TaskCompletionSource<bool> Completion { get; set; }
        }

        private static readonly object QueueSync = new object();
        private static readonly Queue<ExtractionRequest> ExtractionQueue =
            new Queue<ExtractionRequest>();
        private static readonly Dictionary<long, ExtractionRequest> InFlightExtractions =
            new Dictionary<long, ExtractionRequest>();

        private static int maxConcurrentCount = 1;
        private static int activeCount;
        private static int waitingCount;

        /// <summary>
        /// 更新媒体信息 runner 的运行配置，避免执行热路径反复读取插件配置。
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
        /// 获取媒体信息提取 runner 的实时队列状态。
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
        /// 提取单个音视频条目的媒体信息，同一条目重复请求会复用正在运行的任务。
        /// </summary>
        /// <param name="internalId">Emby 条目内部 ID。</param>
        /// <param name="source">日志来源。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <param name="requiredStreamTypes">完成后必须存在的媒体流类型。</param>
        /// <returns>提取成功返回 true，否则返回 false。</returns>
        public static async Task<bool> ExtractMediaInfoAsync(
            long internalId,
            string source = "媒体信息提取",
            CancellationToken cancellationToken = default,
            MediaStreamType[] requiredStreamTypes = null)
        {
            if (internalId <= 0)
            {
                return false;
            }

            var item = Plugin.LibraryManager?.GetItemById(internalId) as BaseItem;
            if (item == null)
            {
                return false;
            }

            var extractionTask = EnqueueExtraction(internalId, source, cancellationToken, requiredStreamTypes);
            return await extractionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static Task<bool> EnqueueExtraction(
            long internalId,
            string source,
            CancellationToken cancellationToken,
            MediaStreamType[] requiredStreamTypes)
        {
            lock (QueueSync)
            {
                if (InFlightExtractions.TryGetValue(internalId, out var existing))
                {
                    return existing.Completion.Task;
                }

                var request = new ExtractionRequest
                {
                    InternalId = internalId,
                    Source = source,
                    CancellationToken = cancellationToken,
                    RequiredStreamTypes = requiredStreamTypes,
                    Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                };

                InFlightExtractions[internalId] = request;
                ExtractionQueue.Enqueue(request);
                UpdateWaitingCountInsideLock();
                StartWorkersInsideLock();
                return request.Completion.Task;
            }
        }

        private static void StartWorkersInsideLock()
        {
            var maxConcurrent = GetMaxConcurrent();
            while (activeCount < maxConcurrent && ExtractionQueue.Count > 0)
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
                ExtractionRequest request;
                lock (QueueSync)
                {
                    if (ExtractionQueue.Count == 0 || activeCount > GetMaxConcurrent())
                    {
                        activeCount = Math.Max(0, activeCount - 1);
                        Volatile.Write(ref activeCount, activeCount);
                        return;
                    }

                    request = ExtractionQueue.Dequeue();
                    UpdateWaitingCountInsideLock();
                }

                await ExecuteExtractionRequestAsync(request).ConfigureAwait(false);
            }
        }

        private static async Task ExecuteExtractionRequestAsync(ExtractionRequest request)
        {
            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();

                var item = Plugin.LibraryManager?.GetItemById(request.InternalId) as BaseItem;
                var result = item != null &&
                             await Plugin.MediaInfoService
                                 .ExtractMediaInfoAsync(
                                     item,
                                     request.Source,
                                     request.CancellationToken,
                                     request.RequiredStreamTypes)
                                 .ConfigureAwait(false);

                request.Completion.TrySetResult(result);
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
                Plugin.SharedLogger?.Error($"{request.Source} 媒体信息提取失败 item={displayName}");
                Plugin.SharedLogger?.Error(ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                request.Completion.TrySetException(ex);
            }
            finally
            {
                lock (QueueSync)
                {
                    if (InFlightExtractions.TryGetValue(request.InternalId, out var current) &&
                        ReferenceEquals(current, request))
                    {
                        InFlightExtractions.Remove(request.InternalId);
                    }

                    StartWorkersInsideLock();
                }
            }
        }

        private static void UpdateWaitingCountInsideLock()
        {
            waitingCount = ExtractionQueue.Count;
            Volatile.Write(ref waitingCount, waitingCount);
        }

        private static int GetMaxConcurrent()
        {
            return Math.Max(1, Volatile.Read(ref maxConcurrentCount));
        }
    }
}
