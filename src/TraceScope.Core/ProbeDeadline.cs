namespace TraceScope.Core;

internal static class ProbeDeadline
{
    // Timer callbacks can be delayed by thread-pool load. Monotonic elapsed time
    // is authoritative even when cancellation has not yet been dispatched.
    public static void Verify(TimeSpan elapsed, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (elapsed >= timeout) throw new OperationCanceledException("Probe response arrived after its deadline.", token);
    }
}
