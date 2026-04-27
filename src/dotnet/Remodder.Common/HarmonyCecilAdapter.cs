using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using MonoMod.Utils;

namespace Remodder.Common;

/// <summary>
/// Adapter that bridges Harmony's two-phase transpiler output into a Cecil-based PE assembly.
///
/// <para><b>Why this exists:</b></para>
/// <para>
/// Harmony's transpiler pipeline produces a list of <see cref="CodeInstruction"/>s via
/// <see cref="MethodCopier.Finalize"/>. Normally Harmony would then emit those instructions
/// through its own <c>MethodCreatorTools.EmitCodes</c> into a real <see cref="DynamicMethod"/>.
/// We can't use that path because we need a <i>decompilable</i> PE assembly (not a runtime
/// DynamicMethod). So instead we:
/// </para>
///
/// <list type="number">
///   <item>Call <c>Finalize</c> to get the transpiled <c>CodeInstruction</c> list.</item>
///   <item>Expand short-form branches to long-form (<see cref="ExpandShortBranches"/>),
///         replicating what Harmony's <c>CleanupCodes</c> does internally, because we skip
///         that part of Harmony's pipeline.</item>
///   <item>Emit each <c>CodeInstruction</c> through Harmony's <see cref="Emitter"/> using
///         typed <c>Emit</c> overloads that route through the <c>CecilILGenerator</c>
///         (<see cref="EmitCodesToCecil"/>). We explicitly avoid <c>DynEmit</c> for known
///         operand types because it routes through the raw <c>ILGenerator</c> wrapper and
///         produces invalid branch targets in the Cecil output.</item>
///   <item>Clone the resulting Cecil method definition into our own <see cref="ModuleDefinition"/>
///         (<see cref="GenerateCecilMethod"/>, adapted from MonoMod's <c>DMDCecilGenerator</c>)
///         and write it to a PE stream for ILSpy/ICSharpCode.Decompiler to consume.</item>
/// </list>
///
/// <para><b>Validated against:</b> HarmonyX 2.x (Lib.Harmony 2.4.2, as bundled by BepInEx).</para>
///
/// <para><b>Known risk:</b> The <c>DynEmit</c> fallback at the end of <see cref="EmitCodesToCecil"/>
/// handles operand types not covered by the explicit type checks. If you encounter silent bad
/// output, that fallback is the first place to investigate.</para>
/// </summary>
public static class HarmonyCecilAdapter
{
    /// <summary>
    /// Runs the Harmony transpiler (or an identity transpiler if <paramref name="transpiler"/>
    /// is null) against <paramref name="orig"/>, emits the result into a Cecil PE assembly,
    /// and writes it to <paramref name="stream"/>.
    /// </summary>
    public static void WriteAssembly(Stream stream, MethodBase orig, MethodInfo? transpiler)
    {
        var patch = MethodPatcherTools.CreateDynamicMethod(orig, "", false);
        var il = patch.GetILGenerator();

        var originalVariables = MethodPatcherTools.DeclareOriginalLocalVariables(il, orig);

        var copier = new MethodCopier(orig, il, originalVariables);
        var emitter = new Emitter(il);

        copier.AddTranspiler(transpiler ?? IdentityTranspiler);

        var endLabels = new List<Label>();
        var codeInstructions = copier.Finalize(false, out var hasReturnCode, out _, endLabels);

        // Convert short branches to long branches to avoid range issues,
        // same as Harmony's CleanupCodes does internally.
        codeInstructions = ExpandShortBranches(codeInstructions);

        // Emit all instructions through the Emitter's CecilILGenerator.
        EmitCodesToCecil(emitter, codeInstructions);

        foreach (var label in endLabels)
            emitter.MarkLabel(label);

        if (hasReturnCode)
            emitter.Emit(OpCodes.Ret);

        string name = patch.GetDumpName("Cecil");
        var module = ModuleDefinition.CreateModule(name, new ModuleParameters()
        {
            Kind = ModuleKind.Dll,
            ReflectionImporterProvider = MMReflectionImporter.ProviderNoDefault
        });

        var typeDef = new TypeDefinition(
            "",
            orig.DeclaringType?.Name ?? Decompiler.OrigType,
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.Class
        )
        {
            BaseType = module.TypeSystem.Object
        };

        module.Types.Add(typeDef);

        GenerateCecilMethod(patch, typeDef);

        module.Write(stream);
    }

