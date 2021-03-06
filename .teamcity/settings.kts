import jetbrains.buildServer.configs.kotlin.v2019_2.*
import jetbrains.buildServer.configs.kotlin.v2019_2.buildSteps.powerShell
import jetbrains.buildServer.configs.kotlin.v2019_2.triggers.vcs
import jetbrains.buildServer.configs.kotlin.v2019_2.vcs.GitVcsRoot

/*
The settings script is an entry point for defining a TeamCity
project hierarchy. The script should contain a single call to the
project() function with a Project instance or an init function as
an argument.

VcsRoots, BuildTypes, Templates, and subprojects can be
registered inside the project using the vcsRoot(), buildType(),
template(), and subProject() methods respectively.

To debug settings scripts in command-line, run the

    mvnDebug org.jetbrains.teamcity:teamcity-configs-maven-plugin:generate

command and attach your debugger to the port 8000.

To debug in IntelliJ Idea, open the 'Maven Projects' tool window (View
-> Tool Windows -> Maven Projects), find the generate task node
(Plugins -> teamcity-configs -> teamcity-configs:generate), the
'Debug' option is available in the context menu for the task.
*/

version = "2019.2"

project {

    vcsRoot(HttpsIntegrationGitBirchComScmTpNukeFrameworkTestGit1)

    buildType(Develop)
    buildType(Master)

    params {
        param("env.Git_Branch", "${DslContext.settingsRoot.paramRefs.buildVcsBranch}")
    }
}

object Develop : BuildType({
    name = "Develop"

    vcs {
        root(DslContext.settingsRoot)
    }

    steps {
        powerShell {
            name = "Nuke Build"
            scriptMode = file {
                path = "build.ps1"
            }
            param("jetbrains_powershell_scriptArguments", "--nologo --target Octo_Create_Release")
        }
    }

    triggers {
        vcs {
            triggerRules = """-:comment=(\*{3}NO_CI\*{3}):**"""
            branchFilter = "+:refs/heads/develop"
            perCheckinTriggering = true
            groupCheckinsByCommitter = true
            enableQueueOptimization = false
        }
    }
})

object Master : BuildType({
    name = "Master"

    vcs {
        root(HttpsIntegrationGitBirchComScmTpNukeFrameworkTestGit1)
    }

    steps {
        powerShell {
            name = "Nuke Build"
            scriptMode = file {
                path = "build.ps1"
            }
            param("jetbrains_powershell_scriptArguments", "--nologo --target Octo_Create_Release")
        }
    }

    triggers {
        vcs {
            enabled = false
            triggerRules = """-:comment=(\*{3}NO_CI\*{3}):**"""
            branchFilter = "+:refs/heads/develop"
            perCheckinTriggering = true
            groupCheckinsByCommitter = true
            enableQueueOptimization = false
        }
    }
})

object HttpsIntegrationGitBirchComScmTpNukeFrameworkTestGit1 : GitVcsRoot({
    name = "https://Integration@git.birch.com/scm/tp/nuke.framework.test.git (1)"
    url = "https://Integration@git.birch.com/scm/tp/nuke.framework.test.git"
    branch = "refs/heads/develop"
    branchSpec = """
        +:refs/heads/(release/*)
        +:refs/heads/(master/*)
    """.trimIndent()
    authMethod = password {
        userName = "integration"
        password = "credentialsJSON:20e3f2a9-ceed-4aca-8f27-c7138f886047"
    }
})
