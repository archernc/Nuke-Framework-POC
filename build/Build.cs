using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Tools.Octopus;
using Nuke.Common.Tools.Paket;
using Nuke.Common.Tools.VSTest;
using Nuke.Common.Utilities.Collections;
using System;
using System.Linq;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.Tools.Octopus.OctopusTasks;
using static Nuke.Common.Tools.Paket.PaketTasks;
using static Nuke.Common.Tools.VSTest.VSTestTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[TeamCitySetDotCoverHomePath]
class Build : NukeBuild
{
	/// Support plugins are available for:
	///   - JetBrains ReSharper        https://nuke.build/resharper
	///   - JetBrains Rider            https://nuke.build/rider
	///   - Microsoft VisualStudio     https://nuke.build/visualstudio
	///   - Microsoft VSCode           https://nuke.build/vscode

	public static int Main() => Execute<Build>(x => x.Octo_Pack);

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
	/// Build & Generates NuGet packages for Octopus Deploy
	/// </summary>
	Target Compile => _ => _
	.DependsOn(Restore)
	.Executes(() =>
	{
		MSBuild(_ => _
		.SetWorkingDirectory(Solution.Directory)
		.SetTargetPath(Solution.Path)
		.SetTargets("Rebuild")
		.SetConfiguration(Configuration)
		.SetAssemblyVersion(GitVersion.AssemblySemVer)
		.SetFileVersion(GitVersion.AssemblySemFileVer)
		.SetInformationalVersion(GitVersion.InformationalVersion)
		.SetMaxCpuCount(Environment.ProcessorCount)
		.SetNodeReuse(false)
		.When(InvokedTargets.Contains(Octo_Pack) || InvokedTargets.Contains(Octo_Push) || IsServerBuild, _ =>
			_.AddProperty("RunOctoPack", "true")
			.AddProperty("OctoPackPackageVersion", GitVersion.NuGetVersionV2)
			.AddProperty("OctoPackEnforceAddingFiles", "true")
			.AddProperty("OctoPackAppendProjectToFeed", "true")
			.AddProperty("OctoPackNuGetProperties", $"buildDate={DateTime.Now};author=TeamCity;")
			.AddProperty("OctoPackPublishPackagesToTeamCity", "true")
			.AddProperty("OctoPackPublishPackageToFileShare", OctopusOutputDirectory)
			.EnableTreatWarningsAsErrors()
			)
		);
	});

	// http://www.nuke.build/docs/authoring-builds/ci-integration.html#partitioning
	[Partition(2)] readonly Partition TestPartition;
	/// <summary>
	/// Runs all tests and generates report file(s)
	/// </summary>
	Target Test => _ => _
	.DependsOn(Compile)
	.Produces($"{ArtifactsDirectory}/*.trx")
	.Produces($"{ArtifactsDirectory}/*.xml")
	.Partition(() => TestPartition)
	.Executes(() =>
	{
		var projects = TestPartition.GetCurrent(Solution.GetProjects("*.*Test"));
		if (projects.Any())
		{
			VSTest(_ => _
			.CombineWith(projects,
				(_, v) => _
				.SetTestAssemblies((v.Directory / "bin" / Configuration).GlobFiles("*.*Test.dll").Select(a => a.ToString()))
				.SetLogger(Host == HostType.TeamCity
					? "TeamCity"
					: $"trx;LogFileName={v.Name}.trx"
				)
				.SetTestAdapterPath(IsServerBuild
					? $"{NuGetPackageResolver.GetLocalInstalledPackage("teamcity.vstest.testadapter", NuGetPackagesConfigFile)?.Directory}/build/_common/vstest15"
					: "."
				)

			));
		}
	});

	string CoverageReportDirectory => $"{ArtifactsDirectory}/coverage-report";
	string CoverageReportArchive => $"{ArtifactsDirectory}/coverage-report.zip";

	Target Coverage => _ => _
	.DependsOn(Test)
	//.TriggeredBy(Test)
	.Produces(CoverageReportArchive)
	.Executes(() =>
	{
		//https://nuke.build/api/Nuke.Common/Nuke.Common.Tools.DotCover.DotCoverTasks.html
		//DotCoverTasks.DotCoverCover();
	});

