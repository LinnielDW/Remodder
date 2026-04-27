using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Remodder.Common;

/// <param name="OriginalMethod">The method to decompile. For async patches this is the state machine MoveNext.</param>
/// <param name="TranspilerMethod">The transpiler method to apply.</param>
/// <param name="IsAsyncMoveNext">True when the target was resolved via PatchAsyncMoveNext — the original is a MoveNext on a state machine.</param>
/// <param name="AsyncOuterMethod">For async patches, the original async method (e.g. CardPileCmd.Add) before state machine resolution.</param>
public record TranspilerInfo(MethodBase? OriginalMethod, MethodInfo TranspilerMethod, bool IsAsyncMoveNext = false, MethodBase? AsyncOuterMethod = null);

/// <summary>Intermediate result of resolving the original method from a Patch() IL body.</summary>
internal record OriginalMethodResolution(MethodBase? Method, bool IsAsync, MethodBase? OuterMethod);

/// <summary>
/// Resolves Harmony transpiler methods and their target original methods from a loaded assembly.
/// Supports attribute-based discovery, signature-based fallback, and IL-parsing of manual Patch methods.
/// </summary>
public static class TranspilerResolver
{
    /// <summary>
    /// Discovers the transpiler method and its target original method for the given type.
    /// Uses a three-tier strategy: (1) Harmony attribute-based, (2) signature-based fallback,
    /// (3) IL-parsing the Patch() method for AccessTools.Method calls.
    /// </summary>
    public static TranspilerInfo? Resolve(Assembly asm, string typeName)
    {
        var type = asm.GetType(typeName);
        if (type == null)
            return null;

        // 1) Try attribute-based discovery
        var patch = new Harmony("dummy")
            .CreateClassProcessor(type).patchMethods?
            .FirstOrDefault(p => p.type == HarmonyPatchType.Transpiler);

        if (patch != null)
            return new TranspilerInfo(patch.info.GetOriginalMethod(), patch.info.method);

        // 2) Fallback: find a static method with transpiler signature
        var transpilerMethod = FindTranspilerBySignature(type);
        if (transpilerMethod == null)
            return null;

        // 3) Try attributes first, then IL-parse the Patch method
        var originalMethod = ResolveOriginalMethodFromAttributes(type);
        bool isAsync = false;
        MethodBase? asyncOuterMethod = null;

        if (originalMethod == null)
        {
            var resolution = ResolveOriginalMethodFromPatchIL(type);
            originalMethod = resolution.Method;
            isAsync = resolution.IsAsync;
            asyncOuterMethod = resolution.OuterMethod;
        }

        return new TranspilerInfo(originalMethod, transpilerMethod, isAsync, asyncOuterMethod);
    }

    private static MethodInfo? FindTranspilerBySignature(Type type)
    {
        var codeInstructionEnumerable = typeof(IEnumerable<CodeInstruction>);
        // Take(2) so we can detect ambiguity without enumerating all methods
        var candidates = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m =>
            {
                var parameters = m.GetParameters();
                if (!parameters.Any(p => codeInstructionEnumerable.IsAssignableFrom(p.ParameterType)))
                    return false;
                return codeInstructionEnumerable.IsAssignableFrom(m.ReturnType);
            })
            .Take(2)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static MethodBase? ResolveOriginalMethodFromAttributes(Type type)
    {
        try
        {
            var harmonyMethodAttrs = HarmonyMethodExtensions.GetFromType(type);
            if (harmonyMethodAttrs == null || harmonyMethodAttrs.Count == 0)
                return null;

            var merged = HarmonyMethod.Merge(harmonyMethodAttrs);
            if (merged.declaringType == null || string.IsNullOrEmpty(merged.methodName))
                return null;

            return AccessTools.Method(merged.declaringType, merged.methodName, merged.argumentTypes?.ToArray());
        }
        catch
        {
            // HarmonyMethodExtensions or AccessTools can throw on malformed types;
            // fall through to IL-based resolution.
            return null;
        }
    }

