using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.Logging;

namespace LightStudio.VideoValidation.Web;

internal sealed class BrowserValidationView : UserControl
{
    private readonly ValidationLaunchOptions options;
    private readonly Uri corpusBaseUri;
    private readonly IReadOnlyList<ValidationCase> cases;
    private readonly VideoView videoView = new()
    {
        Width = 640,
        Height = 360,
        Background = Brushes.Black,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock statusText = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox caseList = new();
    private readonly TextBox diagnostics = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = FontFamily.Parse("monospace"),
        FontSize = 12,
    };
    private readonly Button runButton = new() { Content = "Run web matrix" };
    private readonly Button stopButton = new() { Content = "Stop", IsEnabled = false };
    private readonly Button downloadButton = new() { Content = "Download results", IsEnabled = false };
    private readonly Dictionary<string, ValidationStatus> statuses = new();
    private ValidationRunner? runner;
    private CancellationTokenSource? runCancellation;
    private bool attached;

    public BrowserValidationView(ValidationLaunchOptions options, Uri corpusBaseUri)
    {
        this.options = options;
        this.corpusBaseUri = corpusBaseUri;
        cases = SelectCases(options);
        Content = BuildContent();
        RefreshCases();

        runButton.Click += async (_, _) => await RunAsync();
        stopButton.Click += (_, _) => runCancellation?.Cancel();
        downloadButton.Click += (_, _) => BrowserValidationInterop.Download();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private static IReadOnlyList<ValidationCase> SelectCases(ValidationLaunchOptions options)
    {
        IReadOnlyDictionary<string, ValidationAsset> assets = ValidationMatrix.Assets
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        IEnumerable<ValidationCase> selected = ValidationMatrix.Cases
            .Where(item => assets[item.AssetId].Codec != "rawvideo");
        if (options.CaseIds.Count > 0)
        {
            var ids = new HashSet<string>(options.CaseIds, StringComparer.OrdinalIgnoreCase);
            ValidationCase[] matches = selected.Where(item => ids.Contains(item.Id)).ToArray();
            string[] missing = ids.Except(matches.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
                throw new ArgumentException($"Unknown or unsupported browser cases: {string.Join(", ", missing)}.");
            selected = matches;
        }

        return selected
            .Select(item => item.DecodeMode != ValidationDecodeMode.HardwareAuto
                ? item
                : item with
                {
                    AllowedDeliveryPaths = [VideoFrameDeliveryPath.ExternalTexture, VideoFrameDeliveryPath.CopiedPlanar,
                        VideoFrameDeliveryPath.CopiedConverted],
                })
            .ToArray();
    }

    private Control BuildContent()
    {
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(18, 14),
            ColumnSpacing = 8,
        };
        toolbar.Children.Add(new TextBlock
        {
            Text = "Web video validation",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(statusText, 1);
        statusText.Margin = new Thickness(20, 0);
        statusText.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(statusText);
        AddToolbarButton(toolbar, runButton, 2);
        AddToolbarButton(toolbar, stopButton, 3);
        AddToolbarButton(toolbar, downloadButton, 4);

        caseList.Margin = new Thickness(12, 8);
        var casePane = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        casePane.Children.Add(new TextBlock
        {
            Text = "Codec cases",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(16, 12, 16, 4),
        });
        Grid.SetRow(caseList, 1);
        casePane.Children.Add(caseList);

        var videoHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 25)),
            Child = videoView,
            MinHeight = 390,
            Padding = new Thickness(10),
        };
        var outputPane = new Grid { RowDefinitions = new RowDefinitions("Auto,390,*") };
        outputPane.Children.Add(new TextBlock
        {
            Text = "Production WebCodecs / WebGL output",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(16, 12, 16, 8),
        });
        Grid.SetRow(videoHost, 1);
        outputPane.Children.Add(videoHost);
        Grid.SetRow(diagnostics, 2);
        diagnostics.Margin = new Thickness(12);
        outputPane.Children.Add(diagnostics);

        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*") };
        workspace.Children.Add(casePane);
        Grid.SetColumn(outputPane, 1);
        workspace.Children.Add(outputPane);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        Grid.SetRow(workspace, 1);
        root.Children.Add(workspace);
        return root;
    }

