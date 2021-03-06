#module nuget:?package=Cake.DotNetTool.Module&version=0.4.0
#tool "dotnet:https://api.nuget.org/v3/index.json?package=Wyam.Tool&version=2.2.9"
#addin "nuget:https://api.nuget.org/v3/index.json?package=Cake.Wyam&version=2.2.9"
#addin "nuget:https://api.nuget.org/v3/index.json?package=Cake.Yaml&version=3.1.1"
#addin "nuget:https://api.nuget.org/v3/index.json?package=YamlDotNet&version=6.1.2"
#addin "nuget:https://api.nuget.org/v3/index.json?package=Octokit&version=0.32.0"

#load "nuget.cake"

using Octokit;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////

// Define variables
var isRunningOnAppVeyor = AppVeyor.IsRunningOnAppVeyor;
var isPullRequest       = AppVeyor.Environment.PullRequest.IsPullRequest;
var accessToken         = EnvironmentVariable("git_access_token");
var deployRemote        = EnvironmentVariable("git_deploy_remote");
var zipFileName         = "output.zip";
var deployCakeFileName  = "deploy.cake";

// Define directories.
var releaseDir          = Directory("./release");
var sourceDir           = releaseDir + Directory("repo");
var extensionDir        = releaseDir + Directory("extensions");
var outputPath          = MakeAbsolute(Directory("./output"));
var rootPublishFolder   = MakeAbsolute(Directory("publish"));

// Definitions
class ExtensionSpec
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string NuGet { get; set; }
    public List<string> Assemblies { get; set; }
    public string Repository { get; set; }
    public string Author { get; set; }
    public string Description { get; set; }
    public List<string> Categories { get; set; }
}

// Variables
List<ExtensionSpec> extensionSpecs = new List<ExtensionSpec>();

//////////////////////////////////////////////////////////////////////
// SETUP
//////////////////////////////////////////////////////////////////////

Setup(ctx =>
{
    // Executed BEFORE the first task.
    Information("Building site...");
});


//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("CleanSource")
    .Does(() =>
{
    if(DirectoryExists(sourceDir))
    {
        CleanDirectory(sourceDir);
        DeleteDirectory(sourceDir, new DeleteDirectorySettings {
            Recursive = true,
            Force = true
        });
    }

    foreach(var cakeDir in GetDirectories(releaseDir.Path.FullPath + "/cake*"))
    {
        DeleteDirectory(cakeDir, new DeleteDirectorySettings {
            Recursive = true,
            Force = true
        });
    }
});

Task("GetSource")
    .IsDependentOn("CleanSource")
    .Does(() =>
    {
        GitHubClient github = new GitHubClient(new ProductHeaderValue("CakeDocs"));

        if (!string.IsNullOrEmpty(accessToken))
        {
            github.Credentials = new Credentials(accessToken);
        }

        // The GitHub releases API returns Not Found if all are pre-release, so need workaround below
        //Release release = github.Repository.Release.GetLatest("cake-build", "cake").Result;
        Release release = github.Repository.Release.GetAll("cake-build", "cake").Result.First( r =>r.PublishedAt.HasValue);
        FilePath releaseZip = DownloadFile(release.ZipballUrl);
        Unzip(releaseZip, releaseDir);

        // Need to rename the container directory in the zip file to something consistent
        var containerDir = GetDirectories(releaseDir.Path.FullPath + "/*").First(x => x.GetDirectoryName().StartsWith("cake"));
        MoveDirectory(containerDir, sourceDir);
    });

Task("CleanExtensionPackages")
    .Does(() =>
{
    CleanDirectory(extensionDir);
});

Task("GetExtensionSpecs")
    .Does(() =>
{
    var extensionSpecFiles = GetFiles("./extensions/*.yml");
    extensionSpecs
        .AddRange(extensionSpecFiles
            .Select(x =>
            {
                Verbose("Deserializing extension YAML from " + x);
                return DeserializeYamlFromFile<ExtensionSpec>(x);
            })
        );
});

