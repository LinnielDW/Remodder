# Remodder (Arquebus Fork)

> **This is a fork of [Remodder by Zetrith](https://github.com/Zetrith/Remodder)** extended with additional capabilities for modders targeting modern .NET and Harmony 2.4+.
> Original plugin: [![Rider](https://img.shields.io/jetbrains/plugin/v/24343.svg?label=Rider&colorB=0A7BBB&style=for-the-badge&logo=rider)](https://plugins.jetbrains.com/plugin/24343)

WIP Rider plugin providing quality-of-life tools for modders of .NET games,
especially those using the [Harmony](https://github.com/pardeike/Harmony) runtime detour library.

Requires **JetBrains Rider 2026.1** or newer.

## What's new in this fork

| Feature | Description                                                                                                                                                                                                                                        |
|---|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Expanded patch discovery** | Three-tier transpiler resolution: Harmony attribute-based, transpiler-signature fallback, and IL-parsing of manual `Patch()` methods for `AccessTools.Method` calls; meaning far more patch styles are detected automatically.                     |
| **Harmony 2.4 support** | Updated to Harmony `2.4.2`, supporting the latest Harmony patch APIs.                                                                                                                                                                              |
| **.NET 9+ support** | The backend now targets `net9.0`, matching the runtime used by Rider 2026.1 and eliminating compatibility issues on modern .NET game projects.                                                                                                     |
| **Async method visualization** | Transpilers targeting `async` methods are now fully supported. The plugin detects `PatchAsyncMoveNext`-style patches, resolves the compiler-generated state machine, and decompiles the readable outer `async` method rather than raw MoveNext IL. |

## Features
- [Transpiler Preview](#transpiler-preview)

### Planned features
- Dropdowns for looking up and selecting reflection members in Harmony APIs (e.g. selecting the target of a `[HarmonyPatch]`)
- Deprioritizing publicized members in code completion suggestions
- Open to suggestions, please leave them in Issues

### Transpiler Preview
A tool for previewing the effects of Harmony transpilers right in the IDE.
The preview is a window showing the diff between the decompiled original method 
and the original after applying the transpiler under the mouse cursor.

Example transpiler:
```cs
[HarmonyPatch(typeof(ExampleClass), nameof(OriginalMethod))]
public static class ExampleClass
{
    public static int OriginalMethod(string s)
    {
        return 90 + s.Length;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
    {
        yield return new CodeInstruction(OpCodes.Ldstr, "Hello World");
        yield return new CodeInstruction(
            OpCodes.Call, 
            AccessTools.Method(typeof(Console), nameof(Console.WriteLine), [typeof(string)])
        );
        
        foreach (var inst in insts)
        {
            if (inst.OperandIs(90))
                inst.operand = 42;

            yield return inst;
        }
    }
}
```

Resulting diff:

<img src="https://github.com/Zetrith/Remodder/blob/main/TranspilerPreviewScreenhot.png?raw=true" width="800"  alt="Transpiler Preview diff screenshot"/>

It works by querying the IDE for relevant referenced assemblies, 
temporarily loading them together with the project's compiled assembly 
and running the transpiler under the cursor in this context.

*User assemblies* is a list of assemblies to load which take precedence during transpiler execution and decompiling.
If you are compiling your project against reference assemblies (without method bodies),
you can use it to provide the original game assemblies to Remodder.

When you request a preview, the plugin executes the code of the project you are working on locally.
**Keep the possible security problems in mind when interacting with projects you don't trust.**

Current limitations:
- Only works on Harmony's class-with-attributes patches and manual `Patch()` method patterns
- `TargetMethods` returning multiple targets is not supported

Good to know:
- Rebuild the project and refresh the preview to see changes in transpiler code
- The executed transpiler runs on the same runtime as the IDE (.NET 9 on Rider 2026.1)

The feature is based on Zetrith's past [TranspilerExplorer](https://github.com/Zetrith/TranspilerExplorer) project.

## Visual Studio support
There are no plans for a Visual Studio version at this time.

Rider is free for non-commercial use as of 2024. See [JetBrains' licensing page](https://www.jetbrains.com/rider/buy/) for details.
Free versions of the paid license are also available for [students](https://www.jetbrains.com/community/education/#students) and [open-source contributors](https://www.jetbrains.com/community/opensource/?var=1).

## Contact & Support
This fork is maintained by **Arquebus**.  
For issues specific to this fork, open an issue in this repository.  
For the original plugin, see [Zetrith/Remodder](https://github.com/Zetrith/Remodder) and support Zetrith on [Patreon](https://www.patreon.com/zetrith).
