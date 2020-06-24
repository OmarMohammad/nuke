// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.AppVeyor;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.CI.TeamCity;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Tools.Slack;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.ControlFlow;
using static Nuke.Common.Gitter.GitterTasks;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Nuke.Common.Tools.Slack.SlackTasks;

[CheckBuildProjectConfigurations]
[DotNetVerbosityMapping]
[UnsetVisualStudioEnvironmentVariables]
[ShutdownDotNetAfterServerBuild]
[TeamCitySetDotCoverHomePath]
[TeamCity(
    TeamCityAgentPlatform.Windows,
    Version = "2019.2",
    VcsTriggeredTargets = new[] { nameof(Pack), nameof(Test) },
    NightlyTriggeredTargets = new[] { nameof(Pack), nameof(Test) },
    ManuallyTriggeredTargets = new[] { nameof(Publish) },
    NonEntryTargets = new[] { nameof(Restore), nameof(DownloadFonts), nameof(InstallFonts), nameof(ReleaseImage) },
    ExcludedTargets = new[] { nameof(Clean) })]
[GitHubActions(
    "continuous",
    GitHubActionsImage.MacOs1014,
    GitHubActionsImage.Ubuntu1604,
    GitHubActionsImage.Ubuntu1804,
    GitHubActionsImage.WindowsServer2016R2,
    GitHubActionsImage.WindowsServer2019,
    On = new[] { GitHubActionsTrigger.Push },
    InvokedTargets = new[] { nameof(Test), nameof(Pack) },
    ImportGitHubTokenAs = nameof(GitHubToken),
    ImportSecrets = new[] { nameof(SlackWebhook), nameof(GitterAuthToken) })]
[AppVeyor(
    AppVeyorImage.VisualStudio2019,
    AppVeyorImage.Ubuntu1804,
    SkipTags = true,
    InvokedTargets = new[] { nameof(Test), nameof(Pack) })]
