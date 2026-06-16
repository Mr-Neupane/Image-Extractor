using System.Diagnostics;

namespace ImageExtractor.Services;

public class Progress : IDisposable
{
    private long _written;
    private long _skipped;
    private long _failed;
    private readonly long _total;
    private readonly Stopwatch _stopwatch = new();
    private Timer? _timer;
    private bool _disposed;

    public Progress(long total) => _total = total;

    public long WrittenCount => Interlocked.Read(ref _written);
    public long SkippedCount => Interlocked.Read(ref _skipped);
    public long FailedCount => Interlocked.Read(ref _failed);
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;

    public void Start()
    {
        _stopwatch.Start();
        _timer = new Timer(_ => Display(), null, 0, 1000);
    }

    public void Written() => Interlocked.Increment(ref _written);
    public void Skipped() => Interlocked.Increment(ref _skipped);
    public void Failed() => Interlocked.Increment(ref _failed);

    private void Display()
    {
        if (_disposed) return;
        long w = Interlocked.Read(ref _written);
        long s = Interlocked.Read(ref _skipped);
        long f = Interlocked.Read(ref _failed);
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        double rate = elapsed > 0 ? w / elapsed : 0;
        string eta = rate > 0 && _total > 0
            ? TimeSpan.FromSeconds((_total - w) / rate).ToString(@"mm\:ss")
            : "--:--";

        Console.Write(
            "\r[{0:HH:mm:ss}] Written: {1:N0}  |  Skipped: {2:N0}  |  Failed: {3:N0}  |  {4:F1} files/s  |  Elapsed: {5:mm\\:ss}  |  ETA: {6}  |  Total: {7:N0}   ",
            DateTime.Now, w, s, f, rate, _stopwatch.Elapsed, eta, _total);
    }

    public void Stop()
    {
        Display();
        Console.WriteLine();
        _stopwatch.Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
