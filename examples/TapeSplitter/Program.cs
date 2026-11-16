using System.Globalization;
using System.Text.Json;
using FrameReader;
using TapeSplitter;

// One long capture of a tape in, its blank stretches and the proposed cut points out, as JSON. With --write, each
// recording also as a file of its own in that folder, by stream copy.
//
//   dotnet run --project examples/TapeSplitter -- capture.mkv [--min-gap 2] [--sound agree] [--write recordings [--as avi]]
//
// The rules from blank spans to cut points and the detectors' thresholds are options (README.md).
//
// FFmpeg's libraries: FrameReader.Native's next to the app, or the folder in FrameReader__Libraries.
string? path = null, write = null, extension = null;
var options = new TapeScanOptions();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--min-gap" when i + 1 < args.Length:
            options = options with { MinGap = TimeSpan.FromSeconds(Number(args[++i])) };
            break;
        case "--min-span" when i + 1 < args.Length:
            options = options with { MinSpan = TimeSpan.FromSeconds(Number(args[++i])) };
            break;
        case "--min-recording" when i + 1 < args.Length:
            options = options with { MinRecording = TimeSpan.FromSeconds(Number(args[++i])) };
            break;
        case "--sound" when i + 1 < args.Length && args[i + 1] is "agree" or "ignore":
            options = options with { Sound = args[++i] == "agree" ? SoundRule.Agree : SoundRule.Ignore };
            break;
        case "--kinds" when i + 1 < args.Length:
            options = options with { Kinds = args[++i].Split(',') };
            break;
        case "--silence" when i + 1 < args.Length:
            options = options with { SilenceLevel = Number(args[++i]) };
            break;
        case "--black" when i + 1 < args.Length:
            options = options with { BlackPixel = Number(args[++i]) };
            break;
        case "--black-share" when i + 1 < args.Length:
            options = options with { BlackPicture = Number(args[++i]) };
            break;
        case "--freeze-noise" when i + 1 < args.Length:
            options = options with { FreezeNoise = Number(args[++i]) };
            break;
        case "--flat-spread" when i + 1 < args.Length:
            options = options with { FlatSpread = Number(args[++i]) };
            break;
        case "--write" when i + 1 < args.Length:
            write = args[++i];
            break;
        case "--as" when i + 1 < args.Length:
            extension = "." + args[++i].TrimStart('.');
            break;
        default:
            path = args[i];
            break;
    }
}

if (path is null)
{
    Console.Error.WriteLine("Usage: TapeSplitter <capture> [--min-gap seconds] [--min-span seconds] [--min-recording seconds] [--sound ignore|agree] "
        + "[--kinds black,flat,frozen] [--silence dB] [--black share] [--black-share share] [--freeze-noise share] [--flat-spread codes] [--write folder [--as avi]]");
    return 2;
}

FFmpegLibraries.Load(Environment.GetEnvironmentVariable("FrameReader__Libraries"));
try
{
    var report = TapeScan.Run(path, options);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    if (write is not null)
    {
        foreach (string file in TapeScan.Write(path, report, write, extension))
            Console.Error.WriteLine($"Wrote {file}");
    }

    return 0;
}
catch (Exception e) when (e is FrameReaderException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