    static readonly Dictionary<OpCode, OpCode> ShortToLongBranch = new()
    {
        { OpCodes.Leave_S, OpCodes.Leave },
        { OpCodes.Brfalse_S, OpCodes.Brfalse },
        { OpCodes.Brtrue_S, OpCodes.Brtrue },
        { OpCodes.Beq_S, OpCodes.Beq },
        { OpCodes.Bge_S, OpCodes.Bge },
        { OpCodes.Bgt_S, OpCodes.Bgt },
        { OpCodes.Ble_S, OpCodes.Ble },
        { OpCodes.Blt_S, OpCodes.Blt },
        { OpCodes.Bne_Un_S, OpCodes.Bne_Un },
        { OpCodes.Bge_Un_S, OpCodes.Bge_Un },
        { OpCodes.Bgt_Un_S, OpCodes.Bgt_Un },
        { OpCodes.Ble_Un_S, OpCodes.Ble_Un },
        { OpCodes.Br_S, OpCodes.Br },
        { OpCodes.Blt_Un_S, OpCodes.Blt_Un },
    };

    /// <summary>
    /// Expands short-form branch instructions to long-form to avoid range issues.
    /// This compensates for skipping Harmony's later <c>CleanupCodes</c> phase which
    /// normally performs this expansion.
    /// </summary>
    internal static List<CodeInstruction> ExpandShortBranches(List<CodeInstruction> instructions)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            if (ShortToLongBranch.TryGetValue(instructions[i].opcode, out var longForm))
                instructions[i].opcode = longForm;
        }
        return instructions;
    }

    /// <summary>
    /// Emits <see cref="CodeInstruction"/>s through Harmony's <see cref="Emitter"/> using typed
    /// <c>Emit</c> overloads that route through the <c>CecilILGenerator</c>.
    ///
    /// <para>
    /// We prefer explicit typed operand handling over <c>DynEmit</c> because <c>DynEmit</c>
    /// routes through the raw <c>ILGenerator</c> wrapper and produces invalid branch targets
    /// in the Cecil output. The <c>DynEmit</c> fallback is retained only for operand categories
    /// not covered by the explicit checks; it is the most likely source of silent bad output if
    /// an unexpected operand type appears.
    /// </para>
    /// </summary>
    internal static void EmitCodesToCecil(Emitter emitter, List<CodeInstruction> codeInstructions)
    {
        foreach (var ci in codeInstructions)
        {
            foreach (var label in ci.labels)
                emitter.MarkLabel(label);

            foreach (var block in ci.blocks)
                emitter.MarkBlockBefore(block, out _);

            var code = ci.opcode;
            var operand = ci.operand;

            if (code.OperandType == OperandType.InlineNone)
            {
                emitter.Emit(code);
            }
            else if (operand is Label lbl)
                emitter.Emit(code, lbl);
            else if (operand is Label[] lbls)
                emitter.Emit(code, lbls);
            else if (operand is LocalBuilder loc)
                emitter.Emit(code, loc);
            else if (operand is FieldInfo fi)
                emitter.Emit(code, fi);
            else if (operand is MethodInfo mi)
                emitter.Emit(code, mi);
            else if (operand is ConstructorInfo ci2)
                emitter.Emit(code, ci2);
            else if (operand is Type t)
                emitter.Emit(code, t);
            else if (operand is string s)
                emitter.Emit(code, s);
            else if (operand is int i)
                emitter.Emit(code, i);
            else if (operand is long l)
                emitter.Emit(code, l);
            else if (operand is float f)
                emitter.Emit(code, f);
            else if (operand is double d)
                emitter.Emit(code, d);
            else if (operand is byte b)
                emitter.Emit(code, b);
            else if (operand is sbyte sb)
                emitter.Emit(code, sb);
            else if (operand is short sh)
                emitter.Emit(code, sh);
            else if (operand is SignatureHelper sig)
                emitter.Emit(code, sig);
            else if (operand != null)
                emitter.DynEmit(code, operand); // fallback for anything exotic
            else
                emitter.Emit(code); // null operand, treat as InlineNone

            foreach (var block in ci.blocks)
                emitter.MarkBlockAfter(block);
        }
    }

    /// <summary>
    /// Clones a <see cref="DynamicMethodDefinition"/>'s Cecil method body into the given
    /// <paramref name="typeDef"/>, relinking all type/member references to the target module.
    /// Adapted from MonoMod's <c>DMDCecilGenerator</c>, modified to write to an in-memory
    /// module instead of loading the assembly.
    /// </summary>
    internal static void GenerateCecilMethod(DynamicMethodDefinition dmd, TypeDefinition typeDef)
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

        foreach (Mono.Cecil.Cil.VariableDefinition var in clone.Body.Variables)
            var.VariableType = var.VariableType.Relink(relinker, clone);

        foreach (Mono.Cecil.Cil.ExceptionHandler handler in clone.Body.ExceptionHandlers)
            if (handler.CatchType != null)
                handler.CatchType = handler.CatchType.Relink(relinker, clone);

        for (int instri = 0; instri < body.Instructions.Count(); instri++)
        {
            Mono.Cecil.Cil.Instruction instr = body.Instructions.ElementAt(instri);
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

    private static readonly MethodInfo IdentityTranspiler = AccessTools.Method(typeof(HarmonyCecilAdapter), nameof(Transpiler));

    /// <summary>
    /// Identity transpiler used when no user transpiler is provided.
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        return insts;
    }
}