Task("GetExtensionPackages")
    .IsDependentOn("CleanExtensionPackages")
    .IsDependentOn("GetExtensionSpecs")
    .Does(context =>
    {
        context.DownloadPackages(extensionDir,
            extensionSpecs
                .Where(x => !string.IsNullOrEmpty(x.NuGet))
                .Select(x => x.NuGet)
                .ToArray());
    });

Task("Build")
    .IsDependentOn("GetArtifacts")
    .Does(() =>
    {
        Wyam(new WyamSettings
        {
            Recipe = "Docs",
            Theme = "Samson",
            UpdatePackages = true,
            Settings = new Dictionary<string, object>
            {
                { "AssemblyFiles",  extensionSpecs.Where(x => x.Assemblies != null).SelectMany(x => x.Assemblies).Select(x => "../release/extensions" + x) }
            }
        });
    });

// Does not download artifacts (run Build or GetArtifacts target first)
Task("Preview")
    .IsDependentOn("GetExtensionSpecs")
    .Does(() =>
    {
        Wyam(new WyamSettings
        {
            Recipe = "Docs",
            Theme = "Samson",
            UpdatePackages = true,
            Preview = true,
            Watch = true,
            Settings = new Dictionary<string, object>
            {
                { "AssemblyFiles",  extensionSpecs.Where(x => x.Assemblies != null).SelectMany(x => x.Assemblies).Select(x => "../release/extensions" + x) }
            }
        });
    });

// Assumes Wyam source is local and at ../Wyam
Task("Debug")
    .Does(() =>
    {
        StartProcess("../Wyam/src/clients/Wyam/bin/Debug/net462/wyam.exe",
            "-a \"../Wyam/tests/integration/Wyam.Examples.Tests/bin/Debug/net462/**/*.dll\" -r \"docs -i\" -t \"../Wyam/themes/Docs/Samson\" -p --attach");
    });

// Does not download artifacts (run Build or GetArtifacts target first)
Task("Debug-Extensions")
    .IsDependentOn("GetExtensionSpecs")
    .Does(() =>
    {
        StartProcess("../Wyam/src/clients/Wyam/bin/Debug/net462/wyam.exe",
            "-a \"../Wyam/tests/integration/Wyam.Examples.Tests/bin/Debug/net462/**/*.dll\" -r \"docs -i\" -t \"../Wyam/themes/Docs/Samson\" -p --attach"
            + " --setting \"AssemblyFiles=["
            + String.Join(",", extensionSpecs.Where(x => x.Assemblies != null).SelectMany(x => x.Assemblies).Select(x => "../release/extensions" + x))
            + "]\"");
    });

Task("Copy-Bootstrapper-Download")
    .Does(()=>
    {
        CopyDirectory("./download", outputPath.Combine("download"));
        CopyDirectory("./download/bootstrapper", outputPath.Combine("bootstrapper"));
    });

Task("ZipArtifacts")
    .IsDependentOn("Build")
    .IsDependentOn("Copy-Bootstrapper-Download")
    .Does(() =>
{
    Zip(outputPath, zipFileName);
});

Task("UploadArtifacts")
    .IsDependentOn("ZipArtifacts")
    .WithCriteria(BuildSystem.IsRunningOnAzurePipelinesHosted)
    .Does(() =>
{
    AzurePipelines.Commands.UploadArtifact("website", zipFileName, "website");
    AzurePipelines.Commands.UploadArtifact("website", deployCakeFileName, "website");
});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
    .IsDependentOn("Build");

Task("GetArtifacts")
    .IsDependentOn("GetSource")
    .IsDependentOn("GetExtensionPackages");

Task("AppVeyor")
    .IsDependentOn("Build");

Task("AzureDevOps")
    .IsDependentOn("UploadArtifacts");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

if (!StringComparer.OrdinalIgnoreCase.Equals(target, "Deploy"))
{
    RunTarget(target);
}
