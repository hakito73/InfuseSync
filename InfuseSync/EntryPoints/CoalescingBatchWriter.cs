using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfuseSync.EntryPoints
{
    internal sealed class BatchStopResult
    {
        public BatchStopResult(Exception error, int unsavedCount, int attempts)
            => (Error, UnsavedCount, Attempts) = (error, unsavedCount, attempts);

        public bool Succeeded => Error == null;
        public Exception Error { get; }
        public int UnsavedCount { get; }
        public int Attempts { get; }
    }

    internal sealed class CoalescingBatchWriter<TKey, TValue>
    {
        private const int ShutdownWriteAttempts = 3;

        private sealed class Batch
        {
            public Batch(Dictionary<TKey, TValue> items, DateTime startedAtUtc)
                => (Items, StartedAtUtc) = (items, startedAtUtc);

            public Dictionary<TKey, TValue> Items { get; }
            public DateTime StartedAtUtc { get; }
        }

        private readonly object _syncLock = new object();
        private readonly TimeSpan _debounceDelay;
        private readonly TimeSpan _maximumDelay;
        private readonly TimeSpan _retryDelay;
        private readonly Func<TValue, TValue, TValue> _merge;
        private readonly Action<IReadOnlyCollection<TValue>> _writeBatch;
        private readonly Action<Exception, int> _writeError;
        private readonly Timer _timer;

        private Dictionary<TKey, TValue> _pending = new Dictionary<TKey, TValue>();
        private Batch _inFlight;
        private Task<Exception> _activeWrite;
        private DateTime? _batchStartedAtUtc;
        private DateTime? _retryNotBeforeUtc;
        private BatchStopResult _stopResult;
        private bool _isStopping;

        public CoalescingBatchWriter(
            TimeSpan debounceDelay,
            TimeSpan maximumDelay,
            TimeSpan retryDelay,
            Func<TValue, TValue, TValue> merge,
            Action<IReadOnlyCollection<TValue>> writeBatch,
            Action<Exception, int> writeError)
        {
            _debounceDelay = debounceDelay;
            _maximumDelay = maximumDelay;
            _retryDelay = retryDelay;
            _merge = merge ?? throw new ArgumentNullException(nameof(merge));
            _writeBatch = writeBatch ?? throw new ArgumentNullException(nameof(writeBatch));
            _writeError = writeError ?? throw new ArgumentNullException(nameof(writeError));
            _timer = new Timer(_ => StartWrite(false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal int PendingCount
        {
            get { lock (_syncLock) return _pending.Count; }
        }

        internal int OutstandingCount
        {
            get { lock (_syncLock) return GetOutstandingCount(); }
        }

        public bool Enqueue(TKey key, TValue value)
        {
            lock (_syncLock)
            {
                if (_isStopping) return false;

                if (_pending.TryGetValue(key, out var current))
                {
                    value = _merge(current, value);
                }

                _pending[key] = value;
                var now = DateTime.UtcNow;
                _batchStartedAtUtc = _batchStartedAtUtc ?? now;
                if (_activeWrite == null)
                {
                    ScheduleNextFlush(now);
                }

                return true;
            }
        }

        internal bool FlushNow()
        {
            var write = StartWrite(true);
            return write == null ? OutstandingCount == 0 : write.GetAwaiter().GetResult() == null;
        }

        public BatchStopResult StopAndFlush(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            lock (_syncLock)
            {
                if (_stopResult != null)
                {
                    return _stopResult;
                }

                if (_isStopping)
                {
                    return new BatchStopResult(
                        new InvalidOperationException("Batch shutdown is already in progress."),
                        GetOutstandingCount(),
                        0);
                }

                _isStopping = true;
                CancelTimer();
            }

            var elapsed = Stopwatch.StartNew();
            Exception lastError = null;
            var attempts = 0;

            while (true)
            {
                Task<Exception> activeWrite;
                bool hasPending;
                lock (_syncLock)
                {
                    activeWrite = _activeWrite;
                    hasPending = _pending.Count > 0;
                }

                if (activeWrite != null)
                {
                    var waitError = WaitFor(activeWrite, elapsed, timeout, cancellationToken);
                    if (waitError != null)
                    {
                        return CompleteStop(waitError, attempts);
                    }

                    lastError = activeWrite.GetAwaiter().GetResult();
                    continue;
                }

                if (!hasPending)
                {
                    return CompleteStop(null, attempts);
                }

                if (attempts == ShutdownWriteAttempts)
                {
                    return CompleteStop(lastError, attempts);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return CompleteStop(new OperationCanceledException(cancellationToken), attempts);
                }

                if (elapsed.Elapsed >= timeout)
                {
                    return CompleteStop(new TimeoutException("Timed out while saving the pending batch."), attempts);
                }

                if (StartWrite(true) != null)
                {
                    attempts++;
                }
            }
        }

        private static Exception WaitFor(
            Task task,
            Stopwatch elapsed,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new TimeoutException("Timed out while waiting for the active batch write.");
            }

            try
            {
                var waitMilliseconds = (int)Math.Min(
                    Math.Ceiling(remaining.TotalMilliseconds),
                    int.MaxValue);
                return task.Wait(waitMilliseconds, cancellationToken)
                    ? null
                    : new TimeoutException("Timed out while waiting for the active batch write.");
            }
            catch (OperationCanceledException exception)
            {
                return exception;
            }
        }

        private BatchStopResult CompleteStop(Exception error, int attempts)
        {
            bool disposeTimer;
            lock (_syncLock)
            {
                var unsavedCount = GetOutstandingCount();
                if (unsavedCount == 0)
                {
                    error = null;
                }
                else if (error == null)
                {
                    error = new InvalidOperationException("The pending batch could not be saved.");
                }

                _stopResult = new BatchStopResult(error, unsavedCount, attempts);
                disposeTimer = _activeWrite == null;
            }

            if (disposeTimer)
            {
                _timer.Dispose();
            }

            return _stopResult;
        }

        private Task<Exception> StartWrite(bool allowDuringStop)
        {
            Batch snapshot;
            TaskCompletionSource<Exception> completion;
            lock (_syncLock)
            {
                if (_stopResult != null || (!allowDuringStop && _isStopping) || _pending.Count == 0 || _activeWrite != null)
                {
                    return null;
                }

                CancelTimer();
                snapshot = new Batch(_pending, _batchStartedAtUtc ?? DateTime.UtcNow);
                _inFlight = snapshot;
                _pending = new Dictionary<TKey, TValue>();
                _batchStartedAtUtc = null;
                completion = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeWrite = completion.Task;
            }

            Task.Run(() => ExecuteWrite(snapshot, completion, !allowDuringStop));
            return completion.Task;
        }

        private void ExecuteWrite(
            Batch snapshot,
            TaskCompletionSource<Exception> completion,
            bool logError)
        {
            Exception error = null;
            try
            {
                _writeBatch(new List<TValue>(snapshot.Items.Values));
            }
            catch (Exception exception)
            {
                error = exception;
            }

            bool disposeTimer;
            int outstandingCount;
            lock (_syncLock)
            {
                if (error == null)
                {
                    _retryNotBeforeUtc = null;
                }
                else
                {
                    Requeue(snapshot);
                    _retryNotBeforeUtc = DateTime.UtcNow.Add(_retryDelay);
                }

                _inFlight = null;
                _activeWrite = null;
                disposeTimer = _stopResult != null;
                if (!disposeTimer && !_isStopping && _pending.Count > 0)
                {
                    ScheduleNextFlush(DateTime.UtcNow);
                }

                outstandingCount = GetOutstandingCount();
            }

            if (disposeTimer)
            {
                _timer.Dispose();
            }

            completion.SetResult(error);
            if (logError && error != null)
            {
                try
                {
                    _writeError(error, outstandingCount);
                }
                catch
                {
                    // Logging must not fault the writer.
                }
            }
        }

        private void Requeue(Batch snapshot)
        {
            foreach (var item in snapshot.Items)
            {
                _pending[item.Key] = _pending.TryGetValue(item.Key, out var current)
                    ? _merge(item.Value, current)
                    : item.Value;
            }

            if (!_batchStartedAtUtc.HasValue || snapshot.StartedAtUtc < _batchStartedAtUtc.Value)
            {
                _batchStartedAtUtc = snapshot.StartedAtUtc;
            }
        }

        private int GetOutstandingCount()
        {
            if (_inFlight == null)
            {
                return _pending.Count;
            }

            var count = _inFlight.Items.Count;
            foreach (var key in _pending.Keys)
            {
                if (!_inFlight.Items.ContainsKey(key))
                {
                    count++;
                }
            }

            return count;
        }

        private void ScheduleNextFlush(DateTime now)
        {
            var maximumRemaining = _maximumDelay - (now - (_batchStartedAtUtc ?? now));
            var delay = maximumRemaining < _debounceDelay ? maximumRemaining : _debounceDelay;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            if (_retryNotBeforeUtc.HasValue && _retryNotBeforeUtc.Value - now > delay)
            {
                delay = _retryNotBeforeUtc.Value - now;
            }

            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private void CancelTimer()
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
