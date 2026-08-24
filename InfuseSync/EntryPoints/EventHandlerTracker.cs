using System;
using System.Threading;

namespace InfuseSync.EntryPoints
{
    internal sealed class EventHandlerTracker
    {
        private readonly object _syncLock = new object();
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private int _activeHandlers;
        private bool _isStopping;

        public int ActiveCount
        {
            get { lock (_syncLock) return _activeHandlers; }
        }

        public bool TryEnter()
        {
            lock (_syncLock)
            {
                if (_isStopping) return false;

                if (_activeHandlers++ == 0)
                {
                    _idle.Reset();
                }

                return true;
            }
        }

        public void Exit()
        {
            lock (_syncLock)
            {
                _activeHandlers--;
                if (_activeHandlers == 0) _idle.Set();
            }
        }

        public Exception StopAndWait(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_syncLock)
            {
                _isStopping = true;
                if (_activeHandlers == 0) return null;
            }

            try
            {
                return _idle.Wait(timeout, cancellationToken)
                    ? null
                    : new TimeoutException("Timed out while waiting for event handlers.");
            }
            catch (OperationCanceledException exception)
            {
                return exception;
            }
        }
    }
}
