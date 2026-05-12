using FluentAssertions;
using Nemerle.LanguageServer.Tests.Infrastructure;
using Xunit;

namespace Nemerle.LanguageServer.Tests;

public class VscodeTestTests : IAsyncLifetime
{
    private ServerHarness _harness = null!;

    public Task InitializeAsync()
    {
        _harness = new ServerHarness();
        return _harness.InitializeAsync("file:///test");
    }

    public async Task DisposeAsync()
    {
        try { await _harness.DisposeAsync(); } catch { }
    }

    [Fact]
    public async Task Simple_file_compiles_and_returns_diagnostics()
    {
        var uri = "file:///test/simple.n";
        await _harness.SendDidOpenAsync(uri, "def x = 42;");
        var diags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(10));
        diags.Should().NotBeNull("server should send diagnostics");
    }

    [Fact]
    public async Task Hover_returns_content_for_identifier()
    {
        var uri = "file:///test/hover.n";
        await _harness.SendDidOpenAsync(uri, "def x = 42;");
        await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(10));

        var result = await _harness.SendRequestAsync("textDocument/hover", new
        {
            textDocument = new { uri = uri },
            position = new { line = 0, character = 4 }
        });

        result.Should().NotBeNull("hover should return result");
    }

    [Fact]
    public async Task Hover_shows_type_info_for_keyword()
    {
        var uri = "file:///test/typeinfo.n";
        await _harness.SendDidOpenAsync(uri, "def x : string = \"hi\";");
        await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(10));

        // Hover over 'string' keyword (column 9)
        var result = await _harness.SendRequestAsync("textDocument/hover", new
        {
            textDocument = new { uri = uri },
            position = new { line = 0, character = 9 }
        });

        result.Should().NotBeNull("hover should return result");
        var contents = result!["contents"]!.GetValue<string>();
        contents.Should().NotBeNullOrEmpty("hover should have markdown string");
        contents.Should().Contain("System.String", "hover on 'string' should show System.String");
    }
}
