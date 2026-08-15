using System;

namespace WatchPartyForEmby
{
    public static class PluginConfigurationMigration
    {
        public const int DirectItemBindingVersion = 2;

        public static int ResetLegacyRooms(PluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (configuration.ConfigurationVersion >= DirectItemBindingVersion)
            {
                return 0;
            }

            var removedRoomCount = configuration.WatchParties?.Count ?? 0;
            configuration.WatchParties?.Clear();
            configuration.ConfigurationVersion = DirectItemBindingVersion;
            return removedRoomCount;
        }
    }
}
