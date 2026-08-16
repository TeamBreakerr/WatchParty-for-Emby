using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Runs asynchronous operations in submission order for each key while allowing
    /// unrelated keys to progress independently. Each key has a fixed outstanding-work
    /// limit, and idle key state is removed automatically after its final operation.
    /// </summary>
    public sealed class KeyedAsyncSerialQueue<TKey> where TKey : notnull
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<TKey, KeyQueue> _queues = new Dictionary<TKey, KeyQueue>();
        private readonly int _capacityPerKey;

        public KeyedAsyncSerialQueue(int capacityPerKey)
        {
            if (capacityPerKey <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacityPerKey));
            }

            _capacityPerKey = capacityPerKey;
        }

        /// <summary>
        /// Gets the number of keys with running or queued work. This is intended for
        /// operational diagnostics and returns to zero after all work becomes idle.
        /// </summary>
        public int ActiveKeyCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _queues.Count;
                }
            }
        }

        /// <summary>
        /// Attempts to submit one operation. A false result means the key already has
        /// <c>capacityPerKey</c> outstanding operations; the supplied operation was not run.
        /// </summary>
        public bool TryEnqueue(
            TKey key,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken,
            out Task completion)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                completion = Task.FromCanceled(cancellationToken);
                return true;
            }

            var item = new WorkItem(operation, cancellationToken);
            KeyQueue queue;
            var startProcessor = false;

            lock (_syncRoot)
            {
                if (!_queues.TryGetValue(key, out queue))
                {
                    queue = new KeyQueue();
                    _queues.Add(key, queue);
                }

                if (queue.OutstandingCount >= _capacityPerKey)
                {
                    completion = Task.CompletedTask;
                    return false;
                }

                queue.Items.Enqueue(item);
                queue.OutstandingCount++;
                if (!queue.IsProcessing)
                {
                    queue.IsProcessing = true;
                    startProcessor = true;
                }
            }

            completion = item.Completion.Task;
            if (startProcessor)
            {
                _ = ProcessQueueAsync(key, queue);
            }

            return true;
        }

        private async Task ProcessQueueAsync(TKey key, KeyQueue queue)
        {
            while (true)
            {
                WorkItem item;
                lock (_syncRoot)
                {
                    item = queue.Items.Dequeue();
                }

                Exception failure = null;
                var wasCanceled = false;
                try
                {
                    item.CancellationToken.ThrowIfCancellationRequested();
                    await item.Operation(item.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    wasCanceled = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                var hasMoreWork = false;
                lock (_syncRoot)
                {
                    queue.OutstandingCount--;
                    hasMoreWork = queue.Items.Count > 0;
                    if (!hasMoreWork)
                    {
                        queue.IsProcessing = false;
                        if (queue.OutstandingCount == 0
                            && _queues.TryGetValue(key, out var current)
                            && ReferenceEquals(current, queue))
                        {
                            _queues.Remove(key);
                        }
                    }
                }

                if (wasCanceled)
                {
                    item.Completion.TrySetCanceled();
                }
                else if (failure != null)
                {
                    item.Completion.TrySetException(failure);
                }
                else
                {
                    item.Completion.TrySetResult(true);
                }

                if (!hasMoreWork)
                {
                    return;
                }
            }
        }

        private sealed class KeyQueue
        {
            public Queue<WorkItem> Items { get; } = new Queue<WorkItem>();

            public int OutstandingCount { get; set; }

            public bool IsProcessing { get; set; }
        }

        private sealed class WorkItem
        {
            public WorkItem(
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken)
            {
                Operation = operation;
                CancellationToken = cancellationToken;
                Completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public Func<CancellationToken, Task> Operation { get; }

            public CancellationToken CancellationToken { get; }

            public TaskCompletionSource<bool> Completion { get; }
        }
    }
}
