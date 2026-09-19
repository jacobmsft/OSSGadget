// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests.Model.SourceMapping;

using Microsoft.CST.OpenSource.PackageManagers;
using Microsoft.CST.OpenSource.Shared;
using Microsoft.CST.OpenSource.Model.SourceMapping;
using PackageUrl;

public class SourceRepositoryCandidateSetTests
{
    [Fact]
    public void IdentityExcludesVersionsAndNormalizesSubpath()
    {
        SourceRepositoryIdentity first = new(
            new PackageURL("github", "Microsoft", "OSSGadget", "v1.0.0", null, "/src//Shared/"));
        SourceRepositoryIdentity second = new(
            new PackageURL("github", "microsoft", "ossgadget", "main", null, "src\\Shared"));

        Assert.Equal(first, second);
        Assert.Null(first.CanonicalRepositoryPurl.Version);
        Assert.Null(first.CanonicalRepositoryPurl.Subpath);
        Assert.Equal("github.com", first.Host);
        Assert.Equal("src/Shared", first.Subpath);
    }

    [Fact]
    public void EvidenceSanitizesCredentials()
    {
        SourceRepositoryEvidence evidence = new(
            SourceRepositoryEvidenceKind.RegistryRepository,
            "repository.url",
            "git+https://" + string.Concat("user", ":", "secret", "@") + "github.com/microsoft/ossgadget?access_token=value",
            "1.2.3");

        Assert.Equal(
            "git+https://github.com/microsoft/ossgadget?access_token=<redacted>",
            evidence.RawValue);
        Assert.Equal("1.2.3", evidence.MetadataPackageVersion);
    }

    [Fact]
    public void IdentityRetainsExplicitNonForgeHostWithoutFabricatingOne()
    {
        PackageURL genericRepository = new(
            "generic",
            "owner",
            "repository",
            null,
            new SortedDictionary<string, string>
            {
                ["vcs_url"] = "https://git.example.com/owner/repository"
            },
            null);

        SourceRepositoryIdentity withUri = new(
            genericRepository,
            new Uri("https://git.example.com/owner/repository"));
        SourceRepositoryIdentity withoutUri = new(genericRepository);

        Assert.Equal("git.example.com", withUri.Host);
        Assert.Equal("https://git.example.com/owner/repository", withUri.CanonicalRepositoryUri?.ToString());
        Assert.Null(withoutUri.CanonicalRepositoryUri);
        Assert.Empty(withoutUri.Host);
        Assert.Null(withUri.CanonicalRepositoryPurl.Qualifiers);
    }

    [Fact]
    public void DeduplicationRetainsEvidenceWarningsAndHighestConfidence()
    {
        SourceRepositoryIdentity firstIdentity = new(
            new PackageURL("github", "microsoft", "ossgadget", "v1", null, "src/shared"));
        SourceRepositoryIdentity secondIdentity = new(
            new PackageURL("github", "Microsoft", "OSSGadget", "main", null, "/src/shared/"));

        SourceRepositoryCandidate first = new(
            firstIdentity,
            new[]
            {
                new SourceRepositoryEvidence(
                    SourceRepositoryEvidenceKind.Homepage,
                    "home_page",
                    "https://github.com/microsoft/ossgadget")
            },
            0.8,
            "1.0.0",
            "v1",
            warnings: new[]
            {
                new SourceRepositoryWarning(
                    SourceRepositoryWarningKind.Ambiguous,
                    "homepage was used")
            });
        SourceRepositoryCandidate second = new(
            secondIdentity,
            new[]
            {
                new SourceRepositoryEvidence(
                    SourceRepositoryEvidenceKind.RegistryRepository,
                    "repository.url",
                    "https://github.com/Microsoft/OSSGadget")
            },
            1.0,
            "1.0.0",
            null,
            null,
            SourceRepositoryVerificationStatus.Verified,
            new[]
            {
                new SourceRepositoryWarning(
                    SourceRepositoryWarningKind.RootPath,
                    "repository field was used")
            },
            SourceRepositoryRelationship.Upstream);

        SourceRepositoryCandidateSet set = new(new[] { first, second });

        SourceRepositoryCandidate candidate = Assert.Single(set.Candidates);
        Assert.Equal(1.0, candidate.Confidence);
        Assert.Equal("v1", candidate.SourceVersion);
        Assert.Equal(SourceRepositoryVerificationStatus.Verified, candidate.VerificationStatus);
        Assert.Equal(SourceRepositoryRelationship.Upstream, candidate.Relationships);
        Assert.Equal(2, candidate.Evidence.Count);
        Assert.Equal(2, candidate.Warnings.Count);
    }

