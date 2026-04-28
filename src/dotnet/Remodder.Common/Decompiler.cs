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

namespace Remodder.Common;

public static class Decompiler
{
    internal const string OrigType = "OrigType";
    const string DummyDll = "decomp.dll";

    /// <summary>
    /// Discovers the transpiler and its target for the given type. Delegates to <see cref="TranspilerResolver"/>.
    /// </summary>
    public static TranspilerInfo? GetTranspilerInfo(Assembly asm, string typeName)
        => TranspilerResolver.Resolve(asm, typeName);


    public static string Decompile(MethodBase orig, MethodInfo? transpiler, string[] userAsms, IDebugInfoProvider? debugInfo, MethodBase? asyncOuterMethod = null, string? asyncAssemblyPath = null)
    {
        try
        {
            return DecompileInternal(orig, transpiler, userAsms, debugInfo, anonymousMethods: true, asyncOuterMethod: asyncOuterMethod, asyncAssemblyPath: asyncAssemblyPath);
        }
        catch
        {
            return DecompileInternal(orig, transpiler, userAsms, debugInfo, anonymousMethods: false, asyncOuterMethod: asyncOuterMethod, asyncAssemblyPath: asyncAssemblyPath);
        }
    }

    private static string DecompileInternal(MethodBase orig, MethodInfo? transpiler, string[] userAsms, IDebugInfoProvider? debugInfo, bool anonymousMethods, MethodBase? asyncOuterMethod = null, string? asyncAssemblyPath = null)
    {
        using var stream = new MemoryStream();
        HarmonyCecilAdapter.WriteAssembly(stream, orig, transpiler, asyncOuterMethod, asyncAssemblyPath, userAsms);
        stream.Position = 0;

        using var peFile = new PEFile(DummyDll, stream);

        var assemblyResolver = new UniversalAssemblyResolver(
            userAsms.FirstOrDefault(),
            false,
            peFile.DetectTargetFrameworkId(),
            peFile.DetectRuntimePack()
        );

        var existingDirs = new HashSet<string>(assemblyResolver.GetSearchDirectories(), StringComparer.OrdinalIgnoreCase);
        foreach (var userAsm in userAsms.Skip(1))
        {
            var dir = Path.GetDirectoryName(userAsm);
            if (!string.IsNullOrEmpty(dir) && existingDirs.Add(dir))
                assemblyResolver.AddSearchDirectory(dir);
        }

        var settings = CreateDecompilerSettings(anonymousMethods, useDebugSymbols: debugInfo != null);

        var decompiler = new CSharpDecompiler(peFile, assemblyResolver, settings)
        {
            DebugInfoProvider = debugInfo,
        };

        // For async methods, decompile the outer type which contains the async method
        var typeName = asyncOuterMethod != null
            ? asyncOuterMethod.DeclaringType?.FullName ?? OrigType
            : orig.DeclaringType?.Name ?? OrigType;

        return decompiler.DecompileTypeAsString(new FullTypeName(typeName));
    }

    private static DecompilerSettings CreateDecompilerSettings(bool anonymousMethods, bool useDebugSymbols) =>
        new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            UseDebugSymbols = useDebugSymbols,
            AnonymousMethods = anonymousMethods,
            AsyncAwait = true,
            YieldReturn = true,
            SwitchStatementOnString = true,
            ForEachStatement = true,
            LockStatement = true,
            UsingStatement = true,
            PatternMatching = true,
            StaticLocalFunctions = true,
            NullPropagation = true,
            StringInterpolation = true,
            AggressiveScalarReplacementOfAggregates = true,
        };


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
