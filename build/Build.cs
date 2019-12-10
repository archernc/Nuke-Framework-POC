using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.TeamCity;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.VSTest;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.CI.TeamCity.TeamCity;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.Tools.VSTest.VSTestTasks;
using Nuke.Common.CI;
using Nuke.Common.Tools.DotCover;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[TeamCitySetDotCoverHomePath]
[TeamCity(
	TeamCityAgentPlatform.Windows,
	DefaultBranch = "Develop",
	//VcsTriggeredTargets = new[] { nameof(Pack), nameof(Test) },
	//NightlyTriggeredTargets = new[] { nameof(Pack), nameof(Test) },
	//ManuallyTriggeredTargets = new[] { nameof(Publish) },
	NonEntryTargets = new[] { nameof(Restore) },
	ExcludedTargets = new[] { nameof(Clean) })]
class Build : NukeBuild
{
	/// Support plugins are available for:
	///   - JetBrains ReSharper        https://nuke.build/resharper
	///   - JetBrains Rider            https://nuke.build/rider
	///   - Microsoft VisualStudio     https://nuke.build/visualstudio
	///   - Microsoft VSCode           https://nuke.build/vscode

	public static int Main() => Execute<Build>(x => x.Test);

	[Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
	readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

	[Solution] readonly Solution Solution;
	[GitRepository] readonly GitRepository GitRepository;
	[GitVersion] readonly GitVersion GitVersion;

	AbsolutePath SourceDirectory => RootDirectory / "src";
	AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
	AbsolutePath NuGetOutputDirectory => ArtifactsDirectory / "NuGet";
	AbsolutePath OctopusOutputDirectory => ArtifactsDirectory / "Octo";


	readonly string NUGET_SERVER_KEY = Environment.GetEnvironmentVariable("NUGET_SERVER_KEY");
	readonly string NUGET_SERVER_URL = Environment.GetEnvironmentVariable("NUGET_SERVER_URL");
	readonly string OCTOPACK_PUBLISH_APIKEY = Environment.GetEnvironmentVariable("OCTOPACK_PUBLISH_APIKEY");
	readonly string OCTOPUS_DEPLOY_SERVER = Environment.GetEnvironmentVariable("OCTOPUS_DEPLOY_SERVER"); //TODO: convert to SSL
	readonly string OCTOPUS_PROJECT_NAME = Environment.GetEnvironmentVariable("OCTOPUS_PROJECT_NAME");// ?? "Nuke.Core";

	/// <summary>
	/// Removes previously built artifacts
	/// </summary>
	Target Clean => _ => _
		.Before(Restore)
		.Executes(() =>
		{
			SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
			EnsureCleanDirectory(ArtifactsDirectory);
		});

	/// <summary>
	/// This will restore all paket references
	/// </summary>
	Target Restore => _ => _
		.Executes(() =>
		{
			MSBuild(_ => _
				.SetTargetPath(Solution)
				.SetTargets("Restore"));
		});

	/// <summary>
	/// Runs JetBrains.ReSharper code analysis
	/// </summary>
	Target Analysis => _ => _
	.DependsOn(Restore)
	.Executes(() =>
	{
		InspectCode(_ => _
			.SetTargetPath(Solution)
			.SetOutput($"{ArtifactsDirectory}/inspectCode.xml")
			//.AddExtensions(
			//    "EtherealCode.ReSpeller",
			//    "PowerToys.CyclomaticComplexity",
			//    "ReSharper.ImplicitNullability",
			//    "ReSharper.SerializationInspections",
			//    "ReSharper.XmlDocInspections")
			);
	});

	/// <summary>
	/// Build
	/// </summary>
	Target Compile => _ => _
		.DependsOn(Restore)
		.Executes(() =>
		{
			MSBuild(_ => _
				.SetTargetPath(Solution)
				.SetTargets("Rebuild")
				.SetConfiguration(Configuration)
				.SetAssemblyVersion(GitVersion.AssemblySemVer)
				.SetFileVersion(GitVersion.AssemblySemFileVer)
				.SetInformationalVersion(GitVersion.InformationalVersion)
				.SetMaxCpuCount(Environment.ProcessorCount)
				.SetNodeReuse(IsLocalBuild));
		});

	// http://www.nuke.build/docs/authoring-builds/ci-integration.html#partitioning
	[Partition(2)] readonly Partition TestPartition;
	/// <summary>
	/// Runs all tests and generates report file(s)
	/// </summary>
	Target Test => _ => _
	//.DependsOn(Compile)
	.Produces($"{ArtifactsDirectory}/*.trx")
	.Produces($"{ArtifactsDirectory}/*.xml")
	.Partition(() => TestPartition)
	.Executes(() =>
	{

		var projects = TestPartition.GetCurrent(Solution.GetProjects("*.*Test"));
		if (projects.Any())
		{
			VSTest(_ => _
			//.EnableEnableCodeCoverage()
			.SetEnableCodeCoverage(IsServerBuild)
			.CombineWith(projects,
				(_, v) => _
				.SetTestAssemblies((v.Directory / "bin" / Configuration).GlobFiles("*.*Test.dll").Select(a => a.ToString()))
				.SetLogger(Host == HostType.TeamCity ? "TeamCity" : $"trx;LogFileName={v.Name}.trx")
			));
		}

		//DotNetTest(_ => _
		//	.SetConfiguration(Configuration)
		//	.SetNoBuild(InvokedTargets.Contains(Compile))
		//	.ResetVerbosity()
		//	.SetResultsDirectory(ArtifactsDirectory)
		//	.When(InvokedTargets.Contains(Coverage) || IsServerBuild,
		//		_ => _
		//		.SetProperty("CollectCoverage", propertyValue: true)
		//		// CoverletOutputFormat: json (default), lcov, opencover, cobertura, teamcity
		//		.SetProperty("CoverletOutputFormat", "teamcity%2copencover")
		//		//.EnableCollectCoverage()
		//		//.SetCoverletOutputFormat(CoverletOutputFormat.teamcity)
		//		.When(IsServerBuild, _ => _
		//			.SetProperty("UseSourceLink", propertyValue: true)
		//			//.EnableUseSourceLink()
		//			)
		//		)
		//	.CombineWith(TestPartition.GetCurrent(Solution.GetProjects("*.UnitTest")), (_, v) => _
		//		.SetProjectFile(v)
		//		.SetLogger($"trx;LogFileName={v.Name}.trx")
		//		.When(InvokedTargets.Contains(Coverage) || IsServerBuild,
		//			_ => _
		//			.SetProperty("CoverletOutput", $"{ArtifactsDirectory}/{v.Name}.xml")
		//			//.SetCoverletOutput(ArtifactsDirectory / $"{v.Name}.xml")
		//			)
		//		)
		//	);
	});
}