using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ProviderBurstPacerTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

        private sealed class ManualClock
        {
            public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

            public DateTime Read() => UtcNow;
        }

        [Fact]
        public void ATwoPersonPartyIsNeverDelayed()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(2, Window, utcNow: clock.Read);

            Assert.Equal(TimeSpan.Zero, pacer.Reserve("party"));
            Assert.Equal(TimeSpan.Zero, pacer.Reserve("party"));
        }

        [Fact]
        public void CommandsAreReleasedTwoPerWindow()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(2, Window, utcNow: clock.Read);

            var delays = new List<TimeSpan>();
            for (var i = 0; i < 5; i++)
            {
                delays.Add(pacer.Reserve("party"));
            }

            // The provider serves two reads that start together, so a party of five
            // goes out as 2 + 2 + 1 rather than as one burst of five.
            Assert.Equal(
                new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    Window,
                    Window,
                    TimeSpan.FromTicks(Window.Ticks * 2)
                },
                delays);
        }

        [Fact]
        public void ATrickleOfCommandsIsNeverDelayed()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(2, Window, utcNow: clock.Read);

            for (var i = 0; i < 6; i++)
            {
                Assert.Equal(TimeSpan.Zero, pacer.Reserve("party"));
                clock.UtcNow += Window;
            }
        }

        [Fact]
        public void AFullBurstStillReleasesImmediatelyOnceTheWindowHasPassed()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(2, Window, utcNow: clock.Read);

            pacer.Reserve("party");
            pacer.Reserve("party");
            Assert.Equal(Window, pacer.Reserve("party"));

            clock.UtcNow += TimeSpan.FromTicks(Window.Ticks * 3);
            Assert.Equal(TimeSpan.Zero, pacer.Reserve("party"));
        }

        [Fact]
        public void PartiesDoNotPaceEachOther()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(2, Window, utcNow: clock.Read);

            pacer.Reserve("party-a");
            pacer.Reserve("party-a");
            Assert.Equal(Window, pacer.Reserve("party-a"));
            Assert.Equal(TimeSpan.Zero, pacer.Reserve("party-b"));
        }

        [Theory]
        [InlineData(ParticipantRoomCommand.PlayNow)]
        [InlineData(ParticipantRoomCommand.Resume)]
        [InlineData(ParticipantRoomCommand.Seek)]
        public void CommandsThatOpenAProviderReadArePaced(ParticipantRoomCommand command)
        {
            Assert.True(ProviderBurstPacer.OpensProviderRead(command));
        }

        [Theory]
        [InlineData(ParticipantRoomCommand.Pause)]
        [InlineData(ParticipantRoomCommand.Stop)]
        public void CommandsThatEndAReadAreNotPaced(ParticipantRoomCommand command)
        {
            // Delaying these would cost synchronization accuracy without ever
            // preventing a provider rejection.
            Assert.False(ProviderBurstPacer.OpensProviderRead(command));
        }

        [Fact]
        public async Task PaceAsyncSkipsTheClockForCommandsThatEndARead()
        {
            var clock = new ManualClock();
            var delays = new List<TimeSpan>();
            var pacer = new ProviderBurstPacer(
                1,
                Window,
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                },
                clock.Read);

            await pacer.PaceAsync("party", ParticipantRoomCommand.Seek, CancellationToken.None);
            await pacer.PaceAsync("party", ParticipantRoomCommand.Seek, CancellationToken.None);
            await pacer.PaceAsync("party", ParticipantRoomCommand.Pause, CancellationToken.None);
            await pacer.PaceAsync("party", ParticipantRoomCommand.Stop, CancellationToken.None);

            Assert.Equal(new[] { Window }, delays);
        }

        [Fact]
        public async Task PacingPropagatesCancellation()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(
                1,
                Window,
                (delay, token) => Task.FromCanceled(token),
                clock.Read);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await pacer.PaceAsync("party", ParticipantRoomCommand.Seek, cts.Token);

                await Assert.ThrowsAsync<TaskCanceledException>(
                    () => pacer.PaceAsync(
                        "party",
                        ParticipantRoomCommand.Seek,
                        cts.Token));
            }
        }

        [Fact]
        public void IdleWindowsAreEventuallyPruned()
        {
            var clock = new ManualClock();
            var pacer = new ProviderBurstPacer(2, Window, utcNow: clock.Read);

            for (var i = 0; i < 40; i++)
            {
                pacer.Reserve("party-" + i);
            }

            Assert.True(pacer.TrackedKeyCount > 0);
            clock.UtcNow += TimeSpan.FromTicks(Window.Ticks * 10);
            pacer.Reserve("party-fresh");

            Assert.Equal(1, pacer.TrackedKeyCount);
        }

        [Fact]
        public void AnInvalidConfigurationIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ProviderBurstPacer(0, Window));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ProviderBurstPacer(2, TimeSpan.Zero));
        }
    }
}
