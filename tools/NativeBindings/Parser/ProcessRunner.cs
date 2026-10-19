using System.Diagnostics;

namespace NativeBindings.Parser;

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable, IEnumerable<string> arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        }
        catch (System.ComponentModel.Win32Exception exception) when (allowFailure)
        {
            return (-1, "", exception.Message);
        }
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var result = (process.ExitCode, await output, await error);
            if (result.ExitCode != 0 && !allowFailure)
                throw new InvalidOperationException($"{executable} {string.Join(' ', start.ArgumentList)} exited with {result.ExitCode}:\n{result.Item3}");
            return result;
        }
    }
}
