namespace NineP.Server.Internal;

/// <summary>
/// Counts the request workers a session has running and lets a drain wait for the last one to
/// unwind. The in-flight budgets are returned when a reply is queued (reference §8 rule 8), which
/// is before the worker's <c>finally</c> runs, so they no longer say whether a handler is still on
/// its way out; this does.
/// </summary>
internal sealed class WorkerCounter
{
    private readonly object _gate = new();
    private int _running;
    private TaskCompletionSource? _idle;

    /// <summary>Records a worker that is about to start; call it before the worker's task exists.</summary>
    public void Enter()
    {
        lock (_gate)
        {
            _running++;
        }
    }

    /// <summary>Records a worker that has unwound, and wakes a drain waiting for the last one.</summary>
    public void Exit()
    {
        TaskCompletionSource? idle = null;

        lock (_gate)
        {
            if (--_running == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }

        idle?.TrySetResult();
    }

    /// <summary>A task that completes once no worker is running; already complete when none is.</summary>
    /// <returns>The task to await.</returns>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            if (_running == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }
}
