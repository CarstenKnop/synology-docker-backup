using SshDockerBackup.Core.Ssh;
using Xunit;

namespace SshDockerBackup.Core.Tests;

/// <summary>
/// Every command this app runs on the host goes through <see cref="SshSession.Quote"/>, and a
/// container name, a volume name or a folder path can contain anything the user typed. A quoting
/// bug here is not a wrong result, it is an arbitrary command running as root on someone's NAS.
/// </summary>
public class ShellQuotingTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("/volume1/docker/app", "'/volume1/docker/app'")]
    [InlineData("with space", "'with space'")]
    // A double quote is inert inside single quotes, so it passes through untouched.
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("", "''")]
    public void WrapsValuesInSingleQuotes(string input, string expected) =>
        Assert.Equal(expected, SshSession.Quote(input));

    /// <summary>
    /// The one case that matters: a single quote has to close the string, emit an escaped quote,
    /// and reopen it. Getting this wrong ends the quoted region early and hands whatever follows
    /// to the shell as code.
    /// </summary>
    [Fact]
    public void EscapesEmbeddedSingleQuotes()
    {
        Assert.Equal(@"'it'\''s'", SshSession.Quote("it's"));
        Assert.Equal(@"''\'''", SshSession.Quote("'"));
        Assert.Equal(@"'a'\''b'\''c'", SshSession.Quote("a'b'c"));
    }

    [Fact]
    public void NeutralisesShellMetacharacters()
    {
        // Nothing in here should survive as syntax; it is all one literal argument.
        Assert.Equal("'app; rm -rf / #'", SshSession.Quote("app; rm -rf / #"));
    }

    /// <summary>
    /// The property that actually matters, stated as a round trip: whatever
    /// <see cref="SshSession.Quote"/> produces, a POSIX shell must hand back the original string
    /// as one word. Reversing the transformation is the checkable form of that, and it holds for
    /// payloads written specifically to break out of the quoting.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("/volume1/docker/my app")]
    [InlineData("it's")]
    [InlineData("'")]
    [InlineData("''")]
    [InlineData("app'; rm -rf /; echo '")]        // close the quote, chain a command, reopen
    [InlineData("$(whoami)")]                      // command substitution
    [InlineData("`id`")]                           // the older spelling of it
    [InlineData("a\\'b")]                          // a backslash next to a quote
    [InlineData("x\"; touch /tmp/pwned; \"y")]     // double quotes, inert inside single ones
    [InlineData("multi\nline")]
    public void QuotingSurvivesARoundTrip(string original)
    {
        var quoted = SshSession.Quote(original);

        Assert.StartsWith("'", quoted, StringComparison.Ordinal);
        Assert.EndsWith("'", quoted, StringComparison.Ordinal);
        Assert.Equal(original, UnquoteAsAShellWould(quoted));
    }

    /// <summary>
    /// What <c>/bin/sh</c> does with a single-quoted word: strip the outer quotes, and treat the
    /// four-character sequence <c>'\''</c> as one literal apostrophe. If any bare quote remains in
    /// the interior after that substitution, the word was not one word and the quoting is broken.
    /// </summary>
    private static string UnquoteAsAShellWould(string quoted)
    {
        Assert.True(quoted.Length >= 2, "a quoted value is at least a pair of quotes");

        var interior = quoted[1..^1];

        // Placeholder first, so the check below cannot be fooled by the escape's own quotes.
        const string escape = @"'\''";
        const char placeholder = '';
        var unescaped = interior.Replace(escape, placeholder.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("'", unescaped, StringComparison.Ordinal);

        return unescaped.Replace(placeholder, '\'');
    }

    [Fact]
    public void BuildCommandCarriesItsOwnPath()
    {
        // DSM's sudoers secure_path omits /usr/local/bin, where docker lives, so a command
        // that does not set PATH itself fails with "command not found".
        var plain = SshSession.BuildCommand("docker ps", elevate: false);

        Assert.Contains("/usr/local/bin", plain, StringComparison.Ordinal);
        Assert.Contains("docker ps", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandReadsTheSudoPasswordFromStdin()
    {
        var elevated = SshSession.BuildCommand("docker ps", elevate: true);

        // -S sends the prompt to stderr and reads the password from stdin; -p '' silences the
        // prompt text. The password must never reach the command line, where ps would show it.
        Assert.Contains("sudo -S -p ''", elevated, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin", elevated, StringComparison.Ordinal);
    }
}
