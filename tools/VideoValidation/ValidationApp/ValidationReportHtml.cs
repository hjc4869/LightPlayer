using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightMediaRenderer.Avalonia.Rendering;

namespace LightStudio.VideoValidation;

public static class ValidationReportHtml
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public static async Task<string> WriteFromJsonAsync(
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(reportPath);
        await using FileStream stream = File.OpenRead(fullPath);
        ValidationRunReport report = await JsonSerializer.DeserializeAsync(
            stream,
            ValidationJsonContext.Default.ValidationRunReport,
            cancellationToken) ?? throw new InvalidDataException($"Could not read validation report '{fullPath}'.");
        return await WriteAsync(fullPath, report, cancellationToken);
    }

    public static async Task<string> WriteAsync(
        string reportPath,
        ValidationRunReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        string fullPath = Path.GetFullPath(reportPath);
        string htmlPath = Path.ChangeExtension(fullPath, ".html");
        string directory = Path.GetDirectoryName(fullPath)!;
        string html = Render(report, directory);
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), cancellationToken);
        return htmlPath;
    }

    public static string Render(ValidationRunReport report, string reportDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);

        bool complete = report.FinishedAtUtc > report.StartedAtUtc;
        int total = report.Results.Count;
        string overallStatus = report.Failed > 0 ? "failed" : complete ? "passed" : "running";
        var html = new StringBuilder(64 * 1024);
        html.Append(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="color-scheme" content="light">
              <title>Light Player video validation</title>
              <style>
                :root { color-scheme: light; --paper:#f7f8f6; --ink:#18201d; --muted:#5d6964; --line:#cfd6d2; --panel:#fff; --pass:#147a48; --pass-bg:#e5f5ec; --fail:#b62d32; --fail-bg:#fdebed; --skip:#956400; --skip-bg:#fff4d6; --run:#176a8f; --run-bg:#e5f4fa; --accent:#0c6250; }
                * { box-sizing:border-box; }
                body { margin:0; background:var(--paper); color:var(--ink); font-family:"Aptos","Segoe UI Variable",sans-serif; font-size:15px; letter-spacing:0; }
                body::before { content:""; position:fixed; inset:0; pointer-events:none; opacity:.32; background-image:linear-gradient(#dfe5e1 1px,transparent 1px),linear-gradient(90deg,#dfe5e1 1px,transparent 1px); background-size:32px 32px; mask-image:linear-gradient(to bottom,black,transparent 420px); }
                main { position:relative; width:min(1540px,calc(100% - 36px)); margin:0 auto; padding:34px 0 70px; }
                h1,h2,h3,p { margin-top:0; }
                h1 { margin-bottom:7px; font-family:"Iowan Old Style","Palatino Linotype",serif; font-size:clamp(28px,4vw,48px); font-weight:600; line-height:1.05; letter-spacing:0; }
                h2 { margin:36px 0 14px; font-size:18px; letter-spacing:0; }
                a { color:var(--accent); text-underline-offset:3px; }
                code,pre,.mono { font-family:"Cascadia Mono","Liberation Mono",monospace; }
                .topline { display:flex; align-items:flex-start; justify-content:space-between; gap:20px; border-bottom:2px solid var(--ink); padding-bottom:22px; }
                .subtitle { margin:0; color:var(--muted); }
                .overall { flex:none; padding:8px 12px; border:1px solid currentColor; border-radius:4px; font-weight:700; text-transform:uppercase; }
                .overall.passed { color:var(--pass); background:var(--pass-bg); }
                .overall.failed { color:var(--fail); background:var(--fail-bg); }
                .overall.running { color:var(--run); background:var(--run-bg); }
                .summary { display:grid; grid-template-columns:repeat(5,minmax(120px,1fr)); border-bottom:1px solid var(--line); }
                .stat { padding:17px 18px 16px 0; }
                .stat + .stat { border-left:1px solid var(--line); padding-left:18px; }
                .stat-value { display:block; font-size:25px; font-weight:700; font-variant-numeric:tabular-nums; }
                .stat-label { display:block; margin-top:2px; color:var(--muted); font-size:12px; text-transform:uppercase; }
                .toolbar { display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin:0 0 12px; }
                .toolbar input { min-width:250px; flex:1; border:1px solid #adb9b3; border-radius:4px; padding:9px 11px; background:#fff; color:var(--ink); font:inherit; }
                .toolbar button { border:1px solid #9eaaa4; border-radius:4px; padding:8px 11px; background:#fff; color:var(--ink); font:inherit; cursor:pointer; }
                .toolbar button[aria-pressed="true"] { border-color:var(--accent); background:#dff0eb; color:#084f41; font-weight:700; }
                .result { margin:0; border-top:1px solid var(--line); background:rgba(255,255,255,.72); }
                .result:last-of-type { border-bottom:1px solid var(--line); }
                .result[hidden] { display:none; }
                .result summary { display:grid; grid-template-columns:90px minmax(260px,2fr) minmax(150px,1fr) minmax(130px,1fr) 90px; gap:14px; align-items:center; min-height:56px; padding:8px 12px; cursor:pointer; list-style:none; }
                .result summary::-webkit-details-marker { display:none; }
                .result summary::after { content:"+"; position:absolute; right:12px; color:var(--muted); }
                .result[open] summary::after { content:"−"; }
                .result summary { position:relative; padding-right:38px; }
                .case-id { min-width:0; overflow-wrap:anywhere; font-weight:700; }
                .case-sub { display:block; color:var(--muted); font-size:12px; font-weight:400; margin-top:3px; }
                .status { display:inline-block; width:max-content; min-width:68px; padding:5px 8px; border-radius:3px; font-size:12px; font-weight:800; text-align:center; text-transform:uppercase; }
                .status.passed { color:var(--pass); background:var(--pass-bg); }
                .status.failed { color:var(--fail); background:var(--fail-bg); }
                .status.skipped { color:var(--skip); background:var(--skip-bg); }
                .detail { display:grid; grid-template-columns:minmax(260px,440px) 1fr; gap:22px; padding:8px 12px 24px 116px; border-top:1px dashed var(--line); }
                .capture { width:100%; aspect-ratio:16/9; object-fit:contain; background:#101412; border:1px solid #29332e; }
                .artifact-links { display:flex; gap:14px; margin-top:8px; font-size:13px; }
                .message { margin:0 0 14px; padding-left:10px; border-left:3px solid var(--line); color:#35413c; }
                .facts { display:grid; grid-template-columns:repeat(3,minmax(150px,1fr)); gap:1px; background:var(--line); border:1px solid var(--line); }
                .fact { min-width:0; padding:9px 10px; background:var(--panel); }
                .fact dt { color:var(--muted); font-size:11px; text-transform:uppercase; }
                .fact dd { margin:4px 0 0; overflow-wrap:anywhere; font-size:13px; }
                .environment { border-top:1px solid var(--line); }
                .environment summary { cursor:pointer; padding:14px 0; font-weight:700; }
                .env-grid { display:grid; grid-template-columns:repeat(4,minmax(160px,1fr)); gap:1px; background:var(--line); border:1px solid var(--line); }
                .platform { margin-top:14px; border-top:1px solid var(--line); }
                .platform summary { padding:10px 0; font-family:"Cascadia Mono","Liberation Mono",monospace; font-size:13px; }
                pre { max-height:420px; overflow:auto; margin:0 0 18px; padding:13px; border:1px solid var(--line); background:#111815; color:#dce8e2; font-size:12px; line-height:1.45; white-space:pre-wrap; overflow-wrap:anywhere; }
                .empty { padding:30px 0; color:var(--muted); }
                footer { margin-top:34px; padding-top:15px; border-top:2px solid var(--ink); color:var(--muted); font-size:12px; }
                @media (max-width:900px) { main{width:min(100% - 20px,1540px);padding-top:20px}.summary{grid-template-columns:repeat(2,1fr)}.stat:nth-child(odd){border-left:0;padding-left:0}.result summary{grid-template-columns:74px minmax(180px,1fr) 120px}.result summary .secondary{display:none}.detail{grid-template-columns:1fr;padding-left:12px}.facts{grid-template-columns:repeat(2,1fr)}.env-grid{grid-template-columns:repeat(2,1fr)} }
                @media (max-width:560px) { .topline{display:block}.overall{display:inline-block;margin-top:14px}.summary{grid-template-columns:1fr 1fr}.result summary{grid-template-columns:70px 1fr}.result summary .route,.result summary .secondary{display:none}.facts,.env-grid{grid-template-columns:1fr}.toolbar input{min-width:100%} }
                @media print { body::before,.toolbar{display:none}.result{break-inside:avoid}.result:not([open]) .detail{display:none}main{width:100%;padding:0} }
              </style>
            </head>
            <body>
            <main>
            """);

        html.Append("<header class=\"topline\"><div><h1>Light Player video validation</h1><p class=\"subtitle\">Run <span class=\"mono\">")
            .Append(E(report.RunId))
            .Append("</span> · ")
            .Append(E(FormatTimestamp(report.StartedAtUtc)))
            .Append("</p></div><span class=\"overall ")
            .Append(overallStatus)
            .Append("\">")
            .Append(complete ? report.Failed > 0 ? "Conformance failed" : "Conformant" : "In progress")
            .Append("</span></header>");

        html.Append("<section class=\"summary\" aria-label=\"Run summary\">");
        AppendStat(html, report.Passed.ToString(CultureInfo.InvariantCulture), "Passed");
        AppendStat(html, report.Failed.ToString(CultureInfo.InvariantCulture), "Failed");
        AppendStat(html, report.Skipped.ToString(CultureInfo.InvariantCulture), "Skipped");
        AppendStat(html, total.ToString(CultureInfo.InvariantCulture), "Completed");
        AppendStat(html, complete ? FormatDuration(report.FinishedAtUtc - report.StartedAtUtc) : "Running", "Run time");
        html.Append("</section><h2>Cases</h2>");

        html.Append(
            """
            <div class="toolbar" role="toolbar" aria-label="Result filters">
              <input id="search" type="search" placeholder="Search cases, codecs, paths, formats…" aria-label="Search cases">
              <button type="button" data-filter="all" aria-pressed="true">All</button>
              <button type="button" data-filter="passed" aria-pressed="false">Passed</button>
              <button type="button" data-filter="failed" aria-pressed="false">Failed</button>
              <button type="button" data-filter="skipped" aria-pressed="false">Skipped</button>
            </div>
            <section id="results" aria-label="Case results">
            """);

        foreach (ValidationCaseResult result in report.Results)
            AppendResult(html, result, reportDirectory);
        if (report.Results.Count == 0)
            html.Append("<p class=\"empty\">No cases have completed yet.</p>");
        html.Append("</section>");

        AppendEnvironment(html, report.Environment);
        html.Append("<footer>Generated beside <a href=\"report.json\">report.json</a>. No network resources are required.</footer>");
        html.Append(
            """
            </main>
            <script>
              (() => {
                const search = document.querySelector('#search');
                const buttons = [...document.querySelectorAll('[data-filter]')];
                const rows = [...document.querySelectorAll('.result')];
                let filter = 'all';
                const apply = () => {
                  const query = search.value.trim().toLowerCase();
                  for (const row of rows) {
                    const statusMatch = filter === 'all' || row.dataset.status === filter;
                    const textMatch = !query || row.dataset.search.includes(query);
                    row.hidden = !(statusMatch && textMatch);
                  }
                };
                search.addEventListener('input', apply);
                for (const button of buttons) button.addEventListener('click', () => {
                  filter = button.dataset.filter;
                  for (const candidate of buttons) candidate.setAttribute('aria-pressed', String(candidate === button));
                  apply();
                });
              })();
            </script>
            </body>
            </html>
            """);
        return html.ToString();
    }

    private static void AppendResult(StringBuilder html, ValidationCaseResult result, string reportDirectory)
    {
        string status = result.Status.ToString().ToLowerInvariant();
        VideoDecoderDiagnostics decoder = result.Decoder;
        VideoOutputStatus output = result.Output;
        string search = string.Join(' ',
            result.CaseId,
            result.AssetId,
            result.Status,
            decoder.CodecName,
            decoder.DecoderName,
            decoder.DeliveryPath,
            decoder.SamplePixelFormat,
            decoder.OutputFormat,
            decoder.HardwareDeviceType,
            output.Scaling).ToLowerInvariant();
        string captureHref = ArtifactHref(reportDirectory, result.CapturePath);
        string nativeCaptureHref = ArtifactHref(reportDirectory, result.NativeCapturePath);
        string logHref = ArtifactHref(reportDirectory, result.CaseLogPath);
        bool captureExists = ArtifactExists(reportDirectory, result.CapturePath);
        bool nativeCaptureExists = ArtifactExists(reportDirectory, result.NativeCapturePath);
        bool logExists = ArtifactExists(reportDirectory, result.CaseLogPath);

        html.Append("<details class=\"result\" data-status=\"")
            .Append(status)
            .Append("\" data-search=\"")
            .Append(E(search))
            .Append("\"")
            .Append(result.Status == ValidationStatus.Failed ? " open" : string.Empty)
            .Append("><summary><span class=\"status ")
            .Append(status)
            .Append("\">")
            .Append(E(result.Status.ToString()))
            .Append("</span><span class=\"case-id\">")
            .Append(E(result.CaseId))
            .Append("<span class=\"case-sub\">")
            .Append(E(result.AssetId))
            .Append("</span></span><span class=\"route mono\">")
            .Append(E(decoder.DeliveryPath.ToString()))
            .Append("</span><span class=\"secondary mono\">")
            .Append(E(decoder.OutputFormat?.ToString() ?? "No output"))
            .Append("</span><span class=\"secondary mono\">")
            .Append(E(FormatDuration(result.Elapsed)))
            .Append("</span></summary><div class=\"detail\"><div>");

        if (captureExists)
        {
            html.Append("<a href=\"").Append(E(captureHref)).Append("\"><img class=\"capture\" loading=\"lazy\" src=\"")
                .Append(E(captureHref))
                .Append("\" alt=\"Rendered capture for ")
                .Append(E(result.CaseId))
                .Append("\"></a>");
        }
        else
        {
            html.Append("<div class=\"capture\" role=\"img\" aria-label=\"No rendered capture available\"></div>");
        }

        html.Append("<div class=\"artifact-links\">");
        if (captureExists)
            html.Append("<a href=\"").Append(E(captureHref)).Append("\">Open capture</a>");
        if (nativeCaptureExists)
            html.Append("<a href=\"").Append(E(nativeCaptureHref)).Append("\">Download float PFM</a>");
        if (logExists)
            html.Append("<a href=\"").Append(E(logHref)).Append("\">Open case log</a>");
        html.Append("</div></div><div><p class=\"message\">").Append(E(result.Message)).Append("</p><dl class=\"facts\">");

        AppendFact(html, "Decoder", JoinNonEmpty(decoder.Implementation, decoder.DecoderName, " / "));
        AppendFact(html, "Codec", decoder.CodecName);
        AppendFact(html, "Picture", $"{decoder.Width}×{decoder.Height} · {decoder.BitDepth}-bit");
        AppendFact(html, "Delivery", decoder.DeliveryPath.ToString());
        AppendFact(html, "Samples → output", $"{Value(decoder.SamplePixelFormat)} → {decoder.OutputFormat?.ToString() ?? "none"}");
        AppendFact(html, "Hardware", decoder.IsHardwareAccelerated ? Value(decoder.HardwareDeviceType) : "Software");
        AppendFact(html, "Zero copy", decoder.ZeroCopyActive ? $"Active ({decoder.SurfaceKind})" : decoder.ZeroCopyAvailable ? "Available" : "No");
        AppendFact(html, "Color", $"{decoder.ColorSpace} / {decoder.ColorPrimaries} / {decoder.ColorTransfer} / {decoder.ColorRange}");
        AppendFact(html, "Chroma", decoder.ChromaLocation.ToString());
        AppendFact(html, "Surface", output.SurfaceObserved ? $"{output.SurfaceEncoding} / {output.SurfaceColorSpace}" : "Not observed");
        AppendFact(
            html,
            "Scaling",
            output.EffectiveScaling is { } effective && effective != output.Scaling
                ? $"{output.Scaling} declined → {effective}"
                : output.Scaling.ToString());
        AppendFact(html, "HDR output", output.PlatformToneMapping ? "Platform tone mapping"
            : output.HdrAdaptedToDisplay ? "Client tone mapping"
            : output.SourceTransfer is LightMediaRenderer.Contracts.VideoColorTransfer.Pq or LightMediaRenderer.Contracts.VideoColorTransfer.Hlg
                ? "Source highlights preserved" : "SDR");
        AppendFact(html, "Frames", $"{result.Statistics.DecodedVideoFrames} decoded · {result.Statistics.PresentedVideoFrames} presented · {result.Statistics.DroppedVideoFrames} dropped");
        AppendFact(html, "Decode errors", result.Statistics.DecodeErrors.ToString(CultureInfo.InvariantCulture));
        AppendFact(html, "Comparison domain", result.Image?.ComparisonDomain.ToString() ?? "n/a");
        bool floatDomain = result.Image?.ComparisonDomain == RenderedFrameComparisonDomain.ScRgbFloat;
        AppendFact(html, floatDomain ? "Diagnostic PNG MAE" : "Image MAE", result.Image is null ? "n/a" : result.Image.MeanAbsoluteError.ToString("0.###", CultureInfo.InvariantCulture));
        AppendFact(html, floatDomain ? "Diagnostic PNG luma correlation" : "Luma correlation", result.Image is null ? "n/a" : result.Image.LumaCorrelation.ToString("0.0000", CultureInfo.InvariantCulture));
        if (floatDomain)
        {
            AppendFact(html, "Float MAE", result.Image.LinearMeanAbsoluteError.ToString("0.00000", CultureInfo.InvariantCulture));
            AppendFact(html, "Float NRMSE", result.Image.LinearNormalizedRootMeanSquareError.ToString("0.00000", CultureInfo.InvariantCulture));
            AppendFact(html, "Float luma correlation", result.Image.LinearLumaCorrelation.ToString("0.00000", CultureInfo.InvariantCulture));
            AppendFact(html, "Non-finite floats", result.Image.NonFiniteLinearSamples.ToString(CultureInfo.InvariantCulture));
        }
        if (result.Image is { GamutComparedPixels: > 0 } gamut)
        {
            AppendFact(html, "Gamut mean error", gamut.GamutMeanAbsoluteError.ToString("0.00000", CultureInfo.InvariantCulture));
            AppendFact(
                html,
                "Gamut max error",
                $"{gamut.GamutMaximumAbsoluteError.ToString("0.00000", CultureInfo.InvariantCulture)} over {gamut.GamutComparedPixels} flat pixels");
        }
        AppendFact(html, "Pattern colors", result.Image?.QuantizedColorCount.ToString(CultureInfo.InvariantCulture) ?? "n/a");
        AppendFact(html, "Elapsed", FormatDuration(result.Elapsed));
        html.Append("</dl></div></div></details>");
    }

    private static void AppendEnvironment(StringBuilder html, ValidationEnvironmentReport environment)
    {
        html.Append("<h2>Environment</h2><details class=\"environment\" open><summary>Runtime and display</summary><dl class=\"env-grid\">");
        AppendFact(html, "Operating system", environment.OperatingSystem);
        AppendFact(html, "Architecture", environment.Architecture);
        AppendFact(html, "Framework", environment.Framework);
        AppendFact(html, "Processors", environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        AppendFact(html, "Session", Value(environment.SessionType));
        AppendFact(html, "Desktop", Value(environment.Desktop));
        AppendFact(html, "Display", Value(environment.Display));
        AppendFact(html, "Culture", Value(environment.CurrentCulture));
        html.Append("</dl></details>");

        foreach ((string key, string value) in environment.PlatformDetails.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            html.Append("<details class=\"platform\"><summary>")
                .Append(E(key))
                .Append("</summary><pre>")
                .Append(E(value))
                .Append("</pre></details>");
        }
    }

    private static void AppendStat(StringBuilder html, string value, string label) =>
        html.Append("<div class=\"stat\"><span class=\"stat-value\">")
            .Append(E(value))
            .Append("</span><span class=\"stat-label\">")
            .Append(E(label))
            .Append("</span></div>");

    private static void AppendFact(StringBuilder html, string label, string value) =>
        html.Append("<div class=\"fact\"><dt>")
            .Append(E(label))
            .Append("</dt><dd>")
            .Append(E(Value(value)))
            .Append("</dd></div>");

    private static string ArtifactHref(string reportDirectory, string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
            return string.Empty;
        string path = Path.IsPathRooted(artifactPath)
            ? Path.GetRelativePath(reportDirectory, artifactPath)
            : artifactPath;
        return string.Join('/', path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    private static bool ArtifactExists(string reportDirectory, string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
            return false;
        string path = Path.IsPathRooted(artifactPath) ? artifactPath : Path.Combine(reportDirectory, artifactPath);
        return File.Exists(path);
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value == default ? "Not recorded" : value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan value) => value.TotalMinutes >= 1
        ? $"{(int)value.TotalMinutes}:{value.Seconds:00}.{value.Milliseconds:000}"
        : $"{value.TotalSeconds:0.000}s";

    private static string JoinNonEmpty(string first, string second, string separator) =>
        string.IsNullOrWhiteSpace(first) ? Value(second) : string.IsNullOrWhiteSpace(second) ? first : first + separator + second;

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "n/a" : value;

    private static string E(string? value) => Encoder.Encode(value ?? string.Empty);
}