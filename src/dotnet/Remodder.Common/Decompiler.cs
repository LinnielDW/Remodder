using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using HarmonyLib;

namespace Remodder.Common;

public static class Decompiler
{
    internal const string OrigType = "OrigType";
    const string DummyDll = "decomp.dll";

    public static AttributePatch? GetTranspiler(Assembly asm, string typeName)
    {
        var type = asm.GetType(typeName);
        if (type == null)
            return null;
        var transpiler = new Harmony("dummy").
            CreateClassProcessor(type).patchMethods?.
            FirstOrDefault(p => p.type == HarmonyPatchType.Transpiler);
        return transpiler;
    }

    public static string Decompile(MethodBase orig, MethodInfo? transpiler, string[] userAsms, IDebugInfoProvider? debugInfo)
    {
        using var stream = new MemoryStream();
        HarmonyCecilAdapter.WriteAssembly(stream, orig, transpiler);
        stream.Position = 0;

        using var peFile = new PEFile(DummyDll, stream);
        using var writer = new StringWriter();

        var assemblyResolver = new UniversalAssemblyResolver(
            userAsms.FirstOrDefault(),
            false,
            peFile.DetectTargetFrameworkId(),
            peFile.DetectRuntimePack()
        );

        foreach (var userAsm in userAsms.Skip(1))
        {
            var dir = Path.GetDirectoryName(userAsm);
            if (!string.IsNullOrEmpty(dir) && !assemblyResolver.GetSearchDirectories().Contains(dir))
                assemblyResolver.AddSearchDirectory(Path.GetDirectoryName(userAsm));
        }

        var settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            AnonymousMethods = false,
            UseDebugSymbols = debugInfo != null
        };

        var decompiler = new CSharpDecompiler(peFile, assemblyResolver, settings)
        {
            DebugInfoProvider = debugInfo,
        };

        var code = decompiler.DecompileTypeAsString(new FullTypeName(orig.DeclaringType?.Name ?? OrigType));

        return code;
    }

    public static string Disasm(MethodBase orig, MethodInfo transpiler)
    {
        using var stream = new MemoryStream();
        HarmonyCecilAdapter.WriteAssembly(stream, orig, transpiler);
        stream.Position = 0;

        using var peFile = new PEFile(DummyDll, stream);
        using var writer = new StringWriter();

        var output = new PlainTextOutput(writer);
        ReflectionDisassembler rd = new ReflectionDisassembler(output, CancellationToken.None);
        rd.DetectControlStructure = false;
        rd.DisassembleType(peFile, peFile.GetTypeDefinition(new TopLevelTypeName(orig.DeclaringType?.Name ?? OrigType)));

        return writer.ToString();
    }
}
