using System.Buffers;

namespace Aml.Core.Retrieval;

/// <summary>
/// An array rented from the shared pool for one search and returned on dispose, so list-sized buffers leave no garbage.
/// A rented array can be longer than asked for and holds stale values: only the first <see cref="Length"/> slots are
/// meaningful, and only once written (or cleared with <c>clear: true</c>).
/// </summary>
internal sealed class PooledArray<T> : IDisposable
{
    public T[] Array { get; }
    public int Length { get; }

    public PooledArray(int length, bool clear = false)
    {
        Array = ArrayPool<T>.Shared.Rent(length);
        Length = length;
        if (clear) System.Array.Clear(Array, 0, length);
    }

    public Span<T> Span => Array.AsSpan(0, Length);

    public void Dispose() => ArrayPool<T>.Shared.Return(Array);
}
