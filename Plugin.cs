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
        private readonly IJsonSerializer _jsonSerializer;
        private ExternalWebServer _externalWebServer;
        private ExternalWebServerBinding _lastBinding;
        public static string ExternalWebServerStatus { get; private set; } = "Not Enabled";

        public PartySessionRegistry PartyParticipants { get; } = new PartySessionRegistry();
        public PartyReadyRegistry PartyReadyUsers { get; } = new PartyReadyRegistry();
        public WaitingRoomStartCoordinator WaitingRoomStarts { get; } =
            new WaitingRoomStartCoordinator();
        public object ConfigurationSyncRoot { get; } = new object();

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IJsonSerializer jsonSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger(GetType().Name);
            _jsonSerializer = jsonSerializer;
            
            ConfigurationUpdated += OnConfigurationUpdated;
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
                    EnableInUserMenu = true,
                    MenuSection = "server",
                    MenuIcon = "live_tv",
                    DisplayName = "一起看控制台"
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
            if (PluginConfigurationPolicy.Normalize(Configuration))
            {
                SaveConfigurationSafely();
                _logger.Info("[Watch Party] Normalized persisted plugin configuration");
            }

            StartWebServer();
        }

        private void OnConfigurationUpdated(object sender, EventArgs e)
        {
            var currentBinding = ExternalWebServerBinding.From(Configuration);
            
            if (_lastBinding == null || !_lastBinding.Equals(currentBinding))
            {
                _logger.Info("[Watch Party] Web server configuration changed, restarting...");
                RestartWebServer();
            }
        }

        private void StartWebServer()
        {
            var binding = ExternalWebServerBinding.From(Configuration);
            if (binding.Enabled)
            {
                _externalWebServer = new ExternalWebServer(
                    _logger,
                    _jsonSerializer,
                    binding.Port,
                    binding.ListenAddress);
                ExternalWebServerStatus = _externalWebServer.Start();
                _logger.Info($"[Watch Party] External web server status: {ExternalWebServerStatus}");
            }
            else
            {
                ExternalWebServerStatus = "Not Enabled";
                _logger.Info("[Watch Party] External web server is disabled in configuration");
                
            }

            _lastBinding = binding;
        }

        private void RestartWebServer()
        {
            if (_externalWebServer != null)
            {
                _logger.Info("[Watch Party] Stopping existing web server...");
                _externalWebServer.Stop();
                _externalWebServer = null;
            }
            
            StartWebServer();
        }

        public void Dispose()
        {
            _externalWebServer?.Stop();
        }
    }
}
