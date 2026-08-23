using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public sealed class WatchPartyMediaIdentity
    {
        public Guid ItemId { get; set; }

        public long InternalItemId { get; set; }

        public string PresentationUniqueKey { get; set; }

        public IReadOnlyCollection<string> MediaSourceItemIds { get; set; } =
            Array.Empty<string>();
    }
}
