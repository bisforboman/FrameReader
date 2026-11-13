using System.Globalization;
using System.Text.Json;
using FrameReader;
using TapeSplitter;

// One long capture of a tape in, its blank stretches and the proposed cut points out, as JSON. With --write, each
// recording also as a file of its own in that folder, by stream copy.
//
//   dotnet run --project examples/TapeSplitter -- capture.mkv [--min-gap 2] [--silence -60] [--write recordings]
//
// FFmpeg's libraries: FrameReader.Native's next to the app, or the folder in FrameReader__Libraries.
string? path = null, write = null;
var options = new TapeScanOptions();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--min-gap" when i + 1 < args.Length:
            options = options with { MinGap = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)) };
            break;
        case "--silence" when i + 1 < args.Length:
            options = options with { SilenceLevel = double.Parse(args[++i], CultureInfo.InvariantCulture) };
            break;
        case "--write" when i + 1 < args.Length:
            write = args[++i];
            break;
        default:
            path = args[i];
            break;
    }
}

if (path is null)
{
    Console.Error.WriteLine("Usage: TapeSplitter <capture> [--min-gap seconds] [--silence dB] [--write folder]");
    return 2;
}

FFmpegLibraries.Load(Environment.GetEnvironmentVariable("FrameReader__Libraries"));
try
{
    var report = TapeScan.Run(path, options);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    if (write is not null)
    {
        foreach (string file in TapeScan.Write(path, report, write))
            Console.Error.WriteLine($"Wrote {file}");
    }

    return 0;
}
catch (FrameReaderException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
