using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Drawing;

namespace WatchPartyForEmby
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IServerEntryPoint, IHasThumbImage
    {
        public event EventHandler ConfigurationUpdated;
        private readonly ILogger _logger;

        public PartySessionRegistry PartyParticipants { get; } = new PartySessionRegistry();
        public PartyReadyRegistry PartyReadyUsers { get; } = new PartyReadyRegistry();
        public WaitingRoomStartCoordinator WaitingRoomStarts { get; } =
            new WaitingRoomStartCoordinator();
        public object ConfigurationSyncRoot { get; } = new object();

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogManager logManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger(GetType().Name);
        }

        public override string Name => "一起看";

        public override string Description => "创建并管理同步播放的一起看房间";

        public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d");

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".images.logo.jpg");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

        public static Plugin Instance { get; private set; }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            lock (ConfigurationSyncRoot)
            {
                if (configuration is PluginConfiguration pluginConfiguration)
                {
                    PluginConfigurationPolicy.Normalize(pluginConfiguration);

                    if (WatchPartyItemMatcher.HasActiveBindingConflict(pluginConfiguration.WatchParties))
                    {
                        throw new InvalidOperationException(
                            "An original Emby item cannot belong to more than one active watch party.");
                    }
                }

                base.UpdateConfiguration(configuration);
            }

            ConfigurationUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void SaveConfigurationSafely()
        {
            lock (ConfigurationSyncRoot)
            {
                SaveConfiguration();
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "watchpartyconfig",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = true,
                    MenuSection = "server",
                    MenuIcon = "live_tv",
                    DisplayName = "一起看"
                },
                new PluginPageInfo
                {
                    Name = "configPagejs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }

        public void Run()
        {
            var upgradedToEmbeddedControlCenter =
                PluginConfigurationMigration.UpgradeToEmbeddedControlCenter(
                    Configuration);
            var normalizedConfiguration =
                PluginConfigurationPolicy.Normalize(Configuration);
            if (normalizedConfiguration || upgradedToEmbeddedControlCenter)
            {
                SaveConfigurationSafely();
                _logger.Info(upgradedToEmbeddedControlCenter
                    ? "[Watch Party] Upgraded configuration to the embedded control center schema"
                    : "[Watch Party] Normalized persisted plugin configuration");
            }

        }

        public void Dispose()
        {
        }
    }
}
