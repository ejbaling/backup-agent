using System.Diagnostics;

namespace BackupAgent.Utilities;

public record CommandResult(bool Success, int ExitCode, string Output, string Error);

public class CommandRunner
{
    public async Task<CommandResult> RunCommand(string command, string args, IDictionary<string, string>? environment = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (environment != null)
        {
            foreach (var kv in environment)
            {
                // set environment variable for the child process
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        try
        {
            using var proc = Process.Start(psi)!;
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask).WaitAsync(ct);

            proc.WaitForExit();

            var outp = outputTask.Result;
            var err = errorTask.Result;
            var code = proc.ExitCode;

            return new CommandResult(code == 0, code, outp, err);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, -1, string.Empty, "Cancelled");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, -1, string.Empty, ex.Message);
        }
    }
}
