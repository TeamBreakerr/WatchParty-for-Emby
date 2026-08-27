using System;
using System.Linq;

namespace WatchPartyForEmby
{
    public enum PartyEpisodeSelectionDisposition
    {
        SelectWithoutPlayback,
        SelectAndDispatch,
        RejectNotSeriesParty,
        RejectEpisodeNotQueued,
        RejectEpisodeUnavailable,
        AlreadyCurrent,
        RejectNoActiveMaster,
        RejectMasterNotControllable
    }

    public sealed class PartyEpisodeSelectionContext
    {
        public bool IsWaitingRoom { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsEpisodeAvailable { get; set; }
        public bool HasActiveMaster { get; set; }
        public bool MasterCanReceivePlayback { get; set; }
    }

    public sealed class PartyEpisodeSelectionCommit
    {
        public PartyEpisodeSelectionDisposition Disposition { get; set; }
        public WatchPartyEpisode Episode { get; set; }
        public int EpisodeIndex { get; set; } = -1;

        public bool Accepted => Disposition
                == PartyEpisodeSelectionDisposition.SelectWithoutPlayback
            || Disposition == PartyEpisodeSelectionDisposition.SelectAndDispatch;

        public bool ShouldDispatch => Disposition
            == PartyEpisodeSelectionDisposition.SelectAndDispatch;
    }

    /// <summary>
    /// Owns the deterministic state transition for an operator-selected queued
    /// episode. Emby session discovery, command delivery, and persistence remain I/O
    /// adapters in ServerEntryPoint; queue identity and room playback state change here.
    /// </summary>
    public static class PartyEpisodeSelectionCoordinator
    {
        public static PartyEpisodeSelectionCommit TryCommit(
            WatchPartyItem party,
            string episodeItemId,
            PartyEpisodeSelectionContext context)
        {
            if (party?.IsSeriesParty != true)
            {
                return Result(PartyEpisodeSelectionDisposition.RejectNotSeriesParty);
            }

            var selectedEpisode = (party.EpisodeQueue ?? Enumerable.Empty<WatchPartyEpisode>())
                .FirstOrDefault(episode => episode != null
                    && string.Equals(
                        episode.ItemId,
                        episodeItemId,
                        StringComparison.OrdinalIgnoreCase));
            if (selectedEpisode == null)
            {
                return Result(PartyEpisodeSelectionDisposition.RejectEpisodeNotQueued);
            }

            if (context?.IsEpisodeAvailable != true)
            {
                return Result(
                    PartyEpisodeSelectionDisposition.RejectEpisodeUnavailable,
                    selectedEpisode,
                    party.CurrentEpisodeIndex);
            }

            var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
            if (currentEpisode != null
                && string.Equals(
                    currentEpisode.ItemId,
                    selectedEpisode.ItemId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result(
                    PartyEpisodeSelectionDisposition.AlreadyCurrent,
                    selectedEpisode,
                    party.CurrentEpisodeIndex);
            }

            var disposition = DecidePlaybackDisposition(context);
            if (disposition != PartyEpisodeSelectionDisposition.SelectWithoutPlayback
                && disposition != PartyEpisodeSelectionDisposition.SelectAndDispatch)
            {
                return Result(disposition, selectedEpisode, party.CurrentEpisodeIndex);
            }

            if (!SeriesPartyQueue.TrySelectEpisode(party, selectedEpisode.ItemId))
            {
                return Result(PartyEpisodeSelectionDisposition.RejectEpisodeNotQueued);
            }

            party.CurrentPositionTicks = 0;
            party.IsPlaying = disposition
                == PartyEpisodeSelectionDisposition.SelectAndDispatch;
            return Result(
                disposition,
                SeriesPartyQueue.GetCurrentEpisode(party),
                party.CurrentEpisodeIndex);
        }

        private static PartyEpisodeSelectionDisposition DecidePlaybackDisposition(
            PartyEpisodeSelectionContext context)
        {
            if (context == null || context.IsWaitingRoom || !context.IsPlaying)
            {
                return PartyEpisodeSelectionDisposition.SelectWithoutPlayback;
            }

            if (!context.HasActiveMaster)
            {
                return PartyEpisodeSelectionDisposition.RejectNoActiveMaster;
            }

            return context.MasterCanReceivePlayback
                ? PartyEpisodeSelectionDisposition.SelectAndDispatch
                : PartyEpisodeSelectionDisposition.RejectMasterNotControllable;
        }

        private static PartyEpisodeSelectionCommit Result(
            PartyEpisodeSelectionDisposition disposition,
            WatchPartyEpisode episode = null,
            int episodeIndex = -1)
        {
            return new PartyEpisodeSelectionCommit
            {
                Disposition = disposition,
                Episode = episode,
                EpisodeIndex = episodeIndex
            };
        }
    }
}
