using System.IO;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace RoslynCodeLens.Metadata;

public sealed class IlDisassemblerAdapter
{
    private readonly PEFileCache _cache;

    public IlDisassemblerAdapter(PEFileCache cache) { _cache = cache; }

    public PEFileCache Cache => _cache;

    public string DisassembleMethod(string assemblyPath, int metadataToken)
    {
        // Stage 0 / item 4: ownership contract — PEFileCache OWNS the PEFile it returns
        // (it stores the instance, disposes stale/replaced entries in Get/Invalidate/Dispose).
        // Do NOT wrap this in `using`: that would dispose an object still live in the cache
        // and the next Get would hand out a disposed PEFile.
        var pe = _cache.Get(assemblyPath);
        using var writer = new StringWriter();
        // PlainTextOutput and ReflectionDisassembler are not IDisposable; `writer` is the
        // only resource to release here, and the StringWriter below already does it.
        var output = new PlainTextOutput(writer);
        var disasm = new ReflectionDisassembler(output, default)
        {
            DetectControlStructure = true,
            ShowSequencePoints = false,
        };
        var handle = MetadataTokens.EntityHandle(metadataToken);
        disasm.DisassembleMethod(pe, (System.Reflection.Metadata.MethodDefinitionHandle)handle);
        return writer.ToString();
    }
}
