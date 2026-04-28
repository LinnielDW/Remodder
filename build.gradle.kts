import com.jetbrains.plugin.structure.base.utils.isFile
import org.jetbrains.intellij.platform.gradle.Constants

plugins {
    id("java")
    alias(libs.plugins.kotlinJvm)
    id("org.jetbrains.intellij.platform") version "2.15.0"
    id("me.filippov.gradle.jvm.wrapper") version "0.15.0"
}

val DotnetSolution: String by project
val BuildConfiguration: String by project
val ProductVersion: String by project
val DotnetPluginId: String by project
val RiderPluginId: String by project
val PublishToken: String by project

allprojects {
    repositories {
        maven { setUrl("https://cache-redirector.jetbrains.com/maven-central") }
    }
}

repositories {
    intellijPlatform {
        defaultRepositories()
        jetbrainsRuntime()
    }
}

version = extra["PluginVersion"] as String

tasks.processResources {
    from("dependencies.json") { into("META-INF") }
}

sourceSets {
    main {
        java.srcDir("src/rider/main/java")
        kotlin.srcDir("src/rider/main/kotlin")
        resources.srcDir("src/rider/main/resources")
    }
}

kotlin {
    jvmToolchain(21)
}

var buildToolExecutable: String? = null
var buildToolArgs: List<String>? = null

val setBuildTool by tasks.registering {
    doLast {
        // Always use dotnet msbuild to ensure .NET 9 SDK support
        buildToolExecutable = "dotnet"
        buildToolArgs = mutableListOf(
            "msbuild",
            DotnetSolution,
            "/p:Configuration=${BuildConfiguration}",
            "/p:HostFullIdentifier="
        )
    }
}

val compileDotNet by tasks.registering {
    dependsOn(setBuildTool)
    doLast {
        val arguments = buildToolArgs!!.toMutableList()
        arguments.add("/t:Restore;Rebuild")
        providers.exec {
            executable(buildToolExecutable!!)
            args(arguments)
            workingDir(rootDir)
        }.result.get()
    }
}

val testDotNet by tasks.registering {
    doLast {
        providers.exec {
            executable("dotnet")
            args("test", "${DotnetSolution}", "--logger", "GitHubActions")
            workingDir(rootDir)
        }.result.get()
    }
}

tasks.buildPlugin {
    doLast {
        copy {
            from(layout.buildDirectory.dir("distributions/${rootProject.name}-${version}.zip"))
            into("${rootDir}/output")
        }
        val changelogText = file("${rootDir}/CHANGELOG.md").readText()
        val changelogMatches = Regex("(?s)(-.+?)(?=##|$)").findAll(changelogText)
        val changeNotes = changelogMatches.map {
            it.groups[1]!!.value.replace("(?s)- ".toRegex(), "\u2022 ").replace("`", "").replace(",", "%2C").replace(";", "%3B")
        }.take(1).joinToString()
        val arguments = buildToolArgs!!.toMutableList()
        arguments.add("/t:Pack")
        arguments.add("/p:PackageOutputPath=${rootDir}/output")
        arguments.add("/p:PackageReleaseNotes=${changeNotes}")
        arguments.add("/p:PackageVersion=${version}")
        providers.exec {
            executable(buildToolExecutable!!)
            args(arguments)
            workingDir(rootDir)
        }.result.get()
    }
}

dependencies {
    intellijPlatform {
        rider(ProductVersion) { useInstaller = false }
        jetbrainsRuntime()
    }
}

tasks.runIde {
    maxHeapSize = "1500m"
}

tasks.patchPluginXml {
    val changelogText = file("${rootDir}/CHANGELOG.md").readText()
    val changelogMatches = Regex("(?s)(-.+?)(?=##|\$)").findAll(changelogText)
    changeNotes.set(changelogMatches.map {
        it.groups[1]!!.value.replace("(?s)\r?\n".toRegex(), "<br />\n")
    }.take(1).joinToString())
}

tasks.prepareSandbox {
    dependsOn(compileDotNet)
    val outputFolder = "${rootDir}/src/dotnet/${DotnetPluginId}/bin/${DotnetPluginId}.Rider/${BuildConfiguration}"
    val dllFiles = listOf(
        "$outputFolder/${DotnetPluginId}.dll",
        "$outputFolder/${DotnetPluginId}.pdb",
    )
    dllFiles.forEach { f ->
        val file = file(f)
        from(file) { into("${rootProject.name}/dotnet") }
    }
    val commonOutputFolder = "${rootDir}/src/dotnet/Remodder.Common/bin/Remodder.Common/${BuildConfiguration}"
    from(commonOutputFolder) { include("*.dll"); into("${rootProject.name}/dotnet") }
    doLast {
        dllFiles.forEach { f ->
            val file = file(f)
            if (!file.exists()) throw RuntimeException("File $file does not exist")
        }
    }
}

tasks.publishPlugin {
    dependsOn(testDotNet)
    dependsOn(tasks.buildPlugin)
    token.set("${PublishToken}")
    doLast {
        providers.exec {
            executable("dotnet")
            args("nuget", "push", "output/${DotnetPluginId}.${version}.nupkg", "--api-key", "${PublishToken}", "--source", "https://plugins.jetbrains.com")
            workingDir(rootDir)
        }.result.get()
    }
}

val riderModel: Configuration by configurations.creating {
    isCanBeConsumed = true
    isCanBeResolved = false
}

artifacts {
    add(riderModel.name, provider {
        intellijPlatform.platformPath.resolve("lib/rd/rider-model.jar").also {
            check(it.isFile) {
                "rider-model.jar is not found at $it"
            }
        }
    }) {
        builtBy(Constants.Tasks.INITIALIZE_INTELLIJ_PLATFORM_PLUGIN)
    }
}




