using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>Indexes the configured folders in place and reports what the scan found.</summary>
internal sealed class LibraryScanTab : View
{
    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Rows kept in either log before the oldest are dropped.</summary>
    private const int MaxLines = 500;

    private readonly IApplication app;
    private readonly ServerConfiguration config;
    private readonly Button scan;
    private readonly ProgressBar progress;
    private readonly Label status;
    private readonly ObservableCollection<string> lines = [];
    private readonly ListView log;
    private readonly ObservableCollection<string> ffmpegLines = [];
    private readonly ListView ffmpegLog;
    private object? pulse;
    private bool scanning;

    /// <summary>Writes the pending edits of every page into the configuration, or returns why it cannot.</summary>
    public Func<string?>? PrepareConfiguration { get; set; }

    public LibraryScanTab(IApplication app, ServerConfiguration config)
    {
        this.app = app;
        this.config = config;
        Title = "Sca_n";
        Width = Dim.Fill();
        Height = Dim.Fill();

        scan = new Button { X = 0, Y = 0, Text = "Scan no_w" };
        scan.Accepting += (_, e) =>
        {
            e.Handled = true;
            StartScan();
        };

        progress = new ProgressBar
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = 1,
            ProgressBarStyle = ProgressBarStyle.MarqueeBlocks,
            Visible = false,
        };

        status = new Label { X = 0, Y = 3, Width = Dim.Fill(), Text = "Idle." };

        log = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        log.SetSource(lines);
        var progressFrame = new FrameView
        {
            Title = "Progress",
            X = 0,
            Y = 5,
            Width = Dim.Fill(),
            Height = Dim.Fill(Dim.Percent(45)),
        };
        progressFrame.Add(log);

        ffmpegLog = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        ffmpegLog.SetSource(ffmpegLines);
        var ffmpegFrame = new FrameView
        {
            Title = "FFmpeg (warnings and errors)",
            X = 0,
            Y = Pos.AnchorEnd(),
            Width = Dim.Fill(),
            Height = Dim.Percent(45),
        };
        ffmpegFrame.Add(ffmpegLog);

        Add(
            scan,
            new Label { X = Pos.Right(scan) + 2, Y = 0, Text = "Stop the running server first: both write the same library database." },
            progress,
            status,
            progressFrame,
            ffmpegFrame);
    }

    private void StartScan()
    {
        if (scanning)
        {
            return;
        }

        if (PrepareConfiguration?.Invoke() is { } error)
        {
            MessageBox.ErrorQuery(app, "Cannot scan", error, "OK");
            return;
        }

        scanning = true;
        scan.Enabled = false;
        lines.Clear();
        ffmpegLines.Clear();
        FfmpegLogBuffer.Clear();
        progress.Visible = true;
        progress.ProgressBarStyle = ProgressBarStyle.MarqueeBlocks;
        progress.Fraction = 0f;
        status.Text = "Preparing...";
        pulse = app.AddTimeout(PulseInterval, () =>
        {
            progress.Pulse();
            DrainFfmpegLog();
            return scanning;
        });

        _ = Task.Run(RunScanAsync);
    }

    private async Task RunScanAsync()
    {
        ILibraryScanService? service = null;
        void OnStarted(object? _, LibraryScanStartedEventArgs e) => Post(() =>
        {
            status.Text = $"Scanning {e.Folders.Count} folder(s)...";
            foreach (string folder in e.Folders)
            {
                Append($"Scanning {folder}");
            }
        });
        void OnProgress(object? _, LibraryScanProgressEventArgs e) => Post(() =>
            status.Text = $"Indexed {e.IndexedCount.ToString("N0", CultureInfo.InvariantCulture)} item(s)...");
        void OnWarning(object? _, LibraryScanWarningEventArgs e) => Post(() =>
            Append($"Warning: {e.Warning.Path}: {e.Warning.Message}"));

        try
        {
            LocalServices.LocalServices services = ServerLibraryHost.GetOrCreate(config);

            FfmpegLogBuffer.Attach();
            IReadOnlyList<string> applied = ServerLibraryHost.ApplyFolders(services, config);
            if (applied.Count == 0)
            {
                Post(() => Append("None of the configured music folders exist; the stored folders are kept as they are."));
            }

            service = services.LibraryScan;
            service.ScanStarted += OnStarted;
            service.ScanProgressChanged += OnProgress;
            service.ScanWarning += OnWarning;

            LibraryScanResult result = await services.LibraryScan.ScanAsync().ConfigureAwait(false);
            Post(() =>
            {
                Append(
                    $"Finished: {result.IndexedCount.ToString("N0", CultureInfo.InvariantCulture)} track(s), " +
                    $"{result.VideoIndexedCount.ToString("N0", CultureInfo.InvariantCulture)} video(s), and " +
                    $"{result.ImageIndexedCount.ToString("N0", CultureInfo.InvariantCulture)} photo(s) indexed, " +
                    $"{result.Warnings.Count} warning(s).");
                status.Text = "Finished.";
                progress.ProgressBarStyle = ProgressBarStyle.Blocks;
                progress.Fraction = 1f;
            });
        }
        catch (Exception exception)
        {
            Post(() =>
            {
                Append("Scan failed.");
                foreach (string line in exception.ToString().Split(Environment.NewLine))
                {
                    Append(line);
                }

                status.Text = "Failed.";
                progress.Visible = false;
            });
        }
        finally
        {
            if (service is not null)
            {
                service.ScanStarted -= OnStarted;
                service.ScanProgressChanged -= OnProgress;
                service.ScanWarning -= OnWarning;
            }

            Post(() =>
            {
                scanning = false;
                scan.Enabled = true;
                if (pulse is not null)
                {
                    app.RemoveTimeout(pulse);
                    pulse = null;
                }

                DrainFfmpegLog();
            });
        }
    }

    private void DrainFfmpegLog()
    {
        foreach (string line in FfmpegLogBuffer.Drain())
        {
            Append(ffmpegLines, ffmpegLog, line);
        }
    }

    private void Post(Action action) => app.Invoke(action);

    private void Append(string line) => Append(lines, log, line);

    private static void Append(ObservableCollection<string> target, ListView view, string line)
    {
        target.Add(line);
        while (target.Count > MaxLines)
        {
            target.RemoveAt(0);
        }

        view.SelectedItem = target.Count - 1;
    }
}
