#tool "nuget:?package=NUnit.ConsoleRunner&version=3.7.0"
#tool "nuget:?package=GitVersion.CommandLine&version=3.6.5"
#load "./parameters.cake"


var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
bool publishingError = false;

BuildParameters parameters = BuildParameters.GetParameters(Context);

MSBuildSettings CreateBuildSettings()
{
    return new MSBuildSettings()
        .SetConfiguration(configuration)
        .WithProperty("Version", parameters.Version);
}

Setup(context =>
{
    parameters.Initialize(context);

    Information("SemVersion: {0}", parameters.SemVersion);
    Information("Version: {0}", parameters.Version);
    Information("Building from branch: " + AppVeyor.Environment.Repository.Branch);
});

Teardown(context =>
{
    Information("Cake.. NOM-NOM");
});


Task("debug")
    .Does(() => {
        Information("debug");
    });

Task("Clean")
    .Does(() =>
{
    // Clean solution directories.
    Information("Cleaning old files");


    CleanDirectories(new DirectoryPath[]{
        parameters.BuildDir,
        parameters.BuildResultDir,
        Directory("./src/Tests/bin/"),
        Directory("./src/Tests/obj/"),
        Directory(BuildParameters.ProjectDir + "bin"),
        Directory(BuildParameters.ProjectDir + "obj"),
    });
});



Task("Restore-Nuget-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
{
    Information("Restoring packages in {0}", BuildParameters.Solution);

    MSBuild(BuildParameters.Solution, CreateBuildSettings().WithTarget("Restore"));
});





Task("Build")
    .IsDependentOn("Restore-Nuget-Packages")
    .Does(() =>
{
    Information("Building {0}", BuildParameters.Solution);

    MSBuild(BuildParameters.Solution, CreateBuildSettings().WithTarget("Build"));
});

Task("Start-LocalDB")
    .Description(@"Starts LocalDB - executes the following: C:\Program Files\Microsoft SQL Server\120\Tools\Binn\SqlLocalDB.exe create v12.0 12.0 -s")
    .WithCriteria(() => !parameters.SkipTests)
    .Does(() => 
    {
        var sqlLocalDbPath13 = @"c:\Program Files\Microsoft SQL Server\130\Tools\Binn\SqlLocalDB.exe";
        var sqlLocalDbPath12 = @"C:\Program Files\Microsoft SQL Server\120\Tools\Binn\SqlLocalDB.exe";

        if(FileExists(sqlLocalDbPath13))
        {
            StartProcess(sqlLocalDbPath13, new ProcessSettings(){ Arguments="create \"v12.0\" 12.0 -s" });    
            return;
        }

        if(FileExists(sqlLocalDbPath12))
        {
            StartProcess(sqlLocalDbPath12, new ProcessSettings(){ Arguments="create \"v12.0\" 12.0 -s" });    
            return;
        }

        Information("Unable to start LocalDB");
        throw new Exception("LocalDB v12 is not installed. Can't complete tests");
    });

Task("Run-Unit-Tests")
    .IsDependentOn("Build")
    .IsDependentOn("Start-LocalDB")
    .WithCriteria(() => !parameters.SkipTests)
    .Does(() =>
	{
        var tempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        CreateDirectory(tempDirectory);
        try
        {
            try
            {
                DotNetCoreTest("src/Tests/Tests.csproj", new DotNetCoreTestSettings
                {
                    Configuration = configuration,
                    NoBuild = true,
                    Logger = "trx",
                    ArgumentCustomization = a => a.AppendSwitchQuoted("--results-directory", tempDirectory)
                });
            }
            finally
            {
                if (parameters.IsRunningOnAppVeyor)
                {
                    // dotnet test cannot do more than one target framework per TRX file
                    // AppVeyor seems to ignore all but the first TRX upload– perhaps because the test names are identical
                    // https://github.com/Microsoft/vstest/issues/880#issuecomment-341912021
                    foreach (var testResultsFile in GetFiles(tempDirectory + "/**/*.trx"))
                        AppVeyor.UploadTestResults(testResultsFile, AppVeyorTestResultsType.MSTest);
                }
            }
        }
        finally
        {
            DeleteDirectory(tempDirectory, new DeleteDirectorySettings { Recursive = true });
        }
    });



Task("Create-NuGet-Packages")
    .IsDependentOn("Run-Unit-Tests")
    .Does(() =>
	{
        // https://github.com/cake-build/cake/issues/1910
        var releaseNotes = string.Join("%0D%0A", ParseReleaseNotes("./ReleaseNotes.md").Notes);

        MSBuild(BuildParameters.ProjectDir, CreateBuildSettings()
            .WithTarget("Pack")
            .WithProperty("PackageReleaseNotes", releaseNotes)
            .WithProperty("PackageOutputPath", System.IO.Path.GetFullPath(parameters.BuildResultDir)));
	});



Task("Publish-Nuget")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.ShouldPublishToNugetOrg)
    .Does(() =>
	{
		// Resolve the API key.
		var apiKey = EnvironmentVariable("NUGET_API_KEY");

		if(string.IsNullOrEmpty(apiKey))
		{
			throw new InvalidOperationException("Could not resolve MyGet API key.");
		}

		// Push the package.
		NuGetPush(parameters.ResultNugetPath, new NuGetPushSettings
		{
			ApiKey = apiKey,
			Source = "https://www.nuget.org/api/v2/package"
		});
	})
	.OnError(exception =>
	{
		Information("Publish-NuGet Task failed, but continuing with next Task...");
		publishingError = true;
	});


Task("Publish-MyGet")
    .IsDependentOn("Package")
    .WithCriteria(() => parameters.ShouldPublishToMyGet)
    .Does(() =>
	{
		// Resolve the API key.
		var apiKey = EnvironmentVariable("MYGET_API_KEY");
		if(string.IsNullOrEmpty(apiKey)) {
			throw new InvalidOperationException("Could not resolve MyGet API key.");
		}

		// Resolve the API url.
		var apiUrl = EnvironmentVariable("MYGET_API_URL");
		if(string.IsNullOrEmpty(apiUrl)) {
			throw new InvalidOperationException("Could not resolve MyGet API url.");
		}

		// Push the package.
		NuGetPush(parameters.ResultNugetPath, new NuGetPushSettings {
			Source = apiUrl,
			ApiKey = apiKey
		});
	})
	.OnError(exception =>
	{
		Information("Publish-MyGet Task failed, but continuing with next Task...");
		publishingError = true;
	});


Task("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => parameters.IsRunningOnAppVeyor)
    .Does(() =>
	{
		AppVeyor.UploadArtifact(parameters.ResultNugetPath);
	});


Task("Package")
    .IsDependentOn("Create-NuGet-Packages");

Task("AppVeyor")
    //.IsDependentOn("Publish-Nuget")
    .IsDependentOn("Publish-MyGet")
    .IsDependentOn("Upload-AppVeyor-Artifacts")
    .Finally(() =>
    {
        if(publishingError)
        {
            throw new Exception("An error occurred during the publishing of Cake.  All publishing tasks have been attempted.");
        }
    });

Task("Default")
    .IsDependentOn("Package");

RunTarget(target);