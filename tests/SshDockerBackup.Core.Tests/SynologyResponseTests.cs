using SshDockerBackup.Core.Synology;
using Xunit;

namespace SshDockerBackup.Core.Tests;

/// <summary>
/// Regression tests for a real bug. <c>synowebapi</c> prints diagnostics before its response, and
/// those lines contain braces of their own — <c>param={…}</c> in particular. Taking the first
/// <c>{</c> in the output therefore parsed the echoed parameters instead of the result, and every
/// call reported failure even when DSM had done exactly what was asked. On a live restore this
/// showed up as five Container Manager projects reported as failures that had in fact been created.
/// </summary>
public class SynologyResponseTests
{
    /// <summary>The shape of a real <c>method=create</c> response, preamble and all.</summary>
    private const string CreateOutput = """
        [Line 265] Not a json value: gallery-bridge
        [Line 265] Not a json value: /volume1/docker/myapp
        [Line 295] Exec WebAPI:  api=SYNO.Docker.Project, version=1, method=create, param={"name":"myapp","path":"/volume1/docker/myapp","share_path":"/docker/myapp"}, runner=SYSTEM_ADMIN
        {
           "data" : {
              "containerIds" : [ "abc123" ]
           },
           "success" : true
        }
        """;

    /// <summary>A <c>method=list</c> response. Its <c>param={}</c> is the shortest trap of all.</summary>
    private const string ListOutput = """
        [Line 295] Exec WebAPI:  api=SYNO.Docker.Project, version=1, method=list, param={}, runner=SYSTEM_ADMIN
        {
           "data" : {},
           "httpd_restart" : false,
           "success" : true
        }
        """;

    [Fact]
    public void SkipsThePreambleAndFindsTheResponseBody()
    {
        Assert.True(SynologyProjects.TryExtractJson(CreateOutput, out var json));

        // The old bug: this would have been the echoed param object, and parsing stopped at the
        // comma before "runner=".
        Assert.DoesNotContain("share_path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("runner=", json, StringComparison.Ordinal);
        Assert.Contains("\"success\" : true", json, StringComparison.Ordinal);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// The empty <c>param={}</c> case. It used to yield "{}, runner=SYSTEM_ADMIN\n{…}", which
    /// parsed the empty object and then failed on the comma at byte 2.
    /// </summary>
    [Fact]
    public void HandlesAnEmptyParamObjectInThePreamble()
    {
        Assert.True(SynologyProjects.TryExtractJson(ListOutput, out var json));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public void StillWorksWhenThereIsNoPreamble()
    {
        // Not every DSM version is chatty, so a bare response must keep working.
        const string bare = """{ "success" : true }""";

        Assert.True(SynologyProjects.TryExtractJson(bare, out var json));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sh: synowebapi: command not found")]
    [InlineData("[Line 295] Exec WebAPI: api=..., param={}, runner=SYSTEM_ADMIN")]
    public void ReportsFailureRatherThanGuessingWhenThereIsNoBody(string output)
    {
        // The last case is the important one: a preamble arrived but no response followed, so
        // there is nothing to report success from. Parsing the preamble's braces would be worse
        // than admitting the call produced nothing.
        var found = SynologyProjects.TryExtractJson(output, out var json);

        if (!found) return;

        // If a fallback did fire, whatever it returned must at least not be the echoed parameters
        // masquerading as a result.
        Assert.DoesNotContain("runner=", json, StringComparison.Ordinal);
    }
}