[AzurePipelines(
    suffix: null,
    AzurePipelinesImage.UbuntuLatest,
    AzurePipelinesImage.WindowsLatest,
    AzurePipelinesImage.MacOsLatest,
    InvokedTargets = new[] { nameof(Test), nameof(Pack) },
    NonEntryTargets = new[] { nameof(Restore), nameof(DownloadFonts), nameof(InstallFonts), nameof(ReleaseImage) },
    ExcludedTargets = new[] { nameof(Clean), nameof(Coverage) })]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Pack);

    Target Foo => _ => _
        .Executes(() =>
        {
            Console.WriteLine(Configuration);
            Console.WriteLine(MyPath);
        });

    [Parameter] readonly AbsolutePath MyPath;

    [CI] readonly TeamCity TeamCity;
    [CI] readonly AzurePipelines AzurePipelines;

    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(NoFetch = true)] readonly GitVersion GitVersion;
    [Solution] readonly Solution Solution;

    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath SourceDirectory => RootDirectory / "source";

    const string MasterBranch = "master";
    const string DevelopBranch = "develop";
    const string ReleaseBranchPrefix = "release";
    const string HotfixBranchPrefix = "hotfix";

    const int ServiceUnavailableRetryTimeout = 300;
    const int WaitForCompletionRetryTimeout = 500;
    const int WaitForCompletionTimeout = 300_000;
    const int DefaultHttpClientTimeout = 100;
    const int UploadAndDownloadRequestTimeout = 300;

    static void SubmitSigningRequest(
        string userToken,
        string organizationId,
        string artifactConfigurationId,
        string signingPolicyId,
        string projectKey,
        string artifactConfigurationKey,
        string signingPolicyKey,
        string inputArtifactPath,
        string description,
        string apiUrl = "https://app.signpath.io/api/v1")
    {
        CreateAndUseAuthorizedHttpClient(userToken, DefaultHttpClientTimeout,
            defaultHttpClient =>
            {
                CreateAndUseAuthorizedHttpClient(userToken, UploadAndDownloadRequestTimeout,
                    uploadAndDownloadHttpClient =>
                    {
                        var outputArtifactPath =
                            Path.ChangeExtension(
                                inputArtifactPath,
                                $".signed{Path.GetExtension(inputArtifactPath)}");
                        var submitUrl = $"{apiUrl}/{organizationId}/SigningRequests";
                        var getUrl = SubmitVia(
                            uploadAndDownloadHttpClient,
                            artifactConfigurationId,
                            signingPolicyId,
                            projectKey,
                            artifactConfigurationKey,
                            signingPolicyKey,
                            description,
                            inputArtifactPath,
                            submitUrl);

                        var downloadUrl = WaitForCompletion(
                            defaultHttpClient,
                            getUrl);
                        DownloadArtifact(
                            uploadAndDownloadHttpClient,
                            downloadUrl,
                            outputArtifactPath);
                    });
            });
    }

    static string TriggerViaWebhook(
        string userToken,
        string organizationId,
        string projectKey,
        string signingPolicyKey)
    {
        string uri = null;
        // https://app.signpath.io/API/v1/<ORGANIZATION_ID>/Integrations/AppVeyor?ProjectKey=<PROJECT_KEY>&SigningPolicyKey=<SIGNING_POLICY_KEY>
        CreateAndUseAuthorizedHttpClient(userToken, DefaultHttpClientTimeout, httpHandler =>
        {
            Console.WriteLine($"BuildVersion = {AppVeyor.Instance.BuildVersion}");
            Console.WriteLine($"BuildId = {AppVeyor.Instance.BuildId}");
            Console.WriteLine($"BuildNumber = {AppVeyor.Instance.BuildNumber}");
            var response = httpHandler.PostAsync(
                requestUri:
                $"https://app.signpath.io/API/v1/{organizationId}/Integrations/AppVeyor?ProjectKey={projectKey}&SigningPolicyKey={signingPolicyKey}",
                new StringContent(SerializationTasks
                        .JsonSerialize(new
                        {
                            AppVeyor.Instance.AccountName,
                            AppVeyor.Instance.ProjectSlug,
                            AppVeyor.Instance.BuildVersion,
                            AppVeyor.Instance.BuildId,
                            AppVeyor.Instance.JobId
                        }),
                    Encoding.UTF8,
                    "application/json")
            ).GetAwaiter().GetResult();
            Console.WriteLine(response.StatusCode);
            Console.WriteLine(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert(response.StatusCode == HttpStatusCode.Created,
                "response.StatusCode == HttpStatusCode.Created");

            uri = response.Headers.Location.AbsoluteUri;
        });
        return uri;
    }

    static void GetSignedArtifact(
        string userToken,
        string organizationId,
        string signingRequestId,
        string outputArtifactPath,
        string apiUrl = "https://app.signpath.io/api/v1")
    {
        CreateAndUseAuthorizedHttpClient(userToken, DefaultHttpClientTimeout,
            defaultHttpClient =>
            {
                CreateAndUseAuthorizedHttpClient(userToken, UploadAndDownloadRequestTimeout,
                    uploadAndDownloadHttpClient =>
                    {
                        var expectedSigningRequestUrl = $"{apiUrl}/{organizationId}/SignRequests/{signingRequestId}";
                        var downloadUrl = WaitForCompletion(
                            defaultHttpClient,
                            expectedSigningRequestUrl);
                        DownloadArtifact(
                            uploadAndDownloadHttpClient,
                            downloadUrl,
                            outputArtifactPath);
                    });
            });
    }

    static void CreateAndUseAuthorizedHttpClient(string userToken, int timeout, Action<HttpClient> action)
    {
        var previousProtocol = ServicePointManager.SecurityProtocol;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeout),
            DefaultRequestHeaders =
            {
                Authorization = new AuthenticationHeaderValue("Bearer", userToken)
            }
        };
        action.Invoke(httpClient);
        ServicePointManager.SecurityProtocol = previousProtocol;
    }

    static string WaitForCompletion(HttpClient httpClient, string url)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            Console.WriteLine("checking status");
            using var response = GetWithRetry(httpClient, url);
            var result = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var jsonResult = SerializationTasks.JsonDeserialize<JObject>(result);
            var status = jsonResult["status"].Value<string>();
            Console.WriteLine(status);

            switch (status)
            {
                case "Completed":
                    return jsonResult["signedArtifactLink"].Value<string>();
                case "Failed":
                case "Denied":
                case "Canceled":
                    throw new Exception(status);
            }

            Thread.Sleep(WaitForCompletionRetryTimeout);
        } while (stopwatch.ElapsedMilliseconds < WaitForCompletionTimeout);

        throw new Exception("Operation timed out");
    }

    static HttpResponseMessage GetWithRetry(HttpClient httpClient, string url)
    {
        var response = SendWithRetry(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, url));
        return response;
    }

    static HttpResponseMessage SendWithRetry(
        HttpClient httpClient,
        Func<HttpRequestMessage> action)
    {
        var retry = 0;
        string reason = null;
        while (true)
        {
            try
            {
                var request = action.Invoke();
                var response = httpClient.SendAsync(request).GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    reason = "Temp unavailable";
                else if (HttpStatusCode.InternalServerError <= response.StatusCode)
                    reason = $"Unexpected status code {response.StatusCode}";
                else
                    return response;
            }
            catch (Exception e)
            {
                if (retry > 3)
                    throw;
                Thread.Sleep(ServiceUnavailableRetryTimeout);
                retry++;
            }
        }
    }

    static void DownloadArtifact(
        HttpClient httpClient,
        string url,
        string path)
    {
        using var response = GetWithRetry(httpClient, url);
        Assert(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} == HttpStatusCode.OK");

        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        EnsureExistingParentDirectory(path);
        using var fileStream = File.Open(path, FileMode.Create);
        stream.CopyToAsync(fileStream).GetAwaiter().GetResult();
    }

    static string SubmitVia(HttpClient httpClient,
        [CanBeNull] string artifactConfigurationId,
        [CanBeNull] string signingPolicyId,
        [CanBeNull] string projectKey,
        [CanBeNull] string artifactConfigurationKey,
        [CanBeNull] string signingPolicyKey,
        string description,
        string inputArtifact,
        string url)
    {
        HttpRequestMessage Factory()
        {
            var content = new MultipartFormDataContent();
            var data = new[]
                {
                    ("ArtifactConfigurationId", artifactConfigurationId),
                    ("SigningPolicyId", signingPolicyId),
                    ("ProjectKey", projectKey),
                    ("ArtifactConfigurationKey", artifactConfigurationKey),
                    ("SigningPolicyKey", signingPolicyKey),
                    ("Description", description)
                }
                .Where(x => x.Item2 != null).ToList();
            data.ForEach(x => content.Add(new StringContent(x.Item2), x.Item1));

            var fileStream = new FileStream(inputArtifact, FileMode.Open);
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "Artifact", inputArtifact);

            var request = new HttpRequestMessage(HttpMethod.Post, url) {Content = content};
            return request;
        }

        var response = SendWithRetry(httpClient, Factory);
        ControlFlow.Assert(response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode} == HttpStatusCode.OK");

        return response.Headers.Location.AbsoluteUri;
    }

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("*/bin", "*/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    [Parameter] bool IgnoreFailedSources;
    [Parameter] string SignPathApiToken;

    Target Restore => _ => _
        .Requires(() => SignPathApiToken)
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetProjectFile(Solution)
                .SetIgnoreFailedSources(IgnoreFailedSources));
        });

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    Project GlobalToolProject => Solution.GetProject("Nuke.GlobalTool");
    Project MSBuildTasksProject => Solution.GetProject("Nuke.MSBuildTasks");

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetNoRestore(InvokedTargets.Contains(Restore))
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion));

            var publishConfigurations =
                from project in new[] { GlobalToolProject, MSBuildTasksProject }
                from framework in project.GetTargetFrameworks()
                select new { project, framework };

            DotNetPublish(_ => _
                    .SetNoRestore(InvokedTargets.Contains(Restore))
                    .SetConfiguration(Configuration)
                    .SetAssemblyVersion(GitVersion.AssemblySemVer)
                    .SetFileVersion(GitVersion.AssemblySemFileVer)
                    .SetInformationalVersion(GitVersion.InformationalVersion)
                    .CombineWith(publishConfigurations, (_, v) => _
                        .SetProject(v.project)
                        .SetFramework(v.framework)),
                degreeOfParallelism: 10);
        });

    string ChangelogFile => RootDirectory / "CHANGELOG.md";
    AbsolutePath PackageDirectory => OutputDirectory / "packages";
    IEnumerable<string> ChangelogSectionNotes => ExtractChangelogSectionNotes(ChangelogFile);

    Target Pack => _ => _
        .DependsOn(Compile)
        .Produces(PackageDirectory / "*.nupkg")
        .Executes(() =>
        {
            DotNetPack(_ => _
                .SetProject(Solution)
                .SetNoBuild(InvokedTargets.Contains(Compile))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackageDirectory)
                .SetVersion(GitVersion.NuGetVersionV2)
                .SetPackageReleaseNotes(GetNuGetReleaseNotes(ChangelogFile, GitRepository)));

            var packagesZip = OutputDirectory / "packages.zip";
            CompressZip(PackageDirectory, packagesZip);
            AppVeyor.Instance.PushArtifact(packagesZip);
            
            // PackageFiles.ForEach(x => AppVeyor.Instance.PushArtifact(x));
            var organizationId = "09e500c8-00f7-4f3b-95f6-e33f77e67410";
            var uri = TriggerViaWebhook(
                userToken: SignPathApiToken,
                organizationId: organizationId,
                projectKey: "nuke",
                signingPolicyKey: "test-signing");

            CreateAndUseAuthorizedHttpClient(
                SignPathApiToken,
                DefaultHttpClientTimeout,
                httpClient =>
                {
                    var downloadUrl = WaitForCompletion(httpClient, uri);
                    DownloadArtifact(httpClient, downloadUrl, PackageDirectory / "packages.signed.zip");
                    AppVeyor.Instance.PushArtifact(PackageDirectory / "packages.signed.zip");
                    Console.WriteLine(downloadUrl);
                });
        });

    [Partition(2)] readonly Partition TestPartition;
    AbsolutePath TestResultDirectory => OutputDirectory / "test-results";
    IEnumerable<Project> TestProjects => TestPartition.GetCurrent(Solution.GetProjects("*.Tests"));

    Target Test => _ => _
        .DependsOn(Compile)
        .Produces(TestResultDirectory / "*.trx")
        .Produces(TestResultDirectory / "*.xml")
        .Partition(() => TestPartition)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetConfiguration(Configuration)
                .SetNoBuild(InvokedTargets.Contains(Compile))
                .ResetVerbosity()
                .SetResultsDirectory(TestResultDirectory)
                .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                    .EnableCollectCoverage()
                    .SetCoverletOutputFormat(CoverletOutputFormat.cobertura)
                    .SetExcludeByFile("*.Generated.cs")
                    .When(IsServerBuild, _ => _
                        .EnableUseSourceLink()))
                .CombineWith(TestProjects, (_, v) => _
                    .SetProjectFile(v)
                    .SetLogger($"trx;LogFileName={v.Name}.trx")
                    .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                        .SetCoverletOutput(TestResultDirectory / $"{v.Name}.xml"))));

            TestResultDirectory.GlobFiles("*.trx").ForEach(x =>
                AzurePipelines?.PublishTestResults(
                    type: AzurePipelinesTestResultsType.VSTest,
                    title: $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.StageDisplayName})",
                    files: new string[] { x }));
        });

    string CoverageReportDirectory => OutputDirectory / "coverage-report";
    string CoverageReportArchive => OutputDirectory / "coverage-report.zip";

    Target Coverage => _ => _
        .DependsOn(Test)
        .TriggeredBy(Test)
        .Consumes(Test)
        .Produces(CoverageReportArchive)
        .Executes(() =>
        {
            ReportGenerator(_ => _
                .SetReports(TestResultDirectory / "*.xml")
                .SetReportTypes(ReportTypes.HtmlInline)
                .SetTargetDirectory(CoverageReportDirectory)
                .SetFramework("netcoreapp2.1"));

            TestResultDirectory.GlobFiles("*.xml").ForEach(x =>
                AzurePipelines?.PublishCodeCoverage(
                    AzurePipelinesCodeCoverageToolType.Cobertura,
                    x,
                    CoverageReportDirectory));

            CompressZip(
                directory: CoverageReportDirectory,
                archiveFile: CoverageReportArchive,
                fileMode: FileMode.Create);
        });

    Target Analysis => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            InspectCode(_ => _
                .SetTargetPath(Solution)
                .SetOutput(OutputDirectory / "inspectCode.xml")
                .AddPlugin("EtherealCode.ReSpeller", InspectCodePluginLatest)
                .AddPlugin("PowerToys.CyclomaticComplexity", InspectCodePluginLatest)
                .AddPlugin("ReSharper.ImplicitNullability", InspectCodePluginLatest)
                .AddPlugin("ReSharper.SerializationInspections", InspectCodePluginLatest)
                .AddPlugin("ReSharper.XmlDocInspections", InspectCodePluginLatest));
        });

    [Parameter("NuGet Api Key")] readonly string ApiKey;
    [Parameter("NuGet Source for Packages")] readonly string Source = "https://api.nuget.org/v3/index.json";

    Target Publish => _ => _
        .ProceedAfterFailure()
        .DependsOn(Clean, Test, Pack)
        .Consumes(Pack)
        .Requires(() => ApiKey)
        .Requires(() => GitHasCleanWorkingCopy())
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Requires(() => GitRepository.Branch.EqualsOrdinalIgnoreCase(MasterBranch) ||
                        GitRepository.Branch.EqualsOrdinalIgnoreCase(DevelopBranch) ||
                        GitRepository.Branch.StartsWithOrdinalIgnoreCase(ReleaseBranchPrefix) ||
                        GitRepository.Branch.StartsWithOrdinalIgnoreCase(HotfixBranchPrefix))
        .Executes(() =>
        {
            var packages = PackageFiles;
            Assert(packages.Count == 5, "packages.Count == 5");

            DotNetNuGetPush(_ => _
                    .SetSource(Source)
                    .SetApiKey(ApiKey)
                    .CombineWith(packages, (_, v) => _
                        .SetTargetPath(v)),
                degreeOfParallelism: 5,
                completeOnFailure: true);
        });

    IReadOnlyCollection<AbsolutePath> PackageFiles => PackageDirectory.GlobFiles("*.nupkg");

    Target Install => _ => _
        .DependsOn(Pack)
        .Executes(() =>
        {
            SuppressErrors(() => DotNet($"tool uninstall -g {GlobalToolProject.Name}"));
            DotNet($"tool install -g {GlobalToolProject.Name} --add-source {OutputDirectory} --version {GitVersion.NuGetVersionV2}");
        });
}
