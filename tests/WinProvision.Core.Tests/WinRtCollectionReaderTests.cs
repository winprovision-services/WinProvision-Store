using Microsoft.Extensions.Logging;
using WinProvision.Providers.WinGet.Com;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinRtCollectionReaderTests
{
    [Fact]
    public void FirstOrDefault_UsesCountAndIndexWithoutEnumerator()
    {
        var collection = new IndexOnlyList<int>(new[] { 2, 4, 6 });

        int? found = WinRtCollectionReader.FirstOrDefault(collection, value => value == 4, "test-list");

        Assert.Equal(4, found);
        Assert.Equal(2, collection.IndexReads);
    }

    [Fact]
    public void IndexInvalidCast_LogsCollectionAndElementTypeThenRethrows()
    {
        var collection = new IndexOnlyList<int>(new[] { 1 }, failAtIndex: 0);
        var logger = new CapturingLogger();

        Assert.Throws<InvalidCastException>(() => WinRtCollectionReader.FirstOrDefault(
            collection, _ => true, "Package.AvailableVersions", logger));

        Assert.Contains("Package.AvailableVersions", logger.Message);
        Assert.Contains(typeof(IndexOnlyList<int>).FullName!, logger.Message);
        Assert.Contains(typeof(int).FullName!, logger.Message);
        Assert.IsType<InvalidCastException>(logger.Exception);
    }

    private sealed class IndexOnlyList<T>(IReadOnlyList<T> values, int? failAtIndex = null) : IReadOnlyList<T>
    {
        public int IndexReads { get; private set; }
        public int Count => values.Count;
        public T this[int index]
        {
            get
            {
                IndexReads++;
                if (index == failAtIndex) throw new InvalidCastException("fake WinRT projection failure");
                return values[index];
            }
        }
        public IEnumerator<T> GetEnumerator() => throw new InvalidCastException("LINQ enumeration was used");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CapturingLogger : ILogger
    {
        public string Message { get; private set; } = string.Empty;
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            Exception = exception;
        }
    }
}