    /// <summary>
    /// Scans the IL of a static "Patch" method (or similar) for calls to AccessTools.Method
    /// to determine the original target method. Also detects PatchAsyncMoveNext to resolve
    /// to the async state machine's MoveNext method.
    /// </summary>
    private static OriginalMethodResolution ResolveOriginalMethodFromPatchIL(Type type)
    {
        var none = new OriginalMethodResolution(null, false, null);
        try
        {
            var patchMethod = type.GetMethod("Patch", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (patchMethod == null)
                return none;

            var instructionsRaw = PatchProcessor.ReadMethodBody(patchMethod);
            if (instructionsRaw == null)
                return none;
            var instructions = instructionsRaw.ToList();

            bool isAsync = instructions.Any(i =>
                i.Value is MethodInfo mi && mi.Name == "PatchAsyncMoveNext");

            if (!TryExtractAccessToolsArgs(instructions, out var targetType, out var targetMethodName, out var candidateArgTypes))
                return none;

            MethodBase? resolved = null;
            try
            {
                var argTypes = candidateArgTypes.Count > 0 ? candidateArgTypes.ToArray() : null;
                resolved = AccessTools.Method(targetType, targetMethodName, argTypes);
            }
            catch
            {
                // Retry without argument types in case the type list was incorrectly inferred
                try { resolved = AccessTools.Method(targetType, targetMethodName); } catch { }
            }

            if (resolved == null)
                return none;

            if (isAsync)
            {
                var stateMachineAttr = resolved.GetCustomAttribute<AsyncStateMachineAttribute>()
                                       ?? resolved.GetCustomAttribute<IteratorStateMachineAttribute>() as StateMachineAttribute;
                if (stateMachineAttr != null)
                {
                    var moveNext = AccessTools.Method(stateMachineAttr.StateMachineType, "MoveNext");
                    if (moveNext != null)
                        return new OriginalMethodResolution(moveNext, true, resolved);
                }
            }

            return new OriginalMethodResolution(resolved, false, null);
        }
        catch
        {
            return none;
        }
    }

    /// <summary>
    /// Walks backward through the instruction list looking for the first call to
    /// <c>AccessTools.Method</c> and extracts the <c>typeof(T)</c> token and method-name
    /// string that were pushed onto the stack just before it.
    /// </summary>
    private static bool TryExtractAccessToolsArgs(
        IList<KeyValuePair<OpCode, object>> instructions,
        out Type? targetType,
        out string? targetMethodName,
        out List<Type> candidateArgTypes)
    {
        targetType = null;
        targetMethodName = null;
        candidateArgTypes = new List<Type>();

        for (int i = 0; i < instructions.Count; i++)
        {
            var opcode = instructions[i].Key;
            var operand = instructions[i].Value;

            bool isAccessToolsMethod = opcode == OpCodes.Call
                && operand is MethodInfo calledMethod
                && calledMethod.DeclaringType?.FullName == "HarmonyLib.AccessTools"
                && calledMethod.Name == "Method";

            if (!isAccessToolsMethod) continue;

            // Reset for each candidate call site
            targetType = null;
            targetMethodName = null;
            candidateArgTypes.Clear();

            for (int j = i - 1; j >= 0; j--)
            {
                var prevOp = instructions[j].Key;
                var prevOperand = instructions[j].Value;

                if (prevOp == OpCodes.Ldstr && prevOperand is string s && targetMethodName == null)
                {
                    targetMethodName = s;
                }
                else if (prevOp == OpCodes.Ldtoken)
                {
                    var resolvedType = prevOperand as Type
                                       ?? (prevOperand is RuntimeTypeHandle h ? Type.GetTypeFromHandle(h) : null);
                    if (resolvedType != null)
                    {
                        if (targetMethodName != null)
                        {
                            targetType = resolvedType;
                            break;
                        }
                        candidateArgTypes.Add(resolvedType);
                    }
                }
            }

            if (targetType != null && targetMethodName != null)
            {
                candidateArgTypes.Reverse();
                return true;
            }
        }

        return false;
    }
}

