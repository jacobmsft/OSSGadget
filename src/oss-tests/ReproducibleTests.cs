// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using OssGadget.Options;
using Microsoft.CST.OpenSource.Reproducibility;
using System.Text.Json;

public class ReproducibleTest
{
    [Theory]
    [InlineData("pkg:npm/left-pad@1.3.0", true)]
    [InlineData("pkg:npm/non-existent1267461827467@12421", false)]
    public async Task CheckReproducibility(string packageUrl, bool? expectedToBeReproducible)
    {
        string? outputFilename = Guid.NewGuid().ToString() + ".json";
        var options = new ReproducibleToolOptions()
        {
            AllStrategies = true, OutputFile = outputFilename, Targets = new[] { packageUrl }
        };
        await new ReproducibleTool().RunAsync(options);

        bool outputFileExists = File.Exists(outputFilename);

        if (expectedToBeReproducible != null)
        {
            Assert.True(outputFileExists, "Output file does not exist.");
            string? result = File.ReadAllText(outputFilename);

            List<ReproducibleToolResult>? jsonResults = JsonSerializer.Deserialize<List<ReproducibleToolResult>>(result);
            Assert.NotNull(jsonResults); // "Output file was not parseable."

            Assert.Equal(expectedToBeReproducible == true, jsonResults.First().IsReproducible);
        }
        else
        {
            if (outputFileExists)
            {
                File.Delete(outputFilename);
            }
            Assert.True(!outputFileExists, "File was produced but should have been an error.");
        }

        // Cleanup
        if (File.Exists(outputFilename))
        {
            File.Delete(outputFilename);
        }

    }


    [Theory]
    [InlineData("/foo/bar/quux.c", "quux.c", "quux.c")]
    [InlineData("/foo/bar/quux.c", "baz.c", null)]
    [InlineData("/foo/bar/quux.c", "baz/quux.c,bar/quux.c", "bar/quux.c")]
    public async Task CheckGetClosestMatch(string filename, string targets, string expectedTarget)
    {
        IEnumerable<string>? results = OssReproducibleHelpers.GetClosestFileMatch(filename, targets.Split(','));
        Assert.NotNull(results);

        if (expectedTarget == null)
        {
            Assert.False(results.Any());
        }
        else
        {
            Assert.True(results.Any());
            Assert.Equal(expectedTarget, results.First());
        }
    }

    [Fact]
    public void SourceReferencesDoNotAssumePackageVersion()
    {
        IReadOnlyList<string> references = ReproducibleTool.GetSourceReferences(null, null);

        Assert.Equal(new[] { "master", "main" }, references);
    }

    [Fact]
    public void SourceReferencesPreferExplicitRefsAndKeepBranchFallbacks()
    {
        IReadOnlyList<string> references = ReproducibleTool.GetSourceReferences("release/1.2", "v1.2.3");

        Assert.Equal(new[] { "release/1.2", "v1.2.3", "master", "main" }, references);
    }

    [Fact]
    public void SourceReferencesAreDeduplicated()
    {
        IReadOnlyList<string> references = ReproducibleTool.GetSourceReferences("main", "main");

        Assert.Equal(new[] { "main", "master" }, references);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveSourceDirectoryUsesRepositorySubpath(bool hasRootFile)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string sourceDirectory = Path.Combine(tempDirectory, "src");
        string repositoryRoot = Path.Combine(sourceDirectory, "github-example-repo-main", "repo-main");
        string packageDirectory = Path.Combine(repositoryRoot, "packages", "widget");

        try
        {
            Directory.CreateDirectory(packageDirectory);
            if (hasRootFile)
            {
                File.WriteAllText(Path.Combine(repositoryRoot, "README.md"), "root");
            }
            File.WriteAllText(Path.Combine(packageDirectory, "package.json"), "{}");

            string result = ReproducibleTool.ResolveSourceDirectory(repositoryRoot, "packages/widget");

            Assert.Equal(packageDirectory, result);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("packages/../widget")]
    [InlineData("/outside")]
    [InlineData("\\outside")]
    [InlineData(".")]
    [InlineData("..")]
    public void ResolveSourceDirectoryRejectsTraversal(string subpath)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string sourceDirectory = Path.Combine(tempDirectory, "src");
        string repositoryRoot = Path.Combine(sourceDirectory, "repo-main");

        try
        {
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(Path.Combine(tempDirectory, "outside"));

            Assert.Throws<ArgumentException>(
                () => ReproducibleTool.ResolveSourceDirectory(repositoryRoot, subpath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }

    [Fact]
    public void ResolveSourceDirectoryRejectsMissingSubpathInsteadOfComparingSiblings()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string repositoryRoot = Path.Combine(tempDirectory, "repo-main");
        string siblingDirectory = Path.Combine(repositoryRoot, "packages", "sibling");

        try
        {
            Directory.CreateDirectory(siblingDirectory);
            File.WriteAllText(Path.Combine(repositoryRoot, "README.md"), "root");
            File.WriteAllText(Path.Combine(siblingDirectory, "index.js"), "module.exports = 1;");

            Assert.Throws<DirectoryNotFoundException>(
                () => ReproducibleTool.ResolveSourceDirectory(repositoryRoot, "packages/missing"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }
}