	/// <summary>
	/// Creates NuGet package(s) that can be pushed to NuGet server.
	/// Implements Paket
	/// </summary>
	Target NuGet_Pack => _ => _
	.DependsOn(Test)
	.Produces($"{NuGetOutputDirectory}/*.nupkg")
	.Executes(() =>
	{
		// utilizes paket.template file to construct package
		PaketPack(_ => _
			.SetToolPath($"{RootDirectory}/.paket/paket.exe")
			.SetLockDependencies(true)
			.SetBuildConfiguration(Configuration)
			.SetPackageVersion(GitVersion.NuGetVersionV2)
			.SetOutputDirectory(NuGetOutputDirectory)
		);
	});

	/// <summary>
	/// Pushes all packages generated from <see cref="NuGet_Pack">NuGet_Pack</see> to the NuGet repository
	/// Implements DotNetNuGet
	/// </summary>
	Target NuGet_Push => _ => _
	.DependsOn(NuGet_Pack)
	.Requires(() => !string.IsNullOrWhiteSpace(NUGET_SERVER_KEY))
	.Requires(() => !string.IsNullOrWhiteSpace(NUGET_SERVER_URL))
	.Executes(() =>
	{
		var packages = NuGetOutputDirectory.GlobFiles("*.nupkg");
		if (packages.Any())
		{
			// requires NuGet.CommandLine
			//NuGetPush(_ => _
			//	.SetApiKey(NUGET_SERVER_KEY)
			//	.SetSource(NUGET_SERVER_URL)
			//	.CombineWith(packages, (_, v) => _.SetTargetPath(v)),
			//	degreeOfParallelism: 5,
			//	completeOnFailure: true
			//);

			PaketPush(_ => _
				.SetToolPath($"{RootDirectory}/.paket/paket.exe")
				.SetApiKey(NUGET_SERVER_KEY)
				.SetUrl(NUGET_SERVER_URL)
				.CombineWith(packages, (_, v) => _.SetFile(v)),
				degreeOfParallelism: 5,
				completeOnFailure: true
			);
		}
	});

	/// <summary>
	/// This is just a convenience target to force the output of packages
	/// </summary>
	Target Octo_Pack => _ => _
	.DependsOn(Compile)
	.Produces($"{OctopusOutputDirectory}/*.nupkg")
	.Executes(() =>
	{
		// The packaging is part of the MSBuild step for frameworks projects.
		//TODO: Docker packages may need a different process
	});

	/// <summary>
	/// Pushes all packages generated from <see cref="Octo_Pack">Octo_Pack</see> to the Octopus repository
	/// </summary>
	Target Octo_Push => _ => _
	.DependsOn(Compile)
	.Requires(() => !string.IsNullOrWhiteSpace(OCTOPUS_DEPLOY_SERVER))
	.Requires(() => !string.IsNullOrWhiteSpace(OCTOPACK_PUBLISH_APIKEY))
	.Executes(() =>
	{
		var packages = OctopusOutputDirectory.GlobFiles("*.nupkg");
		if (packages.Any())
		{
			OctopusPush(_ => _
			.SetServer(OCTOPUS_DEPLOY_SERVER)
			.SetApiKey(OCTOPACK_PUBLISH_APIKEY)
			.EnableReplaceExisting() // keeps from failing the build
			.CombineWith(packages, (_, v) => _.SetPackage(v))
			);
		}
	});

	Target Octo_Create_Release => _ => _
	.DependsOn(Octo_Push)
	.Requires(() => !string.IsNullOrWhiteSpace(OCTOPUS_DEPLOY_SERVER))
	.Requires(() => !string.IsNullOrWhiteSpace(OCTOPACK_PUBLISH_APIKEY))
	.Requires(() => !string.IsNullOrWhiteSpace(OCTOPUS_PROJECT_NAME))
	.Executes(() =>
	{
		OctopusCreateRelease(_ => _
		.SetServer(OCTOPUS_DEPLOY_SERVER)
		.SetApiKey(OCTOPACK_PUBLISH_APIKEY)
		.SetProject(OCTOPUS_PROJECT_NAME)
		.SetEnableServiceMessages(true)
		.SetDefaultPackageVersion(GitVersion.NuGetVersionV2)
		.SetVersion($"{GitVersion.NuGetVersionV2}.i") //auto increment the release version number to allow TC to create duplicate code releases #extreme_edge_case
		.SetReleaseNotes("") //TODO: grab the commit comments
		);
	});
}