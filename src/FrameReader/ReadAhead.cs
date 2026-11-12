using System.Runtime.ExceptionServices;

namespace FrameReader;

/// <summary>
/// <see cref="VideoFrameReaderOptions.ReadAhead"/>: a thread that decodes ahead, taking each frame from the decoder
/// (<see cref="FrameDecoder.TakeFrame"/>: a reference, no copy) into a queue of at most N, while the reader's caller
/// works. The decoder belongs to that thread while it runs: the reader pauses it (<see cref="Pause"/>, <see cref="Clear"/>)
/// before using the decoder itself (a seek, the index), and the next <see cref="Next"/> lets it go on. Started by the
/// first <see cref="Next"/>, so a seek before the first read is the decoder's own (a deinterlacing decoder can seek
/// only then). While the reader reads frame after frame (<see cref="ConvertAhead"/>), the thread also converts each frame
/// with a <see cref="FrameConverter"/> of its own into an output of a pool (one per queued frame, the current one's and
/// the one being converted): the same bytes as the reader's conversion, off the caller's thread.
/// </summary>
internal sealed class ReadAhead : IDisposable
{
    /// <summary>
    /// A frame read ahead, with what the reader asks of the decoder's current frame, and its conversion when the thread
    /// made one (null: the reader converts it).
    /// </summary>
    internal sealed record Item(DecodedFrame Frame, long Pts, bool IsKeyframe, long Duration, FrameOutput? Converted);

    readonly FrameDecoder _decoder;
    readonly int _depth;
    readonly Func<FrameConverter> _newConverter;
    readonly object _lock = new();
    readonly Queue<Item> _queue = new();
    readonly Stack<FrameOutput> _outputs = new(); // converted frames' outputs let go, for the next ones
    FrameConverter? _converter; // the thread's
    Thread? _thread;
    Item? _current;
    bool _pause, _paused, _stop, _ended, _convert;
    ExceptionDispatchInfo? _error;

    internal ReadAhead(FrameDecoder decoder, int depth, Func<FrameConverter> converter) =>
        (_decoder, _depth, _newConverter) = (decoder, depth, converter);

    /// <summary>Frames the thread converted, given converted by <see cref="Next"/> (tests).</summary>
    internal int Converted { get; private set; }

    /// <summary>
    /// Whether the thread converts the frames it decodes from now on (the frames already queued stay as they are): the
    /// reader turns it on while every frame is read and converted, and off when frames may be passed over or not converted.
    /// </summary>
    internal void ConvertAhead(bool on)
    {
        lock (_lock)
            _convert = on;
    }

    /// <summary>The frame <see cref="Next"/> gave last.</summary>
    internal Item Current => _current ?? throw new InvalidOperationException("No frame read.");

    /// <summary>How many times the reader paused the thread to look at the index (tests: a read every second doesn't).</summary>
    internal int Pauses { get; private set; }

    static int _held;

    /// <summary>Frames taken from decoders and not let go yet, in all readers (tests: none after Dispose).</summary>
    internal static int Held => Volatile.Read(ref _held);

    /// <summary>The next frame, as <see cref="FrameDecoder.Next"/>; what decoding it threw is thrown here, in its turn.</summary>
    internal bool Next()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop, this);
            if (_thread is null)
            {
                _thread = new Thread(Decode) { IsBackground = true, Name = "FrameReader read-ahead" };
                _thread.Start();
            }

            (_pause, _paused) = (false, false);
            Monitor.PulseAll(_lock);
            while (_queue.Count == 0 && !_ended)
                Monitor.Wait(_lock);
            if (_queue.TryDequeue(out var item))
            {
                Monitor.PulseAll(_lock); // room for another
                Release(_current);
                _current = item;
                if (item.Converted is not null)
                    Converted++;
                return true;
            }

            if (_error is { } error)
            {
                // As the decoder: a read after a failed one decodes on.
                (_error, _ended) = (null, false);
                error.Throw();
            }

            Release(_current);
            _current = null;
            return false;
        }
    }

    /// <summary>
    /// Waits until the thread has filled the queue, or reached the end or a pause (tests: what decoding ahead leaves out
    /// before the next read, without depending on how fast the machine is).
    /// </summary>
    internal void WaitUntilFull()
    {
        lock (_lock)
        {
            while (_thread is not null && !_stop && !_pause && !_ended && _queue.Count < _depth)
                Monitor.Wait(_lock);
        }
    }

    /// <summary>Waits for the thread to stop between frames, to look at the index; it goes on at the next <see cref="Next"/>.</summary>
    internal void Pause()
    {
        Pauses++;
        Stop();
    }

    void Stop()
    {
        lock (_lock)
        {
            _pause = true;
            Monitor.PulseAll(_lock);
            while (_thread is not null && !_paused)
                Monitor.Wait(_lock);
        }
    }

    /// <summary>Pauses the thread and lets go of the frames read ahead (and the current one): before a seek.</summary>
    internal void Clear()
    {
        Stop();
        lock (_lock)
        {
            DropFrames(); // those from before the seek
            (_ended, _error) = (false, null);
        }
    }

    void DropFrames()
    {
        Release(_current);
        _current = null;
        while (_queue.TryDequeue(out var item))
            Release(item);
    }

    void Decode()
    {
        bool convert;
        while (true)
        {
            lock (_lock)
            {
                while (!_stop && (_pause || _ended || _queue.Count >= _depth))
                {
                    if (_pause && !_paused)
                    {
                        _paused = true;
                        Monitor.PulseAll(_lock);
                    }

                    Monitor.Wait(_lock);
                }

                if (_stop)
                    return;
                convert = _convert;
            }

            Item? item = null;
            ExceptionDispatchInfo? error = null;
            try
            {
                if (_decoder.Next())
                {
                    item = new Item(_decoder.TakeFrame(), _decoder.Pts, _decoder.IsKeyframe, _decoder.Duration, null);
                    Interlocked.Increment(ref _held);
                }
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }

            if (item is not null && convert)
                item = item with { Converted = ConvertFrame(item.Frame) };

            lock (_lock)
            {
                if (item is not null)
                    _queue.Enqueue(item);
                else
                    (_ended, _error) = (true, error);
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// The frame converted into an output of the pool, as the reader would convert it; null when converting fails, so the
    /// reader converts it in its turn and throws there, in the frame's order.
    /// </summary>
    unsafe FrameOutput? ConvertFrame(DecodedFrame frame)
    {
        FrameOutput? output;
        lock (_lock)
            _outputs.TryPop(out output);
        output ??= new FrameOutput();
        try
        {
            (_converter ??= _newConverter()).Convert(frame.Frame, output);
            return output;
        }
        catch (FrameReaderException)
        {
            lock (_lock)
                _outputs.Push(output);
            return null;
        }
    }

    void Release(Item? item)
    {
        if (item is null)
            return;
        item.Frame.Dispose();
        Interlocked.Decrement(ref _held);
        if (item.Converted is { } output)
            _outputs.Push(output); // under the lock, or with the thread gone
    }

    /// <summary>Stops the thread (after the frame it is decoding) and lets go of the frames.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_lock)
        {
            _stop = true;
            Monitor.PulseAll(_lock);
            thread = _thread;
        }

        thread?.Join();
        DropFrames(); // all, the thread gone
        while (_outputs.TryPop(out var output))
            output.Dispose();
        _converter?.Dispose();
        _converter = null;
    }
}
