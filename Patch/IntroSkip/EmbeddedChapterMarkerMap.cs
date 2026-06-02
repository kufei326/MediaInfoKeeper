using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 将 ffprobe 读到的内嵌章节名映射为 Emby 片头片尾标记。
    /// 规则：
    /// Intro -> IntroStart，优先用 Intro 章结束点补一个 IntroEnd；无结束点时退回到下一章起点。
    /// Credits -> CreditsStart。
    /// </summary>
    public static class EmbeddedChapterMarkerMap
    {
        private static readonly HashSet<string> IntroNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "intro",
            "opening",
            "op",
            "片头",
            "片头曲",
            "オープニング",
            "vorspann",
            "opening credits",
            "오프닝"
        };

        private static readonly HashSet<string> CreditsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "credits",
            "end credits",
            "outro",
            "ending",
            "ed",
            "片尾",
            "片尾曲",
            "演职员表",
            "エンディング",
            "スタッフロール",
            "abspann",
            "엔딩",
            "크레딧"
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getMediaInfo;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
                var probeResultNormalizerType = mediaEncodingAssembly?.GetType("Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer");
                var version = mediaEncodingAssembly?.GetName().Version;
                var probeResultType = Assembly.Load("Emby.Media.Model")?.GetType("Emby.Media.Model.ProbeModel.ProbeResult");

                getMediaInfo = PatchMethodResolver.Resolve(
                    probeResultNormalizerType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "proberesultnormalizer-getmediainfo-exact",
                        MethodName = "GetMediaInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { probeResultType, typeof(bool), typeof(string), typeof(MediaBrowser.Model.MediaInfo.MediaProtocol) },
                        ReturnType = typeof(MediaBrowser.Model.MediaInfo.MediaInfo)
                    },
                    logger,
                    "EmbeddedChapterMarkerMap.GetMediaInfo");

                if (getMediaInfo == null)
                {
                    PatchLog.InitFailed(logger, nameof(EmbeddedChapterMarkerMap), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.embeddedchaptermarkermap");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("EmbeddedChapterMarkerMap 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

            if (harmony == null)
            {
                return;
            }

            if (isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(
                getMediaInfo,
                postfix: new HarmonyMethod(typeof(EmbeddedChapterMarkerMap), nameof(GetMediaInfoPostfix)));

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(getMediaInfo, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetMediaInfoPostfix(object __0, MediaBrowser.Model.MediaInfo.MediaInfo __result)
        {
            if (!isEnabled || __0 == null || __result == null)
            {
                return;
            }

            try
            {
                var chapters = __result.Chapters?.ToList();
                var probeChapters = GetProbeChapters(__0);
                if (chapters == null || probeChapters == null || chapters.Count == 0 || probeChapters.Count == 0)
                {
                    return;
                }

                ApplyMarkerMapping(chapters, probeChapters);
                __result.Chapters = chapters.ToArray();
            }
            catch (Exception ex)
            {
                logger?.Error("EmbeddedChapterMarkerMap 处理失败。");
                logger?.Error(ex.Message);
                logger?.Debug(ex.StackTrace);
            }
        }

        private static List<object> GetProbeChapters(object probeResult)
        {
            var field = probeResult.GetType().GetField("chapters", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var property = probeResult.GetType().GetProperty("chapters", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = field?.GetValue(probeResult) ?? property?.GetValue(probeResult);

            if (value is IEnumerable enumerable)
            {
                return enumerable.Cast<object>().Where(i => i != null).ToList();
            }

            return null;
        }

        private static void ApplyMarkerMapping(List<ChapterInfo> chapters, List<object> probeChapters)
        {
            var count = Math.Min(chapters.Count, probeChapters.Count);
            if (count == 0)
            {
                return;
            }

            var addedMarkers = 0;
            var mappedMarkers = new List<string>();
            var orderedPairs = Enumerable.Range(0, count)
                .Select(i => new ChapterPair(chapters[i], probeChapters[i]))
                .Where(i => i.Chapter != null)
                .OrderBy(i => i.Chapter.StartPositionTicks)
                .ToList();

            for (var i = 0; i < orderedPairs.Count; i++)
            {
                var pair = orderedPairs[i];
                var normalizedName = NormalizeName(pair.Chapter.Name);
                if (string.IsNullOrEmpty(normalizedName))
                {
                    continue;
                }

                if (IntroNames.Contains(normalizedName))
                {
                    var hasIntroStart = pair.Chapter.MarkerType == MarkerType.IntroStart;
                    if (pair.Chapter.MarkerType == MarkerType.Chapter)
                    {
                        pair.Chapter.MarkerType = MarkerType.IntroStart;
                        addedMarkers++;
                        hasIntroStart = true;
                        mappedMarkers.Add($"IntroStart={FormatTicks(pair.Chapter.StartPositionTicks)}");
                    }

                    if (hasIntroStart)
                    {
                        var introEndTicks = ResolveIntroEndTicks(orderedPairs, i, pair);
                        if (introEndTicks.HasValue &&
                            introEndTicks.Value > pair.Chapter.StartPositionTicks &&
                            !chapters.Any(c => c != null &&
                                               c.MarkerType == MarkerType.IntroEnd &&
                                               c.StartPositionTicks == introEndTicks.Value))
                        {
                            chapters.Add(new ChapterInfo
                            {
                                Name = "IntroEnd",
                                StartPositionTicks = introEndTicks.Value,
                                MarkerType = MarkerType.IntroEnd
                            });
                            addedMarkers++;
                            mappedMarkers.Add($"IntroEnd={FormatTicks(introEndTicks.Value)}");
                        }
                    }

                    continue;
                }

                if (CreditsNames.Contains(normalizedName) &&
                    pair.Chapter.MarkerType == MarkerType.Chapter)
                {
                    pair.Chapter.MarkerType = MarkerType.CreditsStart;
                    addedMarkers++;
                    mappedMarkers.Add($"CreditsStart={FormatTicks(pair.Chapter.StartPositionTicks)}");
                }
            }

            if (addedMarkers > 0)
            {
                chapters.Sort((left, right) => left.StartPositionTicks.CompareTo(right.StartPositionTicks));
                logger?.Info("EmbeddedChapterMarkerMap 内嵌章节信息映射完成: {0}", mappedMarkers.Count == 0 ? "<none>" : string.Join(", ", mappedMarkers));
            }
        }

        private static long? ResolveIntroEndTicks(IReadOnlyList<ChapterPair> orderedPairs, int index, ChapterPair pair)
        {
            var endTicks = GetProbeChapterTicks(pair.ProbeChapter, "end_time");
            if (endTicks.HasValue && endTicks.Value > pair.Chapter.StartPositionTicks)
            {
                return endTicks.Value;
            }

            var nextPair = index + 1 < orderedPairs.Count ? orderedPairs[index + 1] : null;
            if (nextPair?.Chapter != null && nextPair.Chapter.StartPositionTicks > pair.Chapter.StartPositionTicks)
            {
                return nextPair.Chapter.StartPositionTicks;
            }

            return null;
        }

        private static long? GetProbeChapterTicks(object probeChapter, string propertyName)
        {
            if (probeChapter == null)
            {
                return null;
            }

            var property = probeChapter.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = property?.GetValue(probeChapter);
            if (value == null)
            {
                return null;
            }

            if (value is decimal decimalValue)
            {
                return TimeSpan.FromSeconds((double)decimalValue).Ticks;
            }

            if (decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return TimeSpan.FromSeconds((double)parsed).Ticks;
            }

            return null;
        }

        private static string NormalizeName(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? null
                : name.Trim();
        }

        private static string FormatTicks(long ticks)
        {
            return TimeSpan.FromTicks(ticks).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private sealed class ChapterPair
        {
            public ChapterPair(ChapterInfo chapter, object probeChapter)
            {
                Chapter = chapter;
                ProbeChapter = probeChapter;
            }

            public ChapterInfo Chapter { get; }

            public object ProbeChapter { get; }
        }
    }
}
