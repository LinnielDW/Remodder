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
        WriteAssembly(stream, orig, transpiler, null, null);
    }

    /// <summary>
    /// When <paramref name="asyncOuterMethod"/> is provided, reads the original assembly with Cecil,
    /// copies the full declaring type (with all nested types including the state machine), and replaces
    /// the MoveNext body with the transpiled version. This lets the decompiler reconstruct async/await.
    /// </summary>
    public static void WriteAssembly(Stream stream, MethodBase orig, MethodInfo? transpiler, MethodBase? asyncOuterMethod, string? asyncAssemblyPath, string[]? allAssemblyPaths = null)
    {
        // Build the transpiled MoveNext body via the normal DynamicMethod path
        var dynamicMethod = MethodPatcherTools.CreateDynamicMethod(orig, "", false);
        var il = dynamicMethod.GetILGenerator();

        var originalVariables = MethodPatcherTools.DeclareOriginalLocalVariables(il, orig);

        var copier = new MethodCopier(orig, il, originalVariables);
        var emitter = new Emitter(il);

        copier.AddTranspiler(transpiler ?? IdentityTranspiler);

        var endLabels = new List<Label>();
        var codeInstructions = copier.Finalize(false, out var hasReturnCode, out _, endLabels);

        codeInstructions = ExpandShortBranches(codeInstructions);
        EmitCodesToCecil(emitter, codeInstructions);

        foreach (var label in endLabels)
            emitter.MarkLabel(label);

        if (hasReturnCode)
            emitter.Emit(OpCodes.Ret);

        // If we have the async outer method, build a full-type assembly from the original
        if (asyncOuterMethod != null && asyncAssemblyPath != null)
        {
            WriteAsyncAssembly(stream, orig, dynamicMethod, asyncOuterMethod, asyncAssemblyPath, allAssemblyPaths);
            return;
        }

        // Non-async path: synthetic single-method assembly
        string name = dynamicMethod.GetDumpName("Cecil");
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

        GenerateCecilMethod(dynamicMethod, typeDef);

        // Create stub nested types for any display classes / lambdas referenced
        // from the method body, so the decompiler can resolve them.
        CreateReferencedNestedTypeStubs(typeDef);

        module.Write(stream);
    }

    /// <summary>
    /// Reads the original assembly, finds the state machine's MoveNext method, replaces its body
    /// with the transpiled version, and writes the whole module to the output stream.
    /// By modifying in-place (no cross-module cloning), we avoid Cecil import issues.
    /// </summary>
    private static void WriteAsyncAssembly(Stream stream, MethodBase moveNextMethod, DynamicMethodDefinition transpiledPatch, MethodBase asyncOuterMethod, string assemblyPath, string[]? allAssemblyPaths)
    {
        var outerType = asyncOuterMethod.DeclaringType!;

        var resolver = new DefaultAssemblyResolver();
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(assemblyDir))
            resolver.AddSearchDirectory(assemblyDir);

        if (allAssemblyPaths != null)
        {
            var addedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (assemblyDir != null) addedDirs.Add(assemblyDir);
            foreach (var path in allAssemblyPaths)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && addedDirs.Add(dir))
                    resolver.AddSearchDirectory(dir);
            }
        }

        using var origModule = ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            AssemblyResolver = resolver
        });

        // Find the declaring type in the original module
        var origTypeDef = origModule.Types.FirstOrDefault(t => t.FullName == outerType.FullName);
        if (origTypeDef == null)
            throw new InvalidOperationException($"Type {outerType.FullName} not found in assembly");

        // Find the state machine type and its MoveNext method
        var stateMachineTypeName = moveNextMethod.DeclaringType!.Name;
        var stateMachineType = origTypeDef.NestedTypes.FirstOrDefault(t => t.Name == stateMachineTypeName);
        if (stateMachineType == null)
            throw new InvalidOperationException($"State machine type {stateMachineTypeName} not found");

        var origMoveNext = stateMachineType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (origMoveNext == null)
            throw new InvalidOperationException("MoveNext not found in state machine");

        // Get the transpiled MoveNext body from the DynamicMethodDefinition
        var transpiledDef = transpiledPatch.Definition;

        // Clone the transpiled body onto the original MoveNext method
        var newBody = transpiledDef.Body.Clone(origMoveNext);

        // Relink references in the new body to the original module
        foreach (var variable in newBody.Variables)
        {
            try { variable.VariableType = origModule.ImportReference(variable.VariableType); }
            catch { }
        }

        foreach (var handler in newBody.ExceptionHandlers)
            if (handler.CatchType != null)
            {
                try { handler.CatchType = origModule.ImportReference(handler.CatchType); }
                catch { }
            }

        for (int i = 0; i < newBody.Instructions.Count; i++)
        {
            var instr = newBody.Instructions[i];
            var operand = instr.Operand;

            try
            {
                if (operand is FieldReference fieldRef)
                    instr.Operand = origModule.ImportReference(fieldRef);
                else if (operand is MethodReference methodRef)
                    instr.Operand = origModule.ImportReference(methodRef);
                else if (operand is TypeReference typeRef && typeRef is not GenericParameter)
                    instr.Operand = origModule.ImportReference(typeRef);
            }
            catch { }
        }

        origMoveNext.Body = newBody;

        // Write the entire original module (with only MoveNext replaced)
        origModule.Write(stream);
    }

    // NOTE: CloneTypeWithNestedTypes and RelinkMethodBody were removed — the async path
    // now mutates the original module in-place (WriteAsyncAssembly), making deep-clone unnecessary.

    /// <summary>
    /// Scans the emitted method body for references to methods whose declaring types
    /// are not defined in the current module. For each such type, creates a stub type
    /// with stub methods so that the decompiler (with AnonymousMethods enabled) can
    /// resolve delegate targets without crashing.
    /// </summary>
    private static void CreateReferencedNestedTypeStubs(TypeDefinition parentType)
    {
        var module = parentType.Module;
        var method = parentType.Methods.FirstOrDefault();
        if (method?.Body == null) return;

        // Collect all referenced method/type pairs not defined in this module
        var referencedMethods = new Dictionary<string, List<MethodReference>>();

        foreach (var instr in method.Body.Instructions)
        {
            if (instr.Operand is MethodReference methodRef)
            {
                var declType = methodRef.DeclaringType;
                if (declType == null) continue;

                // Skip types already in the module, and skip the parent type itself
                var fullName = declType.FullName;
                if (fullName == parentType.FullName) continue;

                // Only stub compiler-generated types (display classes, state machines, etc.)
                // These typically contain '<' or start with '<>'
                var typeName = declType.Name;
                if (!typeName.Contains('<') && !typeName.Contains('>') && !typeName.StartsWith("$")) continue;

                if (!referencedMethods.ContainsKey(fullName))
                    referencedMethods[fullName] = new List<MethodReference>();
                referencedMethods[fullName].Add(methodRef);
            }
        }

        foreach (var (typeFullName, methods) in referencedMethods)
        {
            // Check if a type with this name already exists
            var sample = methods[0].DeclaringType;
            var stubTypeName = sample.Name;

            if (module.Types.Any(t => t.FullName == typeFullName))
                continue;
            if (parentType.NestedTypes.Any(t => t.Name == stubTypeName))
                continue;

            var stubType = new TypeDefinition(
                sample.Namespace,
                stubTypeName,
                Mono.Cecil.TypeAttributes.NestedPrivate | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.Class
            )
            {
                BaseType = module.TypeSystem.Object
            };

            // Add stub fields referenced from method body
            var referencedFields = new HashSet<string>();
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is FieldReference fieldRef &&
                    fieldRef.DeclaringType?.FullName == typeFullName &&
                    referencedFields.Add(fieldRef.Name))
                {
                    var fieldType = module.ImportReference(fieldRef.FieldType);
                    stubType.Fields.Add(new FieldDefinition(fieldRef.Name, Mono.Cecil.FieldAttributes.Public, fieldType));
                }
            }

            // Add stub methods
            var addedMethods = new HashSet<string>();
            foreach (var methodRef in methods)
            {
                var sig = methodRef.FullName;
                if (!addedMethods.Add(sig)) continue;

                var stubMethod = new MethodDefinition(
                    methodRef.Name,
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                    module.ImportReference(methodRef.ReturnType)
                );

                if (methodRef.Name == ".ctor")
                {
                    stubMethod.Attributes = Mono.Cecil.MethodAttributes.Public |
                                            Mono.Cecil.MethodAttributes.HideBySig |
                                            Mono.Cecil.MethodAttributes.SpecialName |
                                            Mono.Cecil.MethodAttributes.RTSpecialName;
                }

                foreach (var param in methodRef.Parameters)
                {
                    stubMethod.Parameters.Add(new ParameterDefinition(
                        param.Name,
                        param.Attributes,
                        module.ImportReference(param.ParameterType)
                    ));
                }

                // Minimal body: just throw or return default
                stubMethod.Body = new Mono.Cecil.Cil.MethodBody(stubMethod);
                var ilProc = stubMethod.Body.GetILProcessor();
                if (methodRef.ReturnType.FullName == "System.Void")
                {
                    ilProc.Append(ilProc.Create(Mono.Cecil.Cil.OpCodes.Ret));
                }
                else
                {
                    ilProc.Append(ilProc.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
                    ilProc.Append(ilProc.Create(Mono.Cecil.Cil.OpCodes.Throw));
                }

                stubType.Methods.Add(stubMethod);
            }

            parentType.NestedTypes.Add(stubType);

            // Now relink instructions to point to the stub definitions instead of external references
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is MethodReference methodRef &&
                    methodRef.DeclaringType?.FullName == typeFullName)
                {
                    var stubMethod = stubType.Methods.FirstOrDefault(m => m.FullName == methodRef.FullName);
                    if (stubMethod != null)
                        instr.Operand = stubMethod;
                }
                else if (instr.Operand is FieldReference fieldRef &&
                         fieldRef.DeclaringType?.FullName == typeFullName)
                {
                    var stubField = stubType.Fields.FirstOrDefault(f => f.Name == fieldRef.Name);
                    if (stubField != null)
                        instr.Operand = stubField;
                }
            }
        }
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
