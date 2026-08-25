using Xunit;
using MediaBrowser.Model.Session;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackGenerationAdoptionPolicyTests
    {
        [Fact]
        public void QualityChangeIsAnExplicitReplacementSignal()
        {
            Assert.True(PlaybackGenerationAdoptionPolicy.IsExplicitStreamChange(
                ProgressEvent.QualityChange));
        }

        [Fact]
        public void SubtitleTrackChangeIsAnExplicitReplacementSignal()
        {
            Assert.True(PlaybackGenerationAdoptionPolicy.IsExplicitStreamChange(
                ProgressEvent.SubtitleTrackChange));
        }

        [Fact]
        public void AudioTrackChangeIsAnExplicitReplacementSignal()
        {
            Assert.True(PlaybackGenerationAdoptionPolicy.IsExplicitStreamChange(
                ProgressEvent.AudioTrackChange));
        }

        [Fact]
        public void OrdinaryProgressAndMissingEventsAreNotReplacementSignals()
        {
            Assert.False(PlaybackGenerationAdoptionPolicy.IsExplicitStreamChange(
                ProgressEvent.TimeUpdate));
            Assert.False(PlaybackGenerationAdoptionPolicy.IsExplicitStreamChange(
                default(ProgressEvent)));
        }

        [Fact]
        public void AdoptedGenerationCannotCreateImmediateOrDelayedPauseTransition()
        {
            Assert.False(PlaybackGenerationAdoptionPolicy.ShouldApplyPauseTransition(
                adoptedPlaybackGeneration: true,
                isMaster: true,
                deferMasterPauseTransition: false,
                ignoreMasterPauseTransition: false));
            Assert.False(PlaybackGenerationAdoptionPolicy.ShouldApplyPauseTransition(
                adoptedPlaybackGeneration: true,
                isMaster: true,
                deferMasterPauseTransition: true,
                ignoreMasterPauseTransition: false));
        }

        [Fact]
        public void QualityChangePreservesThePreviouslyKnownPauseState()
        {
            Assert.True(PlaybackGenerationAdoptionPolicy.ResolveEffectivePauseState(
                adoptedPlaybackGeneration: true,
                previousPauseState: true,
                reportedPauseState: false,
                applyPauseTransition: false));
            Assert.False(PlaybackGenerationAdoptionPolicy.ResolveEffectivePauseState(
                adoptedPlaybackGeneration: true,
                previousPauseState: false,
                reportedPauseState: true,
                applyPauseTransition: false));
        }

        [Fact]
        public void OrdinaryParticipantAndStableMasterTransitionsRemainApplicable()
        {
            Assert.True(PlaybackGenerationAdoptionPolicy.ShouldApplyPauseTransition(
                adoptedPlaybackGeneration: false,
                isMaster: false,
                deferMasterPauseTransition: false,
                ignoreMasterPauseTransition: false));
            Assert.True(PlaybackGenerationAdoptionPolicy.ShouldApplyPauseTransition(
                adoptedPlaybackGeneration: false,
                isMaster: true,
                deferMasterPauseTransition: false,
                ignoreMasterPauseTransition: false));
            Assert.False(PlaybackGenerationAdoptionPolicy.ShouldApplyPauseTransition(
                adoptedPlaybackGeneration: false,
                isMaster: true,
                deferMasterPauseTransition: false,
                ignoreMasterPauseTransition: true));
        }
    }
}