    [Fact]
    public void CompatibilityConversionPreservesScoresAndDiscoveryOrder()
    {
        PackageURL packagePurl = new("npm", null, "example", "2.0.0", null, null);
        Dictionary<PackageURL, double> legacyMappings = new()
        {
            [new PackageURL("github", "owner", "first", "v2", null, "packages/example")] = 0.75,
            [new PackageURL("github", "owner", "second", null, null, null)] = 1.0
        };

        SourceRepositoryCandidateSet set = SourceRepositoryCandidateSet.FromCompatibilityDictionary(
            packagePurl,
            legacyMappings,
            SourceRepositoryEvidenceKind.RegistryRepository,
            "repository.url");
        Dictionary<PackageURL, double> converted = set.ToCompatibilityDictionary();

        Assert.Equal(
            legacyMappings.Select(mapping => mapping.Key.ToString()),
            converted.Select(mapping => mapping.Key.ToString()));
        Assert.Equal(legacyMappings.Select(mapping => mapping.Value), converted.Select(mapping => mapping.Value));
        Assert.All(set.Candidates, candidate =>
        {
            SourceRepositoryEvidence evidence = Assert.Single(candidate.Evidence);
            Assert.Equal("2.0.0", candidate.PackageVersion);
            Assert.Equal("2.0.0", evidence.MetadataPackageVersion);
        });
    }

    [Fact]
    public async Task SourceMappingFlowRetainsEvidenceAndPreservesLegacyResult()
    {
        PackageURL packagePurl = new("npm", null, "example", "2.0.0", null, null);
        ProjectManagerFactory factory = new(new Dictionary<string, ProjectManagerFactory.ConstructProjectManager>
        {
            ["npm"] = (_, _, _) => new TestProjectManager()
        });

        RepoSearch repoSearch = new(factory);
        SourceRepositoryCandidate candidate = Assert.Single(
            await repoSearch.ResolvePackageLibraryCandidatesAsync(packagePurl));
        SourceRepositoryEvidence evidence = Assert.Single(candidate.Evidence);

        Assert.Equal(SourceRepositoryEvidenceKind.RegistryRepository, evidence.Kind);
        Assert.Equal("repository.url", evidence.SourceField);
        Assert.Equal("https://github.com/owner/repository", evidence.RawValue);
        Assert.Equal("2.0.0", candidate.PackageVersion);

        FindSourceTool findSourceTool = new(factory);
        KeyValuePair<PackageURL, double> compatibilityResult = Assert.Single(
            await findSourceTool.FindSourceAsync(packagePurl));

        Assert.Equal("pkg:github/owner/repository", compatibilityResult.Key.ToString());
        Assert.Equal(0.9, compatibilityResult.Value);
    }

    private sealed class TestProjectManager : BaseProjectManager
    {
        public override string ManagerType => "npm";

        public override Task<IEnumerable<string>> EnumerateVersionsAsync(
            PackageURL purl,
            bool useCache = true,
            bool includePrerelease = true)
        {
            return Task.FromResult<IEnumerable<string>>(new[] { "2.0.0" });
        }

        public override Task<string?> GetMetadataAsync(PackageURL purl, bool useCache = true)
        {
            return Task.FromResult<string?>("{}");
        }

        protected override Task<IReadOnlyList<SourceRepositoryCandidate>> SearchSourceRepositoryCandidatesInPackageMetadataAsync(
            PackageURL purl,
            string metadata)
        {
            PackageURL repositoryPurl = new("github", "owner", "repository", null, null, null);
            return Task.FromResult<IReadOnlyList<SourceRepositoryCandidate>>(new[]
            {
                new SourceRepositoryCandidate(
                    new SourceRepositoryIdentity(repositoryPurl),
                    new[]
                    {
                        new SourceRepositoryEvidence(
                            SourceRepositoryEvidenceKind.RegistryRepository,
                            "repository.url",
                            "https://" + string.Concat("user", ":", "secret", "@") + "github.com/owner/repository",
                            purl.Version)
                    },
                    0.9,
                    purl.Version)
            });
        }
    }
}
