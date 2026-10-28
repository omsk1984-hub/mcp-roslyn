using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class AnalyzeMethodToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public AnalyzeMethodToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public async Task Execute_ForGreeterGreet_ReturnsAnalysis()
    {
        var result = await AnalyzeMethodLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Signature);
        Assert.NotNull(result.File);
        Assert.True(result.Line > 0);
    }

    [Fact]
    public async Task Execute_ForGreeterGreet_HasCallers()
    {
        var result = await AnalyzeMethodLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", CancellationToken.None);

        Assert.NotNull(result);
        // Greet is called from GreeterConsumer.SayHello
        Assert.True(result.Callers.Count > 0 || result.OutgoingCalls.Count > 0);
    }

    [Fact]
    public async Task Execute_ForUnknownMethod_ThrowsSymbolNotFound()
    {
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            AnalyzeMethodLogic.ExecuteAsync(
                _loaded, _resolver, _metadata, "NoSuchClass.NoSuchMethod", CancellationToken.None));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }
}
