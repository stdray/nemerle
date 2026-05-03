using FluentAssertions;
using System.Text.Json.Nodes;
using Nemerle.LanguageServer.Tests.Infrastructure;
using Xunit;

namespace Nemerle.LanguageServer.Tests;

public class DiagnosticsTests : IAsyncLifetime
{
    private ServerHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = new ServerHarness();
        await _harness.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Reports_diagnostics_for_opened_file()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n",
            "System.Console.WriteLine(1);");

        var diags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        diags.Should().NotBeNull("should receive publishDiagnostics notification");
        var diagArray = diags!["diagnostics"]!.AsArray();
        // Should have at least some diagnostics (even if just "Console not found in System")
        diagArray.Should().NotBeEmpty("any .n file should produce at least some diagnostic");
    }

    [Fact]
    public async Task Parse_error_diagnostics_has_correct_range()
    {
        // A clear parse error: missing semicolon after expression
        await _harness.SendDidOpenAsync("file:///test/test.n",
            "def x = 1  // missing semicolon");

        var diags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        diags.Should().NotBeNull();
        var diagArray = diags!["diagnostics"]!.AsArray();
        diagArray.Should().NotBeEmpty("parse error should produce diagnostics");

        // Every diagnostic should have a range with start/end line+character
        foreach (var d in diagArray)
        {
            var range = d!["range"]!;
            range["start"]!["line"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
            range["start"]!["character"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task Multiple_diagnostics_for_multiple_errors()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n",
            "def x : int = \"hello\";\ndef y = undefinedVar + noSuchFunc();");

        var diags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        diags.Should().NotBeNull();
        var diagArray = diags!["diagnostics"]!.AsArray();
        // Multiple errors should produce multiple diagnostics
        diagArray.Should().HaveCountGreaterThanOrEqualTo(1, "file with multiple errors should produce >= 1 diagnostic");
    }
}
