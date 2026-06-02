using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public class MediaInfoService
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IMediaSourceManager mediaSourceManager;
        private readonly IFileSystem fileSystem;

        /// <summary>创建 MediaInfo 处理辅助类并注入所需服务。</summary>
        public MediaInfoService(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            IFileSystem fileSystem)
        {
            this.logger = Plugin.SharedLogger;
            this.libraryManager = libraryManager;
            this.mediaSourceManager = mediaSourceManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>判断条目是否已存在可用的 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (item is not IHasMediaSources)
            {
                return false;
            }

            foreach (var source in GetStaticMediaSources(item, false))
            {
                if (source?.RunTimeTicks.HasValue != true)
                {
                    continue;
                }

                foreach (var stream in source.MediaStreams ?? Enumerable.Empty<MediaStream>())
                {
                    if (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Video)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>判断条目当前 MediaInfo 中是否存在音频流。</summary>
        public bool HasAudioStream(BaseItem item)
        {
            return HasStreamType(item, MediaStreamType.Audio);
        }

        /// <summary>判断条目当前 MediaInfo 中是否存在视频流。</summary>
        public bool HasVideoStream(BaseItem item)
        {
            return HasStreamType(item, MediaStreamType.Video);
        }

        /// <summary>构建 MediaInfo 提取所需的刷新选项。</summary>
        public MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = Plugin.Instance.Options.MetaData.EnableImageCapture,
                EnableSubtitleDownloading = false
            };
        }

        /// <summary>为单个视频或音频条目提取媒体信息。</summary>
        public async Task<bool> ExtractMediaInfoAsync(BaseItem item, string source, CancellationToken cancellationToken = default)
        {
            if (!(item is Video) && !(item is Audio))
            {
                return false;
            }

            var displayName = item.FileName ?? item.Path ?? item.Name;
            using (FfProcessGuard.Allow())
            {
                var filePath = item.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    this.logger.Info($"{source} 提取媒体信息跳过 无路径: {displayName}");
                    return false;
                }

                var refreshOptions = GetMediaInfoRefreshOptions();
                var directoryService = refreshOptions.DirectoryService;

                if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                    uri.Scheme == Uri.UriSchemeFile)
                {
                    var file = directoryService.GetFile(filePath);
                    if (file?.Exists != true)
                    {
                        this.logger.Info($"{source} 提取媒体信息跳过 文件不存在: {displayName}");
                        return false;
                    }
                }

                var deserializeResult = Plugin.MediaSourceInfoStore.ApplyToItem(item);
                if (item is Video)
                {
                    Plugin.ChaptersStore.ApplyToItem(item);
                }
                else if (item is Audio)
                {
                    Plugin.EmbeddedInfoStore.ApplyToItem(item);
                }

                if (deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.Restored ||
                    deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists)
                {
                    this.logger.Info($"{source} 提取媒体信息继续执行刷新: {displayName}");
                }

                var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
                var libraryOptions = this.libraryManager.GetLibraryOptions(item);
                var copiedOptions = LibraryService.CopyLibraryOptions(libraryOptions);

                item.DateLastRefreshed = new DateTimeOffset();
                await RefreshTaskRunner.RunAsync(
                        () => Plugin.ProviderManager
                            .RefreshSingleItem(item, refreshOptions, collectionFolders, copiedOptions, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!HasMediaInfo(item))
                {
                    this.logger.Info($"{source} 提取媒体信息失败 无媒体流: {displayName}");
                    return false;
                }

                return true;
            }
        }

        /// <summary>获取指定条目的静态媒体源。</summary>
        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            if (item is not IHasMediaSources)
            {
                return new List<MediaSourceInfo>();
            }

            var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);

            return this.mediaSourceManager.GetStaticMediaSources(
                item,
                enableAlternateMediaSources,
                enablePathSubstitution: false,
                fillChapters: false,
                collectionFolders: collectionFolders,
                libraryOptions: libraryOptions,
                deviceProfile: null,
                user: null);
        }

        private bool HasStreamType(BaseItem item, MediaStreamType streamType)
        {
            if (item is not IHasMediaSources)
            {
                return false;
            }

            foreach (var source in GetStaticMediaSources(item, false))
            {
                foreach (var stream in source?.MediaStreams ?? Enumerable.Empty<MediaStream>())
                {
                    if (stream.Type == streamType)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
