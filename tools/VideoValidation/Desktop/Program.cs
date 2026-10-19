using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.LightPlayer.Desktop;
using LightStudio.Logging;

namespace LightStudio.VideoValidation.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        ValidationLaunchOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintHelp();
            return 2;
        }

        if (options.RenderReportPath is { } reportPath)
        {
            try
            {
                string htmlPath = ValidationReportHtml.WriteFromJsonAsync(reportPath).GetAwaiter().GetResult();
                Console.WriteLine(htmlPath);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }

        string logPath = Path.Combine(options.OutputDirectory, "logs", "video-validation.log");
        AppLog.Configure(writeToConsole: true, logPath, AppLogLevel.Debug);
        try
        {
            FfmpegLog.Attach();
            DesktopVideoBootstrap.RegisterVideoPipeline();

            IReadOnlyDictionary<string, string> details = CollectPlatformDetails(options);
            options = options with { PlatformDetails = details };
            PersistEnvironmentProbes(options.OutputDirectory, details);
            ValidationApplication.LaunchOptions = options;

            AppLog.Information(
                "Validation.Startup",
                $"Starting desktop validator; autoRun={options.AutoRun}; generate={options.GenerateAssets}; " +
                $"softwareOnly={options.SoftwareOnly}; hardwareDevice={options.HardwareDeviceType ?? "auto"}; " +
                $"wayland={(options.EnableWayland ? "auto" : "off")}; output={options.OutputDirectory}.");
            return DesktopVideoBootstrap
                .BuildAvaloniaApp<ValidationApplication>(options.EnableWayland)
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception exception)
        {
            AppLog.Critical("Validation.Startup", "Desktop validation could not start.", exception);
            Console.Error.WriteLine(exception);
            return 2;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    private static ValidationLaunchOptions ParseOptions(string[] args)
    {
        string outputDirectory = Path.GetFullPath("artifacts/video-validation");
        bool autoRun = false;
        bool generate = false;
        bool generateOnly = false;
        bool exit = false;
        bool softwareOnly = false;
        string? hardwareDeviceType = null;
        bool enableWayland = true;
        string? renderReportPath = null;
        var caseIds = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--run":
                case "--run-all":
                    autoRun = true;
                    break;
                case "--conformance":
                    autoRun = true;
                    exit = true;
                    break;
                case "--generate":
                    generate = true;
                    break;
                case "--generate-only":
                    generate = true;
                    generateOnly = true;
                    exit = true;
                    break;
                case "--exit":
                    exit = true;
                    break;
                case "--software-only":
                    softwareOnly = true;
                    break;
                case "--hardware-device":
                    string hardware = RequireValue(args, ref index, argument);
                    hardwareDeviceType = string.Equals(hardware, "auto", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : hardware;
                    break;
                case "--wayland":
                    string wayland = RequireValue(args, ref index, argument);
                    enableWayland = wayland.ToLowerInvariant() switch
                    {
                        "auto" => true,
                        "off" => false,
                        _ => throw new ArgumentException("--wayland accepts 'auto' or 'off'."),
                    };
                    break;
                case "--output":
                    outputDirectory = Path.GetFullPath(RequireValue(args, ref index, argument));
                    break;
                case "--render-report":
                    renderReportPath = Path.GetFullPath(RequireValue(args, ref index, argument));
                    break;
                case "--case":
                    caseIds.Add(RequireValue(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{argument}'.");
            }
        }

        return new ValidationLaunchOptions
        {
            OutputDirectory = outputDirectory,
            RenderReportPath = renderReportPath,
            AutoRun = autoRun,
            GenerateAssets = generate,
            GenerateOnly = generateOnly,
            ExitWhenComplete = exit,
            SoftwareOnly = softwareOnly,
            HardwareDeviceType = hardwareDeviceType,
            EnableWayland = enableWayland,
            CaseIds = caseIds,
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static IReadOnlyDictionary<string, string> CollectPlatformDetails(ValidationLaunchOptions options)
    {
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["hardware-decoder-types"] = string.Join(", ", VideoDecoderFactory.GetAvailableHardwareDeviceTypes()),
            ["process-path"] = Environment.ProcessPath ?? string.Empty,
            ["requested-hardware-device"] = options.HardwareDeviceType ?? "auto",
            ["requested-wayland-mode"] = options.EnableWayland ? "auto" : "off",
        };

        if (OperatingSystem.IsLinux())
        {
            details["drm-nodes"] = Directory.Exists("/dev/dri")
                ? string.Join(Environment.NewLine, Directory.EnumerateFileSystemEntries("/dev/dri").Order())
                : "none";
            AddProbe(details, "lspci", "lspci", "-nnk");
            AddProbe(details, "vainfo", "vainfo");
            AddProbe(details, "vulkan", "vulkaninfo", "--summary");
            AddProbe(details, "opengl", "glxinfo", "-B");
            AddProbe(details, "nvidia", "nvidia-smi", "-L");
        }
        else if (OperatingSystem.IsWindows())
        {
            AddProbe(details, "display", "wmic", "path", "win32_VideoController", "get", "Name,DriverVersion");
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddProbe(details, "display", "system_profiler", "SPDisplaysDataType");
        }

        AddProbe(details, "ffmpeg-cli", ValidationFfmpegTools.FfmpegPath, "-hide_banner", "-version");
        return details;
    }

    private static void AddProbe(
        IDictionary<string, string> details,
        string key,
        string executable,
        params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                details[key] = "probe timed out";
                return;
            }

            string text = (output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult()).Trim();
            const int maximumLength = 32 * 1024;
            details[key] = text.Length <= maximumLength ? text : text[..maximumLength] + "\n[truncated]";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            details[key] = $"unavailable: {exception.Message}";
        }
    }

    private static void PersistEnvironmentProbes(
        string outputDirectory,
        IReadOnlyDictionary<string, string> details)
    {
        string path = Path.Combine(outputDirectory, "logs", "environment-probes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder();
        foreach ((string key, string value) in details)
            text.AppendLine($"[{key}]").AppendLine(value).AppendLine();
        File.WriteAllText(path, text.ToString());
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Light Player desktop video validation

              --run, --run-all       Run the selected matrix when the window opens
              --conformance          Run existing assets and exit with a conformance status
              --generate             Regenerate assets before a run
              --generate-only        Generate assets and exit
              --exit                 Exit when an automated run completes
              --software-only        Exclude hardware-decoding cases
              --hardware-device TYPE Prefer one runtime hardware device type, or auto
              --wayland auto|off     Mirror the desktop Wayland runtime setting
              --case ID              Run one matrix case (repeatable)
              --output DIRECTORY     Asset, capture, report, and log root
              --render-report JSON   Generate report.html for an existing report.json
              --help                 Show this help
            """);
    }
}