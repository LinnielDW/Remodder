using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Impl;
using JetBrains.Rd.Tasks;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.Util;
using JetBrains.Util.Logging;
using Remodder.Common;
using ReSharperPlugin.RdProtocol;

namespace ReSharperPlugin.Remodder.Rider;

//TODO: remove extensive logging
[SolutionComponent(Instantiation.ContainerAsyncAnyThread)]
public class RemodderComponent
{
    private static readonly ILogger Log = Logger.GetLogger<RemodderComponent>();

    /// <summary>
    /// Assembly name substrings that should never be loaded as project references.
    /// These are runtime/framework assemblies that either come from the host or would
    /// conflict with Harmony's own copies.
    /// </summary>
    private static readonly string[] SkippedAssemblyPrefixes =
    [
        "System", "mscorlib", "Win32", "netstandard", "Microsoft", "Harmony"
    ];

    public RemodderComponent(ISolution solution)
    {
        Log.Info("RemodderComponent initialized");
        var model = solution.GetProtocolSolution().GetRemodderProtocolModel();
        model.Decompile.SetAsync((_, args) => Task.Run(() => HandleDecompileAsync(solution, args)));
    }

    private static async Task<string[]> HandleDecompileAsync(ISolution solution, string[] args)
    {
        var filePath = args[0];
        var typeName = args[1];
        var userAsms = args.Skip(2).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        Log.Info($"Decompile called: typeName={typeName}, filePath={filePath}");

        var alc = new AssemblyLoadContext("Remodder ALC", true);

        try
        {
            foreach (var userAsm in userAsms)
            {
                if (!File.Exists(userAsm))
                {
                    Log.Warn($"User assembly not found: '{userAsm}', skipping");
                    continue;
                }
                alc.LoadFromStream(PathToStream(userAsm));
            }

            IProject? project = null;
            string? projectAssemblyPath = null;
            string[] referenceAssemblyFiles = [];

            await solution.Locks.StartReadActionAsync(solution.GetLifetime(), () =>
            {
                project = solution
                    .FindProjectItemsByLocation(VirtualFileSystemPath.Parse(filePath, InteractionContext.Local))
                    .FirstOrDefault()?.GetProject();

                if (project == null) return;

                var ignoreNames = userAsms.Select(p => AssemblyName.GetAssemblyName(p).Name!).ToArray();
                referenceAssemblyFiles = CollectReferenceAssemblyFiles(solution, project, ignoreNames);
                projectAssemblyPath = project.GetOutputFilePath(project.TargetFrameworkIds[0]).FullPath;
            });

            if (project == null)
                return ["ERROR: Can't find the file's project"];

            foreach (var refFile in referenceAssemblyFiles)
            {
                if (!File.Exists(refFile))
                {
                    Log.Warn($"Reference assembly file not found: '{refFile}', skipping");
                    continue;
                }
                var assemblyBytes = AssemblyHelper.RemoveReferenceAssemblyAttribute(File.ReadAllBytes(refFile));
                alc.LoadFromStream(new MemoryStream(assemblyBytes));
            }

            var projectAssembly = projectAssemblyPath!;
            var loadedAssembly = alc.LoadFromStream(PathToStream(projectAssembly));
            var transpilerInfo = Decompiler.GetTranspilerInfo(loadedAssembly, typeName);

            if (transpilerInfo == null)
                return ["ERROR: No transpiler found. Ensure the class contains a Harmony transpiler method."];

            if (transpilerInfo.OriginalMethod == null)
                return [$"ERROR: Found transpiler method '{transpilerInfo.TranspilerMethod.Name}' but could not determine the target method. " +
                        $"Add [HarmonyPatch(typeof(TargetType), \"MethodName\")] to the class to specify the target."];

            var allAsmPaths = userAsms.Concat(referenceAssemblyFiles).Append(projectAssembly).ToArray();
            var asyncAssemblyPath = PrepareAsyncPatch(transpilerInfo, allAsmPaths);

            var result = DecompileOrigAndTranspiled(transpilerInfo.OriginalMethod, transpilerInfo.TranspilerMethod, allAsmPaths, transpilerInfo.AsyncOuterMethod, asyncAssemblyPath);
            Log.Info($"Decompile succeeded for {typeName}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Decompile failed for {typeName}");
            throw;
        }
        finally
        {
            alc.Unload();
            Log.Info("ALC unloaded");
        }
    }

