using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.Logging;

namespace LightStudio.VideoValidation;

public sealed class ValidationWindow : Window
{
    private readonly ValidationLaunchOptions options;
    private readonly VideoView videoView = new()
    {
        Width = 640,
        Height = 360,
        Background = Brushes.Black,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ListBox caseList = new();
    private readonly TextBlock statusText = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox diagnostics = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = FontFamily.Parse("monospace"),
        FontSize = 12,
    };
    private readonly Button generateButton = new() { Content = "Generate assets" };
    private readonly Button runSelectedButton = new() { Content = "Run selected" };
    private readonly Button runAllButton = new() { Content = "Run all" };
    private readonly Button stopButton = new() { Content = "Stop", IsEnabled = false };
    private readonly Dictionary<string, ValidationStatus> statuses = new();
    private ValidationRunner? runner;
    private CancellationTokenSource? runCancellation;

    public ValidationWindow(ValidationLaunchOptions options)
    {
        this.options = options;
        Title = "Light Player Video Validation";
        Width = 1240;
        Height = 820;
        MinWidth = 940;
        MinHeight = 680;
        Topmost = options.AutoRun || options.GenerateOnly;
        ScrollViewer.SetVerticalScrollBarVisibility(diagnostics, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(diagnostics, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Content = BuildContent();
        RefreshCases();

        generateButton.Click += async (_, _) => await GenerateAsync();
        runSelectedButton.Click += async (_, _) => await RunSelectedAsync();
        runAllButton.Click += async (_, _) => await RunAllAsync();
        stopButton.Click += (_, _) => runCancellation?.Cancel();
        Closed += async (_, _) =>
        {
            runCancellation?.Cancel();
            try
            {
                if (runner is not null)
                    await runner.DisposeAsync();
            }
            catch (Exception exception)
            {
                AppLog.Error("Validation.Close", "Validation cleanup failed.", exception);
            }
        };
        Opened += OnOpened;
    }

    private Control BuildContent()
    {
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(18, 14),
            ColumnSpacing = 8,
        };
        var heading = new TextBlock
        {
            Text = "Video validation",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toolbar.Children.Add(heading);
        Grid.SetColumn(statusText, 1);
        statusText.VerticalAlignment = VerticalAlignment.Center;
        statusText.Margin = new Thickness(20, 0);
        toolbar.Children.Add(statusText);
        AddToolbarButton(toolbar, generateButton, 2);
        AddToolbarButton(toolbar, runSelectedButton, 3);
        AddToolbarButton(toolbar, runAllButton, 4);
        AddToolbarButton(toolbar, stopButton, 5);

        caseList.Margin = new Thickness(12, 8);
        caseList.SelectionMode = SelectionMode.Single;

        var casePane = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        casePane.Children.Add(new TextBlock
        {
            Text = "Matrix cases",
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
            Text = "Production renderer output",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(16, 12, 16, 8),
        });
        Grid.SetRow(videoHost, 1);
        outputPane.Children.Add(videoHost);
        Grid.SetRow(diagnostics, 2);
        diagnostics.Margin = new Thickness(12);
        outputPane.Children.Add(diagnostics);

        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("330,*"),
        };
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

    private async void OnOpened(object? sender, EventArgs e)
    {
        runner = new ValidationRunner(videoView, options, SetProgress, OnResult, ExposeRenderTarget);
        if (!options.AutoRun && !options.GenerateOnly)
            return;

        try
        {
            Activate();
            await Task.Delay(500);
            if (options.GenerateOnly)
            {
                bool generated = await GenerateAsync();
                Shutdown(generated ? 0 : 1);
                return;
            }

            await RunAllAsync();
        }
        catch
        {
            if (options.ExitWhenComplete)
                Shutdown(1);
        }
    }

    private async Task<bool> GenerateAsync()
    {
        if (runner is null || runCancellation is not null)
            return false;

        BeginOperation();
        ValidationRunner currentRunner = runner;
        CancellationToken token = runCancellation!.Token;
        try
        {
            ValidationAssetManifest manifest = await currentRunner.GenerateAssetsAsync(token);
            SetProgress(
                $"Assets: {manifest.Assets.Count(asset => asset.Generated)} generated, " +
                $"{manifest.Assets.Count(asset => asset.Skipped)} skipped");
            return manifest.Assets.All(asset => asset.Generated || asset.Skipped);
        }
        catch (OperationCanceledException)
        {
            SetProgress("Generation canceled");
            return false;
        }
        catch (Exception exception)
        {
            SetProgress("Generation failed");
            diagnostics.Text = exception.ToString();
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<bool> RunSelectedAsync()
    {
        int index = caseList.SelectedIndex;
        if (index < 0 || index >= VisibleCases().Count)
        {
            SetProgress("Select a matrix case first");
            return false;
        }

        return await RunAsync([VisibleCases()[index]]);
    }

    private Task<bool> RunAllAsync()
    {
        IReadOnlyList<ValidationCase> cases = VisibleCases();
        return RunAsync(cases);
    }

    private async Task<bool> RunAsync(IReadOnlyList<ValidationCase> cases)
    {
        if (runner is null || runCancellation is not null)
            return false;

        BeginOperation();
        ValidationRunner currentRunner = runner;
        CancellationToken token = runCancellation!.Token;
        try
        {
            ValidationRunReport report = await currentRunner.RunAsync(
                cases,
                options.GenerateAssets,
                token);
            diagnostics.Text =
                $"Run {report.RunId}{Environment.NewLine}" +
                $"Passed {report.Passed}  Failed {report.Failed}  Skipped {report.Skipped}{Environment.NewLine}" +
                $"Output {Path.Combine(options.OutputDirectory, "runs", report.RunId)}";
            if (options.ExitWhenComplete)
                Shutdown(report.Failed == 0 ? 0 : 1);
            return report.Failed == 0;
        }
        catch (OperationCanceledException)
        {
            SetProgress("Run canceled");
            return false;
        }
        catch (Exception exception)
        {
            SetProgress("Run failed");
            diagnostics.Text = exception.ToString();
            if (options.ExitWhenComplete)
                Shutdown(1);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private IReadOnlyList<ValidationCase> VisibleCases()
    {
        IEnumerable<ValidationCase> cases = ValidationMatrix.Cases;
        if (options.SoftwareOnly)
            cases = cases.Where(item => item.DecodeMode == ValidationDecodeMode.Software);
        if (options.CaseIds.Count > 0)
        {
            var selected = new HashSet<string>(options.CaseIds, StringComparer.OrdinalIgnoreCase);
            cases = cases.Where(item => selected.Contains(item.Id));
        }
        return cases.ToArray();
    }

    private void RefreshCases()
    {
        int selected = caseList.SelectedIndex;
        caseList.ItemsSource = VisibleCases().Select(item =>
        {
            string status = statuses.TryGetValue(item.Id, out ValidationStatus value)
                ? value.ToString().ToUpperInvariant()
                : "PENDING";
            return $"{status,-7} {item.Id}{Environment.NewLine}         {item.Purpose}";
        }).ToArray();
        caseList.SelectedIndex = selected >= 0 ? Math.Min(selected, VisibleCases().Count - 1) : 0;
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

    private void SetProgress(string message) => statusText.Text = message;

    private void ExposeRenderTarget()
    {
        if (!options.AutoRun ||
            !string.Equals(
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                "wayland",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Hide();
        Show();
        Activate();
    }

    private void BeginOperation()
    {
        runCancellation = new CancellationTokenSource();
        generateButton.IsEnabled = false;
        runSelectedButton.IsEnabled = false;
        runAllButton.IsEnabled = false;
        stopButton.IsEnabled = true;
    }

    private void EndOperation()
    {
        runCancellation?.Dispose();
        runCancellation = null;
        generateButton.IsEnabled = true;
        runSelectedButton.IsEnabled = true;
        runAllButton.IsEnabled = true;
        stopButton.IsEnabled = false;
    }

    private static void Shutdown(int exitCode)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(exitCode);
    }
}