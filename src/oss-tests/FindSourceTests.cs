// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Contracts;
using PackageManagers;
using PackageUrl;
using Microsoft.CodeAnalysis.Sarif;
using Microsoft.CST.OpenSource.Model.SourceMapping;
using Microsoft.CST.OpenSource.Shared;
using Moq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class FindSourceTests
{
    [Fact]
    public async Task SourceDiscoveryPipeline_ExecutesStagesInOrderAndAggregatesCandidates()
    {
        List<string> executionOrder = new();
        SourceDiscoveryPipeline pipeline = new(new ISourceDiscoveryStage[]
        {
            new RecordingDiscoveryStage("ecosystem", executionOrder,
                CreateCandidate("pkg:github/contoso/ecosystem", 0.9, "ecosystem")),
            new RecordingDiscoveryStage("generic", executionOrder,
                CreateCandidate("pkg:github/contoso/generic", 0.8, "generic")),
        });

        IReadOnlyList<SourceRepositoryCandidate> candidates = await pipeline.DiscoverAsync(CreateSourceDiscoveryContext());

        Assert.Equal(new[] { "ecosystem", "generic" }, executionOrder);
        Assert.Equal(new[]
        {
            "pkg:github/contoso/ecosystem",
            "pkg:github/contoso/generic",
        }, candidates.Select(candidate => candidate.ToPackageUrl().ToString()));
    }

    [Fact]
    public async Task CandidateDeduplicationStage_RetainsHighestScoringCandidate()
    {
        PackageURL duplicate = new("pkg:github/contoso/repository");
        CandidateDeduplicationStage stage = new();

        IReadOnlyList<SourceRepositoryCandidate> candidates = await stage.ExecuteAsync(
            CreateSourceDiscoveryContext(),
            new[]
            {
                CreateCandidate(duplicate.ToString(), 0.5, "ecosystem"),
                CreateCandidate(duplicate.ToString(), 0.9, "generic"),
            });

        SourceRepositoryCandidate candidate = Assert.Single(candidates);
        Assert.Equal(0.9, candidate.Confidence);
        Assert.Equal(2, candidate.Evidence.Count);
    }

    [Fact]
    public async Task SourceDiscoveryPipeline_ReturnsEmptyResults()
    {
        SourceDiscoveryPipeline pipeline = new(Array.Empty<ISourceDiscoveryStage>());

        IReadOnlyList<SourceRepositoryCandidate> candidates = await pipeline.DiscoverAsync(CreateSourceDiscoveryContext());

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task RepoSearch_AggregatesEcosystemAndGenericMetadataCandidates()
    {
        PackageURL package = new("pkg:npm/example");
        PackageURL ecosystemRepository = new("pkg:github/contoso/ecosystem");
        Mock<IBaseProjectManager> manager = new();
        manager.Setup(m => m.SearchSourceRepositoryCandidatesInPackageMetadataAsync(
                package,
                "https://github.com/contoso/generic"))
            .ReturnsAsync(new[] { CreateCandidate(ecosystemRepository.ToString(), 0.9, "ecosystem") });
        manager.Setup(m => m.GetMetadataAsync(package, true))
            .ReturnsAsync("https://github.com/contoso/generic");
        Mock<IProjectManagerFactory> factory = new();
        factory.Setup(f => f.CreateProjectManager(package, ".", null)).Returns(manager.Object);

        RepoSearch search = new(factory.Object);
        Dictionary<PackageURL, double> candidates = await search.ResolvePackageLibraryAsync(package);

        Assert.Equal(new[]
        {
            "pkg:github/contoso/ecosystem",
            "pkg:github/contoso/generic",
        }, candidates.Keys.Select(candidate => candidate.ToString()));
    }

    [Fact]
    public async Task RepoSearch_ContinuesGenericDiscoveryWhenEcosystemParserFails()
    {
        PackageURL package = new("pkg:maven/contoso/example@1.0.0");
        const string metadata =
            "<project><scm>malformed https://github.com/contoso/example";
        Mock<IBaseProjectManager> manager = new();
        manager.Setup(m => m.SearchSourceRepositoryCandidatesInPackageMetadataAsync(
                package,
                metadata))
            .ThrowsAsync(new InvalidOperationException("Malformed ecosystem metadata."));
        manager.Setup(m => m.GetMetadataAsync(package, true))
            .ReturnsAsync(metadata);
        Mock<IProjectManagerFactory> factory = new();
        factory.Setup(f => f.CreateProjectManager(package, ".", null)).Returns(manager.Object);

        RepoSearch search = new(factory.Object);
        Dictionary<PackageURL, double> candidates = await search.ResolvePackageLibraryAsync(package);

        Assert.Contains(
            candidates.Keys,
            candidate => candidate.ToString() == "pkg:github/contoso/example");
    }

    [Theory]
    [InlineData(
        "pkg:github/acme/project@main#src",
        "https://github.com/acme/project/tree/main/src")]
    [InlineData(
        "pkg:gitlab/acme/project@release#packages/example",
        "https://gitlab.com/acme/project/-/tree/release/packages/example")]
    [InlineData(
        "pkg:generic/git.example.com/acme/project",
        "https://git.example.com/acme/project")]
    public void RepositoryDisplayUrlUsesCandidateProvider(string repository, string expected)
    {
        Assert.Equal(
            expected,
            FindSourceTool.GetRepositoryDisplayUrl(new PackageURL(repository)));
    }

    [Fact]
    public async Task DeterministicRankingStage_SortsByScoreThenPackageUrl()
    {
        DeterministicRankingStage stage = new();

        IReadOnlyList<SourceRepositoryCandidate> candidates = await stage.ExecuteAsync(
            CreateSourceDiscoveryContext(),
            new[]
            {
                CreateCandidate("pkg:github/contoso/zulu", 0.8, "test"),
                CreateCandidate("pkg:github/contoso/bravo", 0.9, "test"),
                CreateCandidate("pkg:github/contoso/alpha", 0.8, "test"),
            });

        Assert.Equal(new[]
        {
            "pkg:github/contoso/bravo",
            "pkg:github/contoso/alpha",
            "pkg:github/contoso/zulu",
        }, candidates.Select(candidate => candidate.ToPackageUrl().ToString()));
    }

    [Theory]
    [InlineData("pkg:npm/md5", "https://github.com/pvorb/node-md5")]
    public async Task Check_Sarif(string purl, string targetResult)
    {
        // for initialization
        FindSourceTool tool = new();

        RepoSearch searchTool = new(new ProjectManagerFactory());
        Dictionary<PackageURL, double>? results = await searchTool.ResolvePackageLibraryAsync(new PackageURL(purl));

        List<Result> sarifResults = new();
        foreach (KeyValuePair<PackageURL, double> result in results)
        {
            double confidence = result.Value * 100.0;

            Result sarifResult = new()
            {
                Message = new Message()
                {
                    Text = $"https://github.com/{result.Key.Namespace}/{result.Key.Name}"
                },
                Kind = ResultKind.Informational,
                Level = FailureLevel.None,
                Rank = confidence,
                Locations = SarifOutputBuilder.BuildPurlLocation(new PackageURL(purl))
            };

            sarifResults.Add(sarifResult);
        }

        IOutputBuilder outputBuilder = OutputBuilderFactory.CreateOutputBuilder("sarifv2");
        outputBuilder.AppendOutput(sarifResults);
        string sarifJSON = outputBuilder.GetOutput();
        SarifLog? sarif = JsonConvert.DeserializeObject<SarifLog>(sarifJSON);
        Assert.NotNull(sarif);

        Run? sarifRun = sarif.Runs.FirstOrDefault();
        Assert.NotNull(sarifRun?.Tool.Driver.Name);

        // make sure atleast one of the result repos match the actual one
        bool found = false;
        if (sarifRun != null)
        {
            foreach (Result? result in sarifRun.Results)
            {
                if (result.Message.Text == targetResult)
                {
                    found = true;
                }
            }
        }
        Assert.True(found);
    }

    [Theory]
    [InlineData("pkg:npm/hjkfashfkjafhakfjsa", "")]
    [InlineData("pkg:pypi/hjkfashfkjafhakfjsa", "")]
    [InlineData("pkg:nuget/hjkfashfkjafhakfjsa", "")]
    public async Task FindSource_NonExistentPackage(string purl, string _)
    {
        // for initialization
        FindSourceTool tool = new();

        RepoSearch searchTool = new(new ProjectManagerFactory());
        Dictionary<PackageURL, double>? results = await searchTool.ResolvePackageLibraryAsync(new PackageURL(purl));
        Assert.True(results.Count() == 0, $"Result {results} obtained from non-existent {purl}");
    }

    [Theory]
    [InlineData("pkg:npm/md5", "pkg:github/pvorb/node-md5")]
    [InlineData("pkg:pypi/moment", "pkg:github/zachwill/moment")]
    [InlineData("pkg:nuget/Newtonsoft.Json", "pkg:github/jamesnk/newtonsoft.json")]
    [InlineData("pkg:pypi/django", "pkg:github/django/django")]
    [InlineData("pkg:pypi/pylint", "pkg:github/pylint-dev/pylint")]
    [InlineData("pkg:pypi/arrow", "pkg:github/arrow-py/arrow")]
    public async Task FindSource_Success(string purl, string targetResult)
    {
        // for initialization
        FindSourceTool tool = new();

        RepoSearch searchTool = new(new ProjectManagerFactory());
        Dictionary<PackageURL, double>? results = await searchTool.ResolvePackageLibraryAsync(new PackageURL(purl));
        PackageURL? targetPurl = new(targetResult);
        bool success = false;

        foreach (KeyValuePair<PackageURL, double> resultEntry in results)
        {
            if (resultEntry.Key.ToString().Equals(targetPurl.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                success = true;
            }
        }
        Assert.True(success, $"Result {targetResult} not found from {purl}");
    }

    private static SourceDiscoveryContext CreateSourceDiscoveryContext() =>
        new(new PackageURL("pkg:npm/example"), new Mock<IBaseProjectManager>().Object);

    private static SourceRepositoryCandidate CreateCandidate(string purl, double confidence, string sourceField) =>
        new(
            new SourceRepositoryIdentity(new PackageURL(purl)),
            new[]
            {
                new SourceRepositoryEvidence(
                    SourceRepositoryEvidenceKind.Unknown,
                    sourceField,
                    purl)
            },
            confidence);

    private sealed class RecordingDiscoveryStage : ISourceDiscoveryStage
    {
        private readonly string _name;
        private readonly List<string> _executionOrder;
        private readonly SourceRepositoryCandidate _candidate;

        public RecordingDiscoveryStage(
            string name,
            List<string> executionOrder,
            SourceRepositoryCandidate candidate)
        {
            _name = name;
            _executionOrder = executionOrder;
            _candidate = candidate;
        }

        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates)
        {
            _executionOrder.Add(_name);
            return Task.FromResult<IReadOnlyList<SourceRepositoryCandidate>>(candidates.Append(_candidate).ToList());
        }
    }
}