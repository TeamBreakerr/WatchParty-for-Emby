using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Stores waiting-room readiness by party and user.
    /// </summary>
    public sealed class PartyReadyRegistry
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, HashSet<string>> _readyUsersByParty =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public void SetReady(string partyId, string userId, bool isReady)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(userId))
            {
                return;
            }

            lock (_syncRoot)
            {
                if (isReady)
                {
                    if (!_readyUsersByParty.TryGetValue(partyId, out var readyUsers))
                    {
                        readyUsers = new HashSet<string>(StringComparer.Ordinal);
                        _readyUsersByParty[partyId] = readyUsers;
                    }

                    readyUsers.Add(userId);
                    return;
                }

                if (_readyUsersByParty.TryGetValue(partyId, out var existingReadyUsers))
                {
                    existingReadyUsers.Remove(userId);
                    if (existingReadyUsers.Count == 0)
                    {
                        _readyUsersByParty.Remove(partyId);
                    }
                }
            }
        }

        public bool IsReady(string partyId, string userId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId)
                    && !string.IsNullOrEmpty(userId)
                    && _readyUsersByParty.TryGetValue(partyId, out var readyUsers)
                    && readyUsers.Contains(userId);
            }
        }

        public IReadOnlyCollection<string> GetReadyUsersSnapshot(string partyId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || !_readyUsersByParty.TryGetValue(partyId, out var readyUsers))
                {
                    return Array.Empty<string>();
                }

                return new List<string>(readyUsers).AsReadOnly();
            }
        }

        public bool RemoveUser(string partyId, string userId)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(userId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_readyUsersByParty.TryGetValue(partyId, out var readyUsers)
                    || !readyUsers.Remove(userId))
                {
                    return false;
                }

                if (readyUsers.Count == 0)
                {
                    _readyUsersByParty.Remove(partyId);
                }

                return true;
            }
        }

        public void ClearParty(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _readyUsersByParty.Remove(partyId);
            }
        }

        public int Count(string partyId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId)
                    && _readyUsersByParty.TryGetValue(partyId, out var readyUsers)
                        ? readyUsers.Count
                        : 0;
            }
        }
    }
}
