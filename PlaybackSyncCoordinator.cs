using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public sealed class PlaybackSyncCoordinator
    {
        private static readonly TimeSpan SeekCooldown = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan PauseEchoWindow = TimeSpan.FromSeconds(8);

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, DateTime> _seekSettlingUntil = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, ExpectedPauseState> _expectedPauseStates = new Dictionary<string, ExpectedPauseState>();
        private readonly Dictionary<string, MasterClockState> _masterClocks = new Dictionary<string, MasterClockState>();

        public bool TryBeginSeek(string sessionId, long targetPositionTicks, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || targetPositionTicks < 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_seekSettlingUntil.TryGetValue(sessionId, out var settlingUntil)
                    && nowUtc < settlingUntil)
                {
                    return false;
                }

                _seekSettlingUntil[sessionId] = nowUtc + SeekCooldown;
                return true;
            }
        }

        public bool IsSeekSettling(string sessionId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_seekSettlingUntil.TryGetValue(sessionId, out var settlingUntil))
                {
                    return false;
                }

                if (nowUtc < settlingUntil)
                {
                    return true;
                }

                _seekSettlingUntil.Remove(sessionId);
                return false;
            }
        }

        public void ExpectPauseState(string sessionId, bool isPaused, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _expectedPauseStates[sessionId] = new ExpectedPauseState
                {
                    IsPaused = isPaused,
                    ExpiresAt = nowUtc + PauseEchoWindow
                };
            }
        }

        public bool ConsumeExpectedPauseState(string sessionId, bool isPaused, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_expectedPauseStates.TryGetValue(sessionId, out var expected))
                {
                    return false;
                }

                if (nowUtc >= expected.ExpiresAt)
                {
                    _expectedPauseStates.Remove(sessionId);
                    return false;
                }

                if (expected.IsPaused != isPaused)
                {
                    return false;
                }

                _expectedPauseStates.Remove(sessionId);
                return true;
            }
        }

        public bool UpdateMasterPosition(
            string partyId,
            long positionTicks,
            bool isPlaying,
            DateTime nowUtc,
            long seekThresholdTicks)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return false;
            }

            positionTicks = Math.Max(0, positionTicks);
            lock (_syncRoot)
            {
                var isSeek = false;
                if (_masterClocks.TryGetValue(partyId, out var previous))
                {
                    var expectedPosition = EstimatePosition(previous, nowUtc);
                    isSeek = Math.Abs(positionTicks - expectedPosition) > Math.Max(0, seekThresholdTicks);
                }

                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = positionTicks,
                    IsPlaying = isPlaying,
                    UpdatedAt = nowUtc
                };
                return isSeek;
            }
        }

        public long GetEstimatedPartyPosition(string partyId, long fallbackPositionTicks, DateTime nowUtc)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId) && _masterClocks.TryGetValue(partyId, out var clock)
                    ? EstimatePosition(clock, nowUtc)
                    : Math.Max(0, fallbackPositionTicks);
            }
        }

        public void StopMasterClock(string partyId, long positionTicks, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = Math.Max(0, positionTicks),
                    IsPlaying = false,
                    UpdatedAt = nowUtc
                };
            }
        }

        public void ClearSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _seekSettlingUntil.Remove(sessionId);
                _expectedPauseStates.Remove(sessionId);
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
                _masterClocks.Remove(partyId);
            }
        }

        public static bool ShouldSynchronizeParticipant(
            bool isActive,
            bool isPlaying,
            bool isWaitingRoom,
            bool isMaster,
            long sessionPositionTicks,
            long partyPositionTicks,
            long toleranceTicks)
        {
            return isActive
                && isPlaying
                && !isWaitingRoom
                && !isMaster
                && Math.Abs(sessionPositionTicks - partyPositionTicks) > Math.Max(0, toleranceTicks);
        }

        private static long EstimatePosition(MasterClockState clock, DateTime nowUtc)
        {
            if (!clock.IsPlaying || nowUtc <= clock.UpdatedAt)
            {
                return clock.PositionTicks;
            }

            return clock.PositionTicks + (nowUtc - clock.UpdatedAt).Ticks;
        }

        private sealed class ExpectedPauseState
        {
            public bool IsPaused { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        private sealed class MasterClockState
        {
            public long PositionTicks { get; set; }
            public bool IsPlaying { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}
