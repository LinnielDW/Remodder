pluginManagement {
    repositories {
        maven { setUrl("https://cache-redirector.jetbrains.com/plugins.gradle.org") }
        maven { setUrl("https://cache-redirector.jetbrains.com/maven-central") }
        maven { setUrl("https://cache-redirector.jetbrains.com/dl.bintray.com/kotlin/kotlin-eap") }
    }
    resolutionStrategy {
        eachPlugin {
            if (requested.id.id == "com.jetbrains.rdgen") {
                useModule("com.jetbrains.rd:rd-gen:${requested.version}")
            }
        }
    }
}

rootProject.name = "ReSharperPlugin.Remodder"

include(":protocol")