    private static void AddToolbarButton(Grid toolbar, Button button, int column)
    {
        button.MinHeight = 34;
        Grid.SetColumn(button, column);
        toolbar.Children.Add(button);
    }

    private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (attached)
            return;
        attached = true;
        runner = new ValidationRunner(videoView, options, SetProgress, OnResult);
        BrowserValidationInterop.SetStatus("ready", "Validation workbench ready");

        if (options.AutoRun)
            await RunAsync();
    }

    private async void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        runCancellation?.Cancel();
        ValidationRunner? closing = runner;
        runner = null;
        try
        {
            if (closing is not null)
                await closing.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppLog.Error("Validation.Web.Close", "Web validation cleanup failed.", exception);
        }
    }

    private async Task RunAsync()
    {
        if (runner is null || runCancellation is not null)
            return;
        if (cases.Count == 0)
        {
            Fail("No shared matrix cases matched the requested case IDs.");
            return;
        }

        BeginOperation();
        ValidationRunner currentRunner = runner;
        CancellationToken token = runCancellation!.Token;
        try
        {
            BrowserValidationInterop.SetStatus("loading", "Loading validation corpus");
            await BrowserCorpus.PrepareAsync(options.OutputDirectory, corpusBaseUri, cases, SetProgress, token);
            BrowserValidationInterop.SetStatus("running", $"Running {cases.Count} cases");
            ValidationRunReport report = await currentRunner.RunAsync(cases, generateAssets: false, token);
            diagnostics.Text =
                $"Run {report.RunId}{Environment.NewLine}" +
                $"Passed {report.Passed}  Failed {report.Failed}  Skipped {report.Skipped}{Environment.NewLine}" +
                $"Output {Path.Combine(options.OutputDirectory, "runs", report.RunId)}";
            await PublishResultsAsync(report, token);
        }
        catch (OperationCanceledException)
        {
            SetProgress("Run canceled");
            BrowserValidationInterop.SetStatus("canceled", "Validation run canceled");
        }
        catch (Exception exception)
        {
            AppLog.Error("Validation.Web.Run", "Web validation failed.", exception);
            diagnostics.Text = exception.ToString();
            Fail(exception.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PublishResultsAsync(ValidationRunReport report, CancellationToken cancellationToken)
    {
        string runDirectory = Path.Combine(options.OutputDirectory, "runs", report.RunId);
        string bundlePath = Path.Combine(options.OutputDirectory, $"web-validation-{report.RunId}.zip");
        if (File.Exists(bundlePath))
            File.Delete(bundlePath);
        ZipFile.CreateFromDirectory(runDirectory, bundlePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        byte[] bundle = await File.ReadAllBytesAsync(bundlePath, cancellationToken);
        string reportJson = JsonSerializer.Serialize(report, ValidationJsonContext.Default.ValidationRunReport);
        SetProgress($"Complete: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped");
        BrowserValidationInterop.Complete(reportJson, bundle, Path.GetFileName(bundlePath));
        downloadButton.IsEnabled = true;
    }

    private void OnResult(ValidationCaseResult result)
    {
        statuses[result.CaseId] = result.Status;
        RefreshCases();
        diagnostics.Text =
            $"{result.CaseId}: {result.Status}{Environment.NewLine}" +
            $"{result.Message}{Environment.NewLine}{Environment.NewLine}" +
            result.Statistics.ToDisplayString();
    }

    private void RefreshCases()
    {
        caseList.ItemsSource = cases.Select(item =>
        {
            string status = statuses.TryGetValue(item.Id, out ValidationStatus value)
                ? value.ToString().ToUpperInvariant()
                : "PENDING";
            return $"{status,-7} {item.Id}{Environment.NewLine}         {item.Purpose}";
        }).ToArray();
    }

    private void SetProgress(string message)
    {
        statusText.Text = message;
        BrowserValidationInterop.SetStatus("running", message);
    }

    private void Fail(string message)
    {
        statusText.Text = "Validation failed";
        BrowserValidationInterop.Fail(message);
    }

    private void BeginOperation()
    {
        runCancellation = new CancellationTokenSource();
        runButton.IsEnabled = false;
        stopButton.IsEnabled = true;
        downloadButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        runCancellation?.Dispose();
        runCancellation = null;
        runButton.IsEnabled = true;
        stopButton.IsEnabled = false;
    }
}