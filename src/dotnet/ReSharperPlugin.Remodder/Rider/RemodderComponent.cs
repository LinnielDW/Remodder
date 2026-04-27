using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using HarmonyLib;
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

    public RemodderComponent(ISolution solution)
    {
        Log.Info("RemodderComponent initialized");
        var model = solution.GetProtocolSolution().GetRemodderProtocolModel();
        
        model.Decompile.SetAsync((_, args) =>
        {
            return Task.Run(async () =>
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
                    string[]? referenceAssemblyFiles = null;

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

                    foreach (var refFile in referenceAssemblyFiles!)
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
                    var a = alc.LoadFromStream(PathToStream(projectAssembly));
                    var transpiler = Decompiler.GetTranspiler(a, typeName);

                    if (transpiler == null)
                        return ["ERROR: No transpiler"];

                    var result = DecompileOrigAndTranspiled(transpiler, userAsms);
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
            });
        });
    }

    private static string[] CollectReferenceAssemblyFiles(ISolution solution, IProject project, string[] asmNamesToIgnore)
    {
        var files = new System.Collections.Generic.List<string>();
        foreach (var r in project.GetModuleReferences(project.TargetFrameworkIds[0]))
        {
            if (r.Name.Contains("System") ||
                r.Name.Contains("mscorlib") ||
                r.Name.Contains("Win32") ||
                r.Name.Contains("netstandard") ||
                r.Name.Contains("Microsoft") ||
                r.Name.Contains("Harmony") ||
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

    private static string[] DecompileOrigAndTranspiled(AttributePatch patch, string[] userAsms)
    {
        var origDecomp = Decompiler.Decompile(
            patch.info.GetOriginalMethod(),
            null,
            userAsms,
            null
        );

        var decomp = Decompiler.Decompile(
            patch.info.GetOriginalMethod(), 
            patch.info.method, 
            userAsms,
            null
        );
        
        return [origDecomp, decomp];
    }

    private static MemoryStream PathToStream(string path)
    {
        return new MemoryStream(File.ReadAllBytes(path));
    }
}