using System.Buffers;

namespace Aml.Core.Retrieval;

/// <summary>
/// An array of <see cref="Length"/> slots rented from the shared pool for one search and returned on dispose, so
/// list-sized buffers leave no garbage. Only the slots asked for are reachable: a rented array is often longer, and its
/// tail holds another search's values. Slots start stale unless rented with <c>clear: true</c>. Disposing twice returns
/// the array once; any access after dispose throws.
/// </summary>
internal sealed class PooledArray<T> : IDisposable
{
    private T[]? _rented;

    public int Length { get; }

    public PooledArray(int length, bool clear = false)
    {
        _rented = ArrayPool<T>.Shared.Rent(length);
        Length = length;
        if (clear) Array.Clear(_rented, 0, length);
    }

    public Span<T> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_rented is null, this);
            return _rented.AsSpan(0, Length);
        }
    }

    public ref T this[int index] => ref Span[index];

    public void Dispose()
    {
        if (_rented is null) return;
        ArrayPool<T>.Shared.Return(_rented);
        _rented = null;
    }
}
