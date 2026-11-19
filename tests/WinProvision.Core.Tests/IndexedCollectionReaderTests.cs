using System.Collections;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class IndexedCollectionReaderTests
{
    [Fact]
    public void ReadsVectorWithoutRequestingIterableInterface()
        => Assert.Equal(new[] { 10, 20 }, IndexedCollectionReader.Read(new VectorView()).ToArray());

    [Fact]
    public void CancellationStopsBeforeReadingNextNativeItem()
    {
        using var cancellation = new CancellationTokenSource();
        using var reader = IndexedCollectionReader.Read(new VectorView(), cancellation.Token).GetEnumerator();
        Assert.True(reader.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.MoveNext());
    }

    private sealed class VectorView : IReadOnlyList<int>
    {
        public int Count => 2;
        public int this[int index] => (index + 1) * 10;
        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("Native iterable unavailable");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
