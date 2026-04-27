using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Remodder.Common;

public static class Decompiler
{
    const string OrigType = "OrigType";
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
        WriteAssembly(stream, orig, transpiler);
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
        WriteAssembly(stream, orig, transpiler);
        stream.Position = 0;

        using var peFile = new PEFile(DummyDll, stream);
        using var writer = new StringWriter();

        var output = new PlainTextOutput(writer);
        ReflectionDisassembler rd = new ReflectionDisassembler(output, CancellationToken.None);
        rd.DetectControlStructure = false;
        rd.DisassembleType(peFile, peFile.GetTypeDefinition(new TopLevelTypeName(orig.DeclaringType?.Name ?? OrigType)));

        return writer.ToString();
    }

    static void WriteAssembly(Stream stream, MethodBase orig, MethodInfo? transpiler)
    {
        var patch = MethodPatcher.CreateDynamicMethod(orig, "", false);
        var il = patch.GetILGenerator();

        // On .NET 9+, CecilILGenerator.DeclareLocal crashes because LocalBuilder is abstract.
        // Fix: try the normal path first, fall back to declaring locals on the Cecil body directly
        // and returning real LocalBuilders from a side DynamicMethod for MethodCopier compatibility.
        var originalVariables = DeclareLocalVariablesSafe(il, orig);

        var copier = new MethodCopier(orig, il, originalVariables);
        var emitter = new Emitter(il, false);

        copier.AddTranspiler(transpiler ?? identityTranspiler);

        var endLabels = new List<Label>();
        _ = copier.Finalize(emitter, endLabels, out var hasReturnCode, out _);

        foreach (var label in endLabels)
            emitter.MarkLabel(label);

        if (hasReturnCode)
            emitter.Emit(System.Reflection.Emit.OpCodes.Ret);

        string name = patch.GetDumpName("Cecil");
        var module = ModuleDefinition.CreateModule(name, new ModuleParameters()
        {
            Kind = ModuleKind.Dll,
            ReflectionImporterProvider = MMReflectionImporter.ProviderNoDefault
        });

        var typeDef = new TypeDefinition(
            "",
            orig.DeclaringType?.Name ?? OrigType,
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.Class
        )
        {
            BaseType = module.TypeSystem.Object
        };

        module.Types.Add(typeDef);

        GenerateCecilMethod(patch, typeDef);

        module.Write(stream);
    }

    /// <summary>
    /// Declares local variables for the original method, working around the .NET 9+ issue
    /// where LocalBuilder is abstract and CecilILGenerator.DeclareLocal fails.
    ///
    /// Strategy: try the normal Harmony path first. If it fails, declare the locals directly
    /// on the Cecil MethodBody (via reflection into CecilILGenerator) and return real
    /// LocalBuilders from a side DynamicMethod for MethodCopier compatibility.
    /// </summary>
    static LocalBuilder[] DeclareLocalVariablesSafe(ILGenerator il, MethodBase orig)
    {
        // Try the normal path first — works on .NET 8 and earlier
        try
        {
            return MethodPatcher.DeclareOriginalLocalVariables(il, orig);
        }
        catch
        {
            // .NET 9+: CecilILGenerator.DeclareLocal throws because LocalBuilder is abstract
        }

        var methodBody = orig.GetMethodBody();
        if (methodBody == null)
            return [];

        var locals = methodBody.LocalVariables;

        // Create real LocalBuilders from a side DynamicMethod (these work on any .NET version)
        var sideDm = new DynamicMethod("__locals", typeof(void), Type.EmptyTypes);
        var sideIl = sideDm.GetILGenerator();
        var localBuilders = locals
            .Select(lvi => sideIl.DeclareLocal(lvi.LocalType, lvi.IsPinned))
            .ToArray();

        // Declare the variables directly on the Cecil body that backs the CecilILGenerator.
        var cecilBody = FindFieldValue<Mono.Cecil.Cil.MethodBody>(il, depth: 6);
        if (cecilBody != null)
        {
            var cecilModule = cecilBody.Method.Module;
            var locsDict = FindFieldValue<Dictionary<LocalBuilder, VariableDefinition>>(il, depth: 6);

            for (int i = 0; i < locals.Count; i++)
            {
                var varDef = new VariableDefinition(cecilModule.ImportReference(locals[i].LocalType));
                cecilBody.Variables.Add(varDef);
                locsDict?.TryAdd(localBuilders[i], varDef);
            }
        }

        return localBuilders;
    }

    /// <summary>
    /// Searches an object graph (via reflection) for a field of the specified type, up to a given depth.
    /// </summary>
    static T? FindFieldValue<T>(object obj, int depth) where T : class
    {
        if (depth == 0 || obj == null) return null;
        var bf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        foreach (var f in obj.GetType().GetFields(bf))
        {
            if (f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
            try
            {
                var val = f.GetValue(obj);
                if (val is T match) return match;
                if (val != null)
                {
                    var found = FindFieldValue<T>(val, depth - 1);
                    if (found != null) return found;
                }
            }
            catch { }
        }
        return null;
    }

    // Copied from MonoMod.Utils.DMDCecilGenerator, edited to not load the assembly
    static void GenerateCecilMethod(DynamicMethodDefinition dmd, TypeDefinition typeDef)
    {
        var def = dmd.Definition;
        var module = typeDef.Module;

        Relinker relinker = (mtp, _) => module.ImportReference(mtp);

        MethodDefinition clone = new MethodDefinition(dmd.Name ?? "_" + def.Name.Replace('.', '_'), def.Attributes, module.TypeSystem.Void)
        {
            MethodReturnType = def.MethodReturnType,
            Attributes = Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
            ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed,
            DeclaringType = typeDef
        };

        foreach (ParameterDefinition param in def.Parameters)
            clone.Parameters.Add(param.Clone().Relink(relinker, clone));

        clone.ReturnType = def.ReturnType.Relink(relinker, clone);

        typeDef.Methods.Add(clone);

        clone.HasThis = def.HasThis;
        Mono.Cecil.Cil.MethodBody body = clone.Body = def.Body.Clone(clone);

        foreach (VariableDefinition var in clone.Body.Variables)
            var.VariableType = var.VariableType.Relink(relinker, clone);

        foreach (ExceptionHandler handler in clone.Body.ExceptionHandlers)
            if (handler.CatchType != null)
                handler.CatchType = handler.CatchType.Relink(relinker, clone);

        for (int instri = 0; instri < body.Instructions.Count(); instri++)
        {
            Instruction instr = body.Instructions.ElementAt(instri);
            object operand = instr.Operand;

            if (operand is ParameterDefinition param)
                operand = clone.Parameters.ElementAt(param.Index);
            else if (operand is IMetadataTokenProvider mtp)
                operand = mtp.Relink(relinker, clone);

            instr.Operand = operand;
        }

        clone.HasThis = false;

        if (def.HasThis)
        {
            TypeReference type = def.DeclaringType;
            if (type.IsValueType)
                type = new Mono.Cecil.ByReferenceType(type);
            clone.Parameters.Insert(0, new ParameterDefinition("<>_this", Mono.Cecil.ParameterAttributes.None, type.Relink(relinker, clone)));
        }
    }

    private static MethodInfo identityTranspiler = AccessTools.Method(typeof(Decompiler), nameof(Transpiler));

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        return insts;
    }
}
