namespace DotNodeViewer.Core.Tests.Harness;

/// <summary>
/// Measures managed heap bytes allocated on the current thread during a synchronous or single-threaded async operation.
/// </summary>
public static class AllocationProbe
{
    /// <summary>
    /// Executes <paramref name="action"/> and returns the number of managed bytes allocated on the current thread.
    /// </summary>
    /// <param name="action">The operation to measure.</param>
    /// <returns>The total bytes allocated on the current thread during <paramref name="action"/>.</returns>
    public static long MeasureBytes(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        long after = GC.GetAllocatedBytesForCurrentThread();
        return Math.Max(0, after - before);
    }

    /// <summary>
    /// Executes <paramref name="func"/> and returns both its result and the number of managed bytes allocated on the current thread.
    /// </summary>
    /// <typeparam name="T">The return type of the measured operation.</typeparam>
    /// <param name="func">The operation to measure.</param>
    /// <returns>A tuple containing the operation result and the allocated byte count on the current thread.</returns>
    public static async ValueTask<(T Result, long AllocatedBytes)> MeasureAsync<T>(Func<ValueTask<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        long before = GC.GetAllocatedBytesForCurrentThread();
        T result = await func().ConfigureAwait(false);
        long after = GC.GetAllocatedBytesForCurrentThread();
        return (result, Math.Max(0, after - before));
    }
}

