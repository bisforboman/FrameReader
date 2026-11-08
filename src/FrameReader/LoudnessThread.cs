using System.Runtime.ExceptionServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// <see cref="AudioAnalysis"/>'s loudness graph on a thread of its own, while the caller's thread converts and runs the
/// other analyses: each decoded frame comes as a reference (<c>av_frame_ref</c>, no copy) with its skip and position,
/// through a queue of at most <see cref="Depth"/>, and the thread gives them to the graph in order, as the one-thread
/// pass does, so its figures are the same. What the graph throws is thrown on the caller's thread, at its next
/// <see cref="Add"/> or at <see cref="Finish"/>. The graph belongs to the thread until <see cref="Finish"/> returns.
/// </summary>
internal sealed unsafe class LoudnessThread : IDisposable
{
    /// <summary>The most frames waiting: a few frames of audio, enough to even out the two threads.</summary>
    internal const int Depth = 16;

    readonly record struct Item(nint Frame, int Skip, long Position);

    readonly AudioFilter _graph;
    readonly Action? _afterFrame;
    readonly CancellationToken _cancellationToken;
    readonly CancellationTokenRegistration _registration;
    readonly object _lock = new();
    readonly Queue<Item> _queue = new();
    readonly Thread _thread;
    bool _end, _stop, _done;
    ExceptionDispatchInfo? _error;

    static int _held, _running;

    /// <summary>Frames referenced and not let go yet, in all analyses (tests: none after a run).</summary>
    internal static int Held => Volatile.Read(ref _held);

    /// <summary>Threads started and not ended, in all analyses (tests: none after a run).</summary>
    internal static int Running => Volatile.Read(ref _running);

    /// <summary>Starts the thread over <paramref name="graph"/>.</summary>
    /// <param name="graph">The loudness graph; the thread's until <see cref="Finish"/>.</param>
    /// <param name="afterFrame">Called on the thread after each frame (tests: a failure or a cancellation there).</param>
    /// <param name="cancellationToken">Stops the thread between frames, and wakes a caller waiting for room.</param>
    internal LoudnessThread(AudioFilter graph, Action? afterFrame, CancellationToken cancellationToken)
    {
        (_graph, _afterFrame, _cancellationToken) = (graph, afterFrame, cancellationToken);
        _registration = cancellationToken.Register(() =>
        {
            lock (_lock)
                Monitor.PulseAll(_lock);
        });
        _thread = new Thread(Run) { IsBackground = true, Name = "FrameReader loudness" };
        Interlocked.Increment(ref _running);
        _thread.Start();
    }

    /// <summary>
    /// The frame <see cref="AudioReader.TryDecode"/> gave, for the graph (as <see cref="AudioFilter.Add"/> takes it);
    /// waits while the queue is full.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="FrameReaderException">The graph failed on an earlier frame.</exception>
    internal void Add(AVFrame* frame, int skip, long position)
    {
        lock (_lock)
        {
            while (_queue.Count >= Depth && _error is null && !_done && !_cancellationToken.IsCancellationRequested)
                Monitor.Wait(_lock);
            _error?.Throw();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        if (skip >= frame->nb_samples)
            return; // wholly before the seek point: AudioFilter.Add drops it too

        var copy = ffmpeg.av_frame_alloc();
        int ret = copy == null ? ffmpeg.AVERROR(ffmpeg.ENOMEM) : ffmpeg.av_frame_ref(copy, frame);
        if (ret < 0)
        {
            ffmpeg.av_frame_free(&copy);
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg (filter): {FFmpegLibraries.ErrorMessage(ret)}");
        }

        Interlocked.Increment(ref _held);
        lock (_lock)
        {
            _queue.Enqueue(new Item((nint)copy, skip, position));
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>The end of the input: waits until the thread has given the graph every frame and its end.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="FrameReaderException">The graph failed.</exception>
    internal void Finish()
    {
        lock (_lock)
        {
            _end = true;
            Monitor.PulseAll(_lock);
            while (!_done && !_cancellationToken.IsCancellationRequested)
                Monitor.Wait(_lock);
            _error?.Throw();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        _thread.Join();
    }

    void Run()
    {
        try
        {
            while (true)
            {
                Item item;
                lock (_lock)
                {
                    while (_queue.Count == 0 && !_end && !_stop && !_cancellationToken.IsCancellationRequested)
                        Monitor.Wait(_lock);
                    if (_stop || _cancellationToken.IsCancellationRequested)
                        return;
                    if (!_queue.TryDequeue(out item))
                        break; // the end, every frame given
                    Monitor.PulseAll(_lock); // room for another
                }

                try
                {
                    _graph.Add((AVFrame*)item.Frame, item.Skip, item.Position);
                    _graph.Drain();
                }
                finally
                {
                    Release(item);
                }

                _afterFrame?.Invoke();
            }

            _graph.End();
            _graph.Drain();
        }
        catch (Exception e)
        {
            lock (_lock)
                _error = ExceptionDispatchInfo.Capture(e);
        }
        finally
        {
            lock (_lock)
            {
                _done = true;
                Monitor.PulseAll(_lock);
            }

            Interlocked.Decrement(ref _running);
        }
    }

    static void Release(Item item)
    {
        var frame = (AVFrame*)item.Frame;
        ffmpeg.av_frame_free(&frame);
        Interlocked.Decrement(ref _held);
    }

    /// <summary>Stops the thread (after the frame it is giving the graph) and lets go of the frames left.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _stop = true;
            Monitor.PulseAll(_lock);
        }

        _thread.Join();
        _registration.Dispose();
        while (_queue.TryDequeue(out var item))
            Release(item);
    }
}