    private static string[] CollectReferenceAssemblyFiles(ISolution solution, IProject project, string[] asmNamesToIgnore)
    {
        var files = new List<string>();
        foreach (var r in project.GetModuleReferences(project.TargetFrameworkIds[0]))
        {
            if (SkippedAssemblyPrefixes.Any(prefix => r.Name.Contains(prefix)) ||
                asmNamesToIgnore.Contains(r.Name))
                continue;

            if (r is ProjectToAssemblyReference assemblyReference)
            {
                var assemblyFile = assemblyReference.ReferenceTarget.HintLocation?.FullPath;
                if (!string.IsNullOrWhiteSpace(assemblyFile))
                    files.Add(assemblyFile);
                else
                    Log.Warn($"Assembly hint location not found: '{assemblyFile}', skipping");
            }
            else if (r is GuidProjectReference guidProjectReference)
            {
                var referencedProject = solution.GetProjectByGuid(guidProjectReference.ReferencedProjectGuid);
                if (referencedProject != null)
                {
                    var refPath = referencedProject.GetOutputFilePath(referencedProject.TargetFrameworkIds[0]).FullPath;
                    if (!string.IsNullOrWhiteSpace(refPath))
                        files.Add(refPath);
                    else
                        Log.Warn($"Referenced project output not found: '{refPath}', skipping");
                }
            }
        }
        return files.ToArray();
    }

    /// <summary>
    /// For async patches: injects the state machine type into the transpiler's static field
    /// and resolves the assembly path containing the outer method's declaring type.
    /// Returns the resolved async assembly path, or null for non-async patches.
    /// </summary>
    private static string? PrepareAsyncPatch(TranspilerInfo info, string[] allAsmPaths)
    {
        if (!info.IsAsyncMoveNext) return null;

        // Inject state machine type into the transpiler's static field
        var stateMachineType = info.OriginalMethod!.DeclaringType;
        var smField = info.TranspilerMethod.DeclaringType?
            .GetField("stateMachineType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (smField != null && stateMachineType != null)
        {
            smField.SetValue(null, stateMachineType);
            Log.Info($"Set stateMachineType to {stateMachineType.FullName}");
        }

        if (info.AsyncOuterMethod == null) return null;
        var targetAsmName = info.AsyncOuterMethod.DeclaringType?.Assembly.GetName().Name;
        if (targetAsmName == null) return null;

        var asyncAssemblyPath = allAsmPaths.FirstOrDefault(path =>
        {
            try { return File.Exists(path) && AssemblyName.GetAssemblyName(path).Name == targetAsmName; }
            catch { return false; }
        });
        Log.Info($"Async assembly path for {targetAsmName}: {asyncAssemblyPath ?? "<not found>"}");
        return asyncAssemblyPath;
    }

    private static string[] DecompileOrigAndTranspiled(MethodBase originalMethod, MethodInfo transpilerMethod, string[] userAsms, MethodBase? asyncOuterMethod = null, string? asyncAssemblyPath = null)
    {
        var origDecomp = Decompiler.Decompile(originalMethod, null, userAsms, null, asyncOuterMethod, asyncAssemblyPath);
        var decomp = Decompiler.Decompile(originalMethod, transpilerMethod, userAsms, null, asyncOuterMethod, asyncAssemblyPath);
        return [origDecomp, decomp];
    }

    /// <summary>Reads a file into a <see cref="MemoryStream"/> so the file handle is released immediately.</summary>
    private static MemoryStream PathToStream(string path) => new(File.ReadAllBytes(path));
}