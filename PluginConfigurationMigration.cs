using System;

namespace WatchPartyForEmby
{
    public static class PluginConfigurationMigration
    {
        public const int DirectItemBindingVersion = 2;

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
