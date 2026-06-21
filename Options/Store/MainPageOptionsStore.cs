namespace MediaInfoKeeper.Options.Store
{
    using Emby.Web.GenericEdit.Elements;
    using MediaBrowser.Model.GenericEdit;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Services;

    internal class MainPageOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public MainPageOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public MainPageOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            var mainPage = options.MainPage ?? new MainPageOptions();
            mainPage.ScheduledTasksEditor ??= new MainPageOptions.ScheduledTaskEditorOptions();
            mainPage.RefreshQueueStatus = BuildRefreshQueueStatus();
            return mainPage;
        }

        public void SetOptions(MainPageOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.MainPage = options ?? new MainPageOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }

        private static StatusItem BuildRefreshQueueStatus()
        {
            var metadataStats = MetaDataRunner.GetQueueStats();
            var mediaInfoStats = MediaInfoRunner.GetQueueStats();

            return new StatusItem(
                "刷新队列",
                $"元数据刷新：{metadataStats.Running} / {metadataStats.MaxConcurrent}  · {metadataStats.Waiting} 等待\n" +
                $"媒体信息提取：{mediaInfoStats.Running} / {mediaInfoStats.MaxConcurrent}  · {mediaInfoStats.Waiting} 等待",
                ItemStatus.Succeeded);
        }
    }
}
