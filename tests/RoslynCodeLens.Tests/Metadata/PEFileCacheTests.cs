using ICSharpCode.Decompiler.Metadata;
using RoslynCodeLens.Metadata;

namespace RoslynCodeLens.Tests.Metadata;

public class PEFileCacheTests
{
    [Fact]
    public void Get_SamePath_ReturnsSameInstance()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        var second = cache.Get(path);

        Assert.Same(first, second);
    }

    [Fact]
    public void Invalidate_DropsEntry()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        cache.Invalidate(path);
        var second = cache.Get(path);

        Assert.NotSame(first, second);
    }

    // Stage 0 / item 4 (fix(stage0.4)): ownership contract.
    // The cache owns every PEFile it hands out — Get stores the instance, Invalidate and
    // Dispose dispose it. Callers (IlDisassemblerAdapter, PeekIlLogic.MetadataTokenFromPE)
    // must therefore NOT wrap the result in `using`: that would dispose an object still
    // live in the cache and the next Get would return a disposed PEFile. This test pins
    // the contract by proving the returned instance survives the caller going away.
    [Fact]
    public void Get_ReturnedInstance_RemainsUsableAfterCallerScope_AndIsReused()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);

        // The caller "finishes" — the cache must still hold a live instance.
        var again = cache.Get(path);
        Assert.Same(first, again);

        // Reading metadata proves the object was not disposed behind our back.
        Assert.False(first.Metadata.IsAssembly == false && first.Metadata.TypeDefinitions.Count == 0);
        Assert.NotNull(first.FileName);
    }

    // Stage 0 / item 4: Dispose is what ends the cache's ownership. After it, Get must
    // refuse rather than hand out a disposed PEFile — the failure mode a stray `using`
    // at a call site would otherwise produce silently.
    [Fact]
    public void Dispose_EndsOwnership_AndGetThrows()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;
        _ = cache.Get(path);

        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cache.Get(path));
    }

    // Stage 0 / item 4: Invalidate disposes the entry it drops, so a fresh Get must return
    // a *different, live* instance — the caller-owns model would leave the old one alive.
    [Fact]
    public void Invalidate_DisposesDroppedEntry_AndReturnsLiveReplacement()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        cache.Invalidate(path);
        var second = cache.Get(path);

        Assert.NotSame(first, second);
        // Replacement is usable, i.e. Invalidate did not poison the cache.
        Assert.NotNull(second.FileName);
        Assert.NotNull(second.Metadata);
    }
}
