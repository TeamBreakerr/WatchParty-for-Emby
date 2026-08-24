using System;

namespace WatchPartyForEmby
{
    public static class PluginConfigurationMigration
    {
        public const int DirectItemBindingVersion = 2;
        public const int EmbeddedControlCenterVersion = 3;

        /// <summary>
        /// Forces one post-upgrade configuration rewrite after the external console
        /// model is retired. XmlSerializer ignores removed elements while reading;
        /// saving version 3 removes the obsolete listener credentials and settings
        /// from disk without changing any room records.
        /// </summary>
        public static bool UpgradeToEmbeddedControlCenter(
            PluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (configuration.ConfigurationVersion >= EmbeddedControlCenterVersion)
            {
                return false;
            }

            // Preserve migration ordering for installations that jump directly
            // from a pre-v2 build to the embedded-control-center release.
            UpgradeLegacyRooms(configuration);
            configuration.ConfigurationVersion = EmbeddedControlCenterVersion;
            return true;
        }

        /// <summary>
        /// Marks the direct-item binding migration complete without deleting the
        /// persisted rooms. Room records already contain the original Emby ItemId;
        /// deleting them on startup was an irreversible data-loss bug.
        /// </summary>
        public static int UpgradeLegacyRooms(PluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (configuration.ConfigurationVersion >= DirectItemBindingVersion)
            {
                return 0;
            }

            var preservedRoomCount = configuration.WatchParties?.Count ?? 0;
            configuration.WatchParties ??= new System.Collections.Generic.List<WatchPartyItem>();
            configuration.ConfigurationVersion = DirectItemBindingVersion;
            return preservedRoomCount;
        }

        [Obsolete("Use UpgradeLegacyRooms; legacy rooms are preserved during migration.")]
        public static int ResetLegacyRooms(PluginConfiguration configuration)
        {
            return UpgradeLegacyRooms(configuration);
        }
    }
}
