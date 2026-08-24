using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyManualSynchronizationCoordinatorTests
    {
        [Fact]
        public async Task OnlyOnlineRemoteControlledTargetsAreDispatched()
        {
            var dispatched = new List<string>();
            var coordinator = new PartyManualSynchronizationCoordinator();

            var result = await coordinator.SynchronizeAsync(
                new[]
                {
                    Target("offline", online: false, supported: true),
                    Target("unsupported", online: true, supported: false),
                    Target("ready", online: true, supported: true)
                },
                Operations(
                    dispatch: target =>
                    {
                        dispatched.Add(target.SessionId);
                        return true;
                    }),
                CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.Equal(new[] { "ready" }, dispatched);
            Assert.Equal("客户端不在线", result.Participants[0].Message);
            Assert.Equal("客户端未声明远程播放能力", result.Participants[1].Message);
            Assert.True(result.Participants[2].CommandSent);
        }

        [Fact]
        public async Task DormantTargetIsNotReactivatedWithoutAControlConnection()
        {
            var dispatched = false;
            var coordinator = new PartyManualSynchronizationCoordinator();
            var operations = Operations(dispatch: _ =>
            {
                dispatched = true;
                return true;
            });
            operations.EnsureControlConnectionAsync = (_, __) => Task.FromResult(false);

            var result = await coordinator.SynchronizeAsync(
                new[] { Target("dormant", true, true, dormant: true) },
                operations,
                CancellationToken.None);

            Assert.False(result.Accepted);
            Assert.False(dispatched);
            Assert.Equal("客户端在线，但控制连接未建立", result.Participants[0].Message);
        }

        [Fact]
        public async Task DormantTargetDispatchesAfterConnectionRecoveryWithoutReactivation()
        {
            var events = new List<string>();
            var coordinator = new PartyManualSynchronizationCoordinator();
            var operations = Operations(dispatch: target =>
            {
                events.Add("dispatch:" + target.SessionId);
                return true;
            });
            operations.EnsureControlConnectionAsync = (target, _) =>
            {
                events.Add("connection:" + target.SessionId);
                return Task.FromResult(true);
            };

            var result = await coordinator.SynchronizeAsync(
                new[] { Target("ios", true, true, dormant: true) },
                operations,
                CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.Equal(
                new[] { "connection:ios", "dispatch:ios" },
                events);
        }

        [Fact]
        public async Task PartialSuccessIsReportedWithoutHidingTheFailedClient()
        {
            var coordinator = new PartyManualSynchronizationCoordinator();

            var result = await coordinator.SynchronizeAsync(
                new[]
                {
                    Target("sent", true, true),
                    Target("no-connection", true, true)
                },
                Operations(
                    dispatch: target => target.SessionId == "sent",
                    hasConnection: target => target.SessionId == "sent"),
                CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.Equal("已向 1 台客户端发送同步命令", result.Message);
            Assert.True(result.Participants[0].CommandSent);
            Assert.False(result.Participants[1].CommandSent);
            Assert.Equal("客户端在线，但控制连接未建立", result.Participants[1].Message);
        }

        [Fact]
        public async Task OnlineClientsAreDispatchedInParallel()
        {
            var entered = 0;
            var bothEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new PartyManualSynchronizationCoordinator();
            var operations = Operations(dispatch: _ => true);
            operations.EnsureControlConnectionAsync = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.TrySetResult(true);
                }

                var completed = await Task.WhenAny(
                    bothEntered.Task,
                    Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                return completed == bothEntered.Task;
            };

            var result = await coordinator.SynchronizeAsync(
                new[]
                {
                    Target("first", true, true),
                    Target("second", true, true)
                },
                operations,
                CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.Equal(2, result.Participants.Count(participant => participant.CommandSent));
        }

        private static PartyManualSynchronizationTarget Target(
            string sessionId,
            bool online,
            bool supported,
            bool dormant = false)
        {
            return new PartyManualSynchronizationTarget
            {
                SessionId = sessionId,
                UserName = sessionId,
                Client = "Emby for iOS",
                IsOnline = online,
                SupportsRemotePlayback = supported,
                IsDormant = dormant
            };
        }

        private static PartyManualSynchronizationOperations Operations(
            Func<PartyManualSynchronizationTarget, bool> dispatch,
            Func<PartyManualSynchronizationTarget, bool> hasConnection = null)
        {
            return new PartyManualSynchronizationOperations
            {
                EnsureControlConnectionAsync = (_, __) => Task.FromResult(true),
                DispatchAsync = (target, _) => Task.FromResult(dispatch(target)),
                HasControlConnection = hasConnection ?? (_ => true)
            };
        }
    }
}
