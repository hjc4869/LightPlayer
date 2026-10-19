using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

const string Usage = """
    lrctest - exercise a Light Player lyrics source script

    usage:
      lrctest <script.js> <title> [artist] [options]
            lrctest <script.js> --info

    options:
      -d, --download <index>  Download the candidate at the given zero-based index.
            -i, --info              Display source information and exit.
      -o, --out <file>        Write the downloaded lyrics to a file instead of stdout.
      -h, --help              Show this help.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

string? scriptPath = null;
string? title = null;
string? artist = null;
int? downloadIndex = null;
string? outputPath = null;
bool showInfo = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    switch (arg)
    {
        case "-d" or "--download":
            if (++i == args.Length || !int.TryParse(args[i], out var index) || index < 0)
            {
                return Fail($"'{arg}' requires a non-negative index.");
            }

            downloadIndex = index;
            break;

        case "-i" or "--info":
            showInfo = true;
            break;

        case "-o" or "--out":
            if (++i == args.Length)
            {
                return Fail($"'{arg}' requires a file path.");
            }

            outputPath = args[i];
            break;

        default:
            if (arg.StartsWith('-'))
            {
                return Fail($"Unknown option '{arg}'.");
            }

            if (scriptPath is null)
            {
                scriptPath = arg;
            }
            else if (title is null)
            {
                title = arg;
            }
            else if (artist is null)
            {
                artist = arg;
            }
            else
            {
                return Fail($"Unexpected argument '{arg}'.");
            }

            break;
    }
}

if (scriptPath is null || (!showInfo && title is null))
{
    return Fail(showInfo
        ? "A script path is required."
        : "A script path and a track title are required.");
}

if (!File.Exists(scriptPath))
{
    return Fail($"Script '{scriptPath}' does not exist.");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var name = Path.GetFileNameWithoutExtension(scriptPath);
using var source = new JsDownloadSource(File.ReadAllText(scriptPath), name);
if (showInfo)
{
    Console.WriteLine($"Source information from '{name}':");
    foreach (var item in source.GetSourceInfo())
    {
        Console.WriteLine(string.IsNullOrWhiteSpace(item.Url)
            ? $"  {item.Text}"
            : $"  {item.Text} -> {item.Url}");
    }

    return 0;
}

ExternalLrcInfo[] candidates;
try
{
    candidates = await source.LookupLrcAsync(title, artist ?? string.Empty, cts.Token);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    return Fail($"lookupLrcAsync failed: {ex}");
}

Console.WriteLine($"{candidates.Length} candidate(s) from '{name}':");
for (var i = 0; i < candidates.Length; i++)
{
    var candidate = candidates[i];
    Console.WriteLine(
        $"  [{i}] {candidate.Title} - {candidate.Artist} | album: {candidate.Album} " +
        $"| {candidate.Duration:0.##}s{(candidate.Instrumental ? " | instrumental" : string.Empty)}");
}

if (downloadIndex is not int selected)
{
    return 0;
}

if (selected >= candidates.Length)
{
    return Fail($"Index {selected} is out of range.");
}

LyricDownload? download;
try
{
    download = await source.DownloadLrcAsync(candidates[selected], cts.Token);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    return Fail($"downloadLrcAsync failed: {ex}");
}

if (download is null)
{
    return Fail("No lyrics were returned.");
}

Console.WriteLine();
Console.WriteLine($"type: {download.Type}");
if (outputPath is null)
{
    Console.WriteLine(download.Content);
}
else
{
    File.WriteAllText(outputPath, download.Content);
    Console.WriteLine($"written to {outputPath}");
}

return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
