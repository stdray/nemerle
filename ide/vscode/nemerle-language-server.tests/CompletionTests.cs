using FluentAssertions;
using System.Text.Json.Nodes;
using Nemerle.LanguageServer.Tests.Infrastructure;
using Xunit;

namespace Nemerle.LanguageServer.Tests;

public class CompletionTests : IAsyncLifetime
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
    public async Task Returns_keywords_when_no_prefix()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n", "def x = 1;\n");

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = "file:///test/test.n" },
            position = new { line = 1, character = 0 }
        });

        // result should be CompletionList with items
        var items = result!["items"]?.AsArray();
        items.Should().NotBeNull();
        items!.Should().NotBeEmpty("should return keywords at empty position");
        items.Should().Contain(i => i!["label"]!.GetValue<string>() == "def");
        items.Should().Contain(i => i!["label"]!.GetValue<string>() == "class");
    }

    [Fact]
    public async Task Filters_by_prefix()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n", "mac");

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = "file:///test/test.n" },
            position = new { line = 0, character = 3 }
        });

        var items = result!["items"]?.AsArray();
        items.Should().NotBeNull();
        // With prefix "mac", should match "macro"
        items!.Should().Contain(i => i!["label"]!.GetValue<string>() == "macro");
    }

    [Fact]
    public async Task Includes_local_identifiers()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n",
            "def myLocalVar = 42;\ndef x = myL");

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = "file:///test/test.n" },
            position = new { line = 1, character = 12 }
        });

        var items = result!["items"]?.AsArray();
        items.Should().NotBeNull();
        // Should find 'myLocalVar' in completions
        items!.Should().Contain(i => i!["label"]!.GetValue<string>() == "myLocalVar");
    }

    [Fact]
    public async Task Includes_stdlib_types()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n", "Li");

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = "file:///test/test.n" },
            position = new { line = 0, character = 2 }
        });

        var items = result!["items"]?.AsArray();
        items.Should().NotBeNull();
        // With prefix "Li", should match "List" and "LazyValue" (both start with "Li")
        items!.Should().Contain(i => i!["label"]!.GetValue<string>() == "List");
        items.Should().Contain(i => i!["label"]!.GetValue<string>() == "list");
    }

    [Fact]
    public async Task Returns_CompletionList_structure()
    {
        await _harness.SendDidOpenAsync("file:///test/test.n", "def x = 1;");

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = "file:///test/test.n" },
            position = new { line = 1, character = 0 }
        });

        result.Should().NotBeNull();
        result!["isIncomplete"]!.GetValue<bool>().Should().BeFalse("completion list should not be incomplete");
    }
}
