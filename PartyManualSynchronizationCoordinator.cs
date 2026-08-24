using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public sealed class PartyManualSynchronizationTarget
    {
        public string SessionId { get; set; }
        public string UserName { get; set; }
        public string Client { get; set; }
        public bool IsOnline { get; set; }
        public bool SupportsRemotePlayback { get; set; }
        public bool IsDormant { get; set; }
    }

    public sealed class PartyManualSynchronizationOperations
    {
        public Func<PartyManualSynchronizationTarget, CancellationToken, Task<bool>>
            EnsureControlConnectionAsync { get; set; }
        public Func<PartyManualSynchronizationTarget, CancellationToken, Task<bool>>
            DispatchAsync { get; set; }
        public Func<PartyManualSynchronizationTarget, bool> HasControlConnection { get; set; }
        public Action<PartyManualSynchronizationTarget, Exception> OnDispatchError { get; set; }
    }

    /// <summary>
    /// Owns the explicit manual synchronization workflow. A manual command can reach a
    /// newly discovered or dormant session, but this coordinator never changes
    /// dormancy; only that client's trusted Start/Progress may restore it to automatic
    /// master broadcasts.
    /// </summary>
    public sealed class PartyManualSynchronizationCoordinator
    {
        public async Task<PartyManualSynchronizationResult> SynchronizeAsync(
            IEnumerable<PartyManualSynchronizationTarget> targets,
            PartyManualSynchronizationOperations operations,
            CancellationToken cancellationToken)
        {
            if (operations?.EnsureControlConnectionAsync == null
                || operations.DispatchAsync == null
                || operations.HasControlConnection == null)
            {
                throw new ArgumentException("Complete synchronization operations are required");
            }

            var targetList = (targets ?? Array.Empty<PartyManualSynchronizationTarget>())
                .Where(target => target != null && !string.IsNullOrEmpty(target.SessionId))
                .ToList();
            var participantTasks = targetList
                .Select(target => SynchronizeTargetAsync(target, operations, cancellationToken))
                .ToArray();
            var participants = await Task.WhenAll(participantTasks).ConfigureAwait(false);
            var result = new PartyManualSynchronizationResult();
            result.Participants.AddRange(participants);

            var sentCount = result.Participants.Count(participant => participant.CommandSent);
            result.Accepted = sentCount > 0;
            result.Message = result.Participants.Count == 0
                ? "房间中没有可同步的在线客户端"
                : sentCount > 0
                    ? $"已向 {sentCount} 台客户端发送同步命令"
                    : "没有客户端具备可用的控制连接";
            return result;
        }

        private static async Task<ParticipantManualSynchronizationResult> SynchronizeTargetAsync(
            PartyManualSynchronizationTarget target,
            PartyManualSynchronizationOperations operations,
            CancellationToken cancellationToken)
        {
            var outcome = new ParticipantManualSynchronizationResult
            {
                SessionId = target.SessionId,
                UserName = target.UserName,
                Client = target.Client,
                Online = target.IsOnline
            };

            if (!target.IsOnline)
            {
                outcome.Message = "客户端不在线";
                return outcome;
            }
            if (!target.SupportsRemotePlayback)
            {
                outcome.Message = "客户端未声明远程播放能力";
                return outcome;
            }

            try
            {
                outcome.HasControlConnection = await operations
                    .EnsureControlConnectionAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (!outcome.HasControlConnection)
                {
                    outcome.Message = "客户端在线，但控制连接未建立";
                    return outcome;
                }

                outcome.CommandSent = await operations
                    .DispatchAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                outcome.HasControlConnection = operations.HasControlConnection(target);
                outcome.Message = outcome.CommandSent
                    ? "已发送当前内容和进度"
                    : outcome.HasControlConnection
                        ? "命令未被发送"
                        : "客户端在线，但控制连接未建立";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome.Message = "发送失败：" + ex.Message;
                operations.OnDispatchError?.Invoke(target, ex);
            }

            return outcome;
        }
    }
}
