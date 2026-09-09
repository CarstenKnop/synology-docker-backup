namespace SshDockerBackup.Core.Ssh;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>Throws with the remote stderr attached, which is what you actually need to debug.</summary>
    public CommandResult EnsureSuccess(string what)
    {
        if (!Success)
        {
            var detail = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
            throw new SshCommandException($"{what} failed (exit {ExitCode}): {detail.Trim()}");
        }
        return this;
    }
}

public sealed record TransferResult(long Bytes, string? Sha256, int ExitCode, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public class SshCommandException(string message) : Exception(message);
