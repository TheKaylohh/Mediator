using System.Collections.Concurrent;

namespace Mediator.SwxWorker;

public class StaQueueException : Exception { public StaQueueException(string message) : base(message) { } }
/// <summary>The work never started because the queue stayed busy past the caller's deadline. Safe to retry.</summary>
public sealed class StaQueueBusyException : StaQueueException { public StaQueueBusyException(string m) : base(m) { } }
/// <summary>The work started and did not finish in time. Its effects are unknown.</summary>
public sealed class StaCallTimeoutException : StaQueueException { public StaCallTimeoutException(string m) : base(m) { } }
/// <summary>A COM call is stuck on the STA thread; the queue accepts no more work and the process should be restarted.</summary>
public sealed class StaQueueWedgedException : StaQueueException { public StaQueueWedgedException(string m) : base(m) { } }

/// <summary>
/// Checked periodically while a work item runs. If <see cref="Check"/> returns an exception (for example
/// "SolidWorks is showing a modal dialog"), the caller is released with it at once instead of waiting for
/// the run timeout. The stuck call itself keeps the thread until it returns or the watchdog wedges the queue.
/// </summary>
public sealed record StallProbe(TimeSpan FirstCheckAfter, TimeSpan Interval, Func<Exception?> Check);

/// <summary>
/// A single thread (STA in production) fed by a queue. COM calls cannot be aborted, so a run that exceeds its
/// timeout marks the queue wedged: pending work fails fast, <see cref="Wedged"/> fires, and the owner restarts
/// the process. Callers that are only waiting in line can be cancelled or time out without side effects.
/// </summary>
public sealed class DedicatedStaQueue : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly Thread _thread;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state; // 0 running, 1 wedged, 2 disposed
    private string? _wedgedReason;

    public event Action<string>? Wedged;

    /// <summary>Force a GC after each item so released RCWs drop their SolidWorks references promptly.</summary>
    public bool CollectAfterEachItem { get; init; } = true;

    /// <param name="apartment">STA for COM. <see cref="ApartmentState.Unknown"/> leaves the default (offline tests on Linux, where STA does not exist).</param>
    public DedicatedStaQueue(string name, ApartmentState apartment = ApartmentState.STA, Action? threadInit = null, Action? threadCleanup = null)
    {
        _thread = new Thread(() => Run(threadInit, threadCleanup)) { Name = name, IsBackground = true };
        if (apartment != ApartmentState.Unknown) _thread.SetApartmentState(apartment);
        _thread.Start();
    }

    public bool IsOnQueueThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;
    public bool IsWedged => Volatile.Read(ref _state) == 1;

    private void Run(Action? init, Action? cleanup)
    {
        try
        {
            init?.Invoke();
            _initialized.TrySetResult();
        }
        catch (Exception ex)
        {
            _initialized.TrySetException(ex);
            MarkWedged($"STA thread initialization failed: {ex.Message}");
            return;
        }
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute(this);
                if (CollectAfterEachItem)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
        }
        finally
        {
            try { cleanup?.Invoke(); } catch (Exception) { /* thread is ending; nothing useful to do */ }
        }
    }

    /// <param name="runTimeout">Longest the work may run once started before the queue is declared wedged.</param>
    /// <param name="queueTimeout">Longest the caller waits for the work to start.</param>
    /// <param name="stallProbe">Optional early-release check while the work runs (modal dialog detection).</param>
    public async Task<T> InvokeAsync<T>(Func<T> work, TimeSpan runTimeout, TimeSpan queueTimeout, CancellationToken ct, StallProbe? stallProbe = null)
    {
        await _initialized.Task.ConfigureAwait(false);
        ThrowIfUnusable();
        var item = new WorkItem<T>(work, runTimeout, stallProbe);
        try
        {
            _queue.Add(item, ct);
        }
        catch (InvalidOperationException)
        {
            ThrowIfUnusable();
            throw;
        }

        try
        {
            return await item.Task.WaitAsync(queueTimeout + runTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (item.TryCancelBeforeStart())
                throw new StaQueueBusyException($"The SolidWorks worker stayed busy for {queueTimeout + runTimeout:g}; the request never started.");
            throw new StaCallTimeoutException("The request started but did not finish in time; its effect in SolidWorks is unknown.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.TryCancelBeforeStart(); // if already running, it completes on its own and the result is discarded
            throw;
        }
    }

    public Task InvokeAsync(Action work, TimeSpan runTimeout, TimeSpan queueTimeout, CancellationToken ct) =>
        InvokeAsync<bool>(() => { work(); return true; }, runTimeout, queueTimeout, ct);

    internal void MarkWedged(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        _wedgedReason = reason;
        _queue.CompleteAdding();
        while (_queue.TryTake(out var pending)) pending.Fail(new StaQueueWedgedException(reason));
        var handlers = Wedged;
        if (handlers is not null) ThreadPool.QueueUserWorkItem(_ => handlers(reason));
    }

    private void ThrowIfUnusable()
    {
        switch (Volatile.Read(ref _state))
        {
            case 1: throw new StaQueueWedgedException(_wedgedReason ?? "The STA thread is wedged.");
            case 2: throw new ObjectDisposedException(nameof(DedicatedStaQueue));
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) _queue.CompleteAdding();
        if (!IsOnQueueThread) _thread.Join(TimeSpan.FromSeconds(10));
    }

    private abstract class WorkItem
    {
        public abstract void Execute(DedicatedStaQueue owner);
        public abstract void Fail(Exception ex);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<T> _work;
        private readonly TimeSpan _runTimeout;
        private readonly StallProbe? _probe;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state; // 0 queued, 1 running, 2 cancelled or failed before start, 3 finished

        public WorkItem(Func<T> work, TimeSpan runTimeout, StallProbe? probe) { _work = work; _runTimeout = runTimeout; _probe = probe; }

        public Task<T> Task => _tcs.Task;

        public bool TryCancelBeforeStart()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return false;
            _tcs.TrySetCanceled();
            return true;
        }

        public override void Fail(Exception ex)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) _tcs.TrySetException(ex);
        }

        public override void Execute(DedicatedStaQueue owner)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return; // cancelled while queued
            using var watchdog = new Timer(_ =>
            {
                if (Volatile.Read(ref _state) != 1) return; // finished just as the timer fired
                _tcs.TrySetException(new StaCallTimeoutException($"A SolidWorks call ran longer than {_runTimeout:g}."));
                owner.MarkWedged($"A COM call exceeded its {_runTimeout:g} run timeout.");
            }, null, _runTimeout, Timeout.InfiniteTimeSpan);

            Timer? stallTimer = null;
            if (_probe is not null)
            {
                stallTimer = new Timer(_ =>
                {
                    if (Volatile.Read(ref _state) != 1 || _tcs.Task.IsCompleted) return;
                    Exception? reason;
                    try { reason = _probe.Check(); }
                    catch (Exception) { return; } // a failing probe must never break the call it watches
                    if (reason is not null) _tcs.TrySetException(reason);
                }, null, _probe.FirstCheckAfter, _probe.Interval);
            }

            try
            {
                _tcs.TrySetResult(_work());
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(ref _state, 3);
                stallTimer?.Dispose();
            }
        }
    }
}
