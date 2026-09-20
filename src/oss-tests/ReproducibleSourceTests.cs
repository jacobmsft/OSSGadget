// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Microsoft.CST.OpenSource.Helpers;
using Microsoft.CST.OpenSource.OssGadget.Options;
using Microsoft.CST.OpenSource.PackageManagers;
using Microsoft.CST.OpenSource.Reproducibility;
using PackageUrl;
using System.IO.Compression;
using System.Text.Json;

public class ReproducibleSourceTests
{
    [Theory]
    [InlineData("packages/widget", ErrorCode.Ok, false)]
    [InlineData("packages/missing", ErrorCode.ProcessingException, false)]
    [InlineData("packages/missing", ErrorCode.ProcessingException, true)]
    [InlineData(null, ErrorCode.Ok, false)]
    public async Task RunLimitsComparisonsToRequestedSubpath(
        string? subpath,
        ErrorCode expectedExitCode,
        bool leaveIntermediateFiles)
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string outputFile = Path.Combine(outputDirectory, "result.json");
        PackageURL repository = new("github", "example", "repo", null, null, subpath);
        List<PackageURL> sourceRequests = new();
        HashSet<string> intermediateDirectories = new();
        ProjectManagerFactory factory = new(new Dictionary<string, ProjectManagerFactory.ConstructProjectManager>
        {
            ["npm"] = (destination, _, _) =>
                new LocalProjectManager(destination, false, repository, sourceRequests, intermediateDirectories),
            ["github"] = (destination, _, _) =>
                new LocalProjectManager(destination, true, repository, sourceRequests, intermediateDirectories)
        });

        try
        {
            Directory.CreateDirectory(outputDirectory);
            ReproducibleTool tool = new(factory);
            ErrorCode result = await tool.RunAsync(new ReproducibleToolOptions
            {
                Targets = new[] { "pkg:npm/widget@9.9.9" },
                SpecificStrategies = nameof(PackageContainedInSourceStrategy),
                OutputFile = outputFile,
                LeaveIntermediateFiles = leaveIntermediateFiles
            });

            Assert.Equal(expectedExitCode, result);
            Assert.Equal(new[] { "master", "main" }, sourceRequests.Select(request => request.Version));
            Assert.All(sourceRequests, request => Assert.Equal(repository.Subpath, request.Subpath));
            Assert.Single(intermediateDirectories);
            Assert.All(intermediateDirectories, directory =>
                Assert.Equal(leaveIntermediateFiles, Directory.Exists(directory)));

            if (expectedExitCode == ErrorCode.Ok)
            {
                List<ReproducibleToolResult>? results =
                    JsonSerializer.Deserialize<List<ReproducibleToolResult>>(await File.ReadAllTextAsync(outputFile));
                Assert.NotNull(results);
                ReproducibleToolResult comparison = Assert.Single(results);
                Assert.True(comparison.IsReproducible);
                Assert.Equal("pkg:npm/widget@9.9.9", comparison.PackageUrl);
            }
            else
            {
                Assert.False(File.Exists(outputFile));
            }
        }
        finally
        {
            foreach (string directory in intermediateDirectories.Append(outputDirectory))
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepositoryRootRejectsInvalidGitHubArchiveLayout(bool hasMultipleRoots)
    {
        string extractionDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(extractionDirectory);
            if (hasMultipleRoots)
            {
                Directory.CreateDirectory(Path.Combine(extractionDirectory, "first"));
                Directory.CreateDirectory(Path.Combine(extractionDirectory, "second"));
            }
            else
            {
                File.WriteAllText(Path.Combine(extractionDirectory, "not-an-archive.txt"), "error response");
            }

            Assert.Throws<InvalidDataException>(
                () => ReproducibleTool.GetSourceRepositoryRoot(extractionDirectory, "github"));
        }
        finally
        {
            Directory.Delete(extractionDirectory, true);
        }
    }

    private sealed class LocalProjectManager : BaseProjectManager
    {
        private readonly bool isSource;
        private readonly PackageURL repository;
        private readonly List<PackageURL> sourceRequests;
        private readonly HashSet<string> intermediateDirectories;

        public LocalProjectManager(
            string destination,
            bool isSource,
            PackageURL repository,
            List<PackageURL> sourceRequests,
            HashSet<string> intermediateDirectories) : base(destination)
        {
            this.isSource = isSource;
            this.repository = repository;
            this.sourceRequests = sourceRequests;
            this.intermediateDirectories = intermediateDirectories;
        }

        public override string ManagerType => isSource ? "github" : "npm";

        public override Task<string?> GetMetadataAsync(PackageURL purl, bool useCache = true) =>
            Task.FromResult<string?>("{}");

        protected override Task<Dictionary<PackageURL, double>> SearchRepoUrlsInPackageMetadata(
            PackageURL purl,
            string metadata) =>
            Task.FromResult(new Dictionary<PackageURL, double> { [repository] = 1 });

        public override Task<IEnumerable<string>> EnumerateVersionsAsync(
            PackageURL purl,
            bool useCache = true,
            bool includePrerelease = true) =>
            throw new InvalidOperationException("Explicit package versions must not trigger version discovery.");

        public override async Task<IEnumerable<string>> DownloadVersionAsync(
            PackageURL purl,
            bool doExtract,
            bool cached = false)
        {
            intermediateDirectories.Add(Path.GetDirectoryName(TopLevelExtractionDirectory)!);
            if (isSource)
            {
                sourceRequests.Add(purl);
                if (purl.Version != "main")
                {
                    return Array.Empty<string>();
                }
            }

            using MemoryStream content = new();
            using (ZipArchive archive = new(content, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    isSource ? "repo-main/packages/widget/index.js" : "package/index.js");
                using StreamWriter writer = new(entry.Open());
                writer.Write("module.exports = 1;");
            }
            content.Position = 0;

            string extractionDirectory = await ArchiveHelper.ExtractArchiveAsync(
                TopLevelExtractionDirectory,
                isSource ? "github-example-repo-main" : "npm-widget-9.9.9",
                content);
            return new[] { extractionDirectory };
        }
    }
}
