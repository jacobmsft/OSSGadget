// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Microsoft.CST.OpenSource.Model.SourceMapping;
using Microsoft.CST.OpenSource.Shared;
using Microsoft.CST.OpenSource.SourceRepositories;
using Microsoft.CST.OpenSource.Contracts;
using Moq;
using PackageUrl;
using CandidateVerificationStatus = Microsoft.CST.OpenSource.Model.SourceMapping.SourceRepositoryVerificationStatus;

public class RepositoryVerificationTests
{
    [Fact]
    public async Task DefaultStageDoesNotRequireProviderAccess()
    {
        PackageURL repository = new("github", "microsoft", "OSSGadget", null, null, "src/Shared");
        RepositoryVerificationStage stage = new();

        IReadOnlyDictionary<PackageURL, RepositoryVerification> results =
            await stage.VerifyAsync(new[] { repository });

        RepositoryVerification result = Assert.Single(results).Value;
        Assert.Equal(RepositoryVerificationStatus.NotAttempted, result.Status);
        Assert.Equal(repository, result.Repository);
    }

    [Fact]
    public async Task StagePreservesVerifierMetadataAndSubpaths()
    {
        PackageURL repository = new("github", "example", "fork", null, null, "packages/widget");
        PackageURL upstream = new("github", "example", "upstream", null, null, repository.Subpath);
        RepositoryVerification expected = new(
            repository,
            RepositoryVerificationStatus.Verified,
            isFork: true,
            upstreamRepository: upstream);
        RepositoryVerificationStage stage = new(new FakeRepositoryVerifier(expected));

        IReadOnlyDictionary<PackageURL, RepositoryVerification> results =
            await stage.VerifyAsync(new[] { repository });

        RepositoryVerification result = Assert.Single(results).Value;
        Assert.Equal(RepositoryVerificationStatus.Verified, result.Status);
        Assert.True(result.IsFork);
        Assert.Equal("packages/widget", result.Repository.Subpath);
        Assert.Equal("packages/widget", result.UpstreamRepository?.Subpath);
        Assert.Equal(upstream, result.UpstreamRepository);
    }

    [Fact]
    public async Task GitHubVerifierTreatsUnsupportedProvidersAsInconclusive()
    {
        PackageURL repository = new("gitlab", "example", "project", null, null, null);
        GitHubRepositoryVerifier verifier = new(string.Empty);

        RepositoryVerification result = await verifier.VerifyAsync(repository);

        Assert.Equal(RepositoryVerificationStatus.Inconclusive, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task PipelineVerificationMapsMetadataAndRemovesRejectedCandidates()
    {
        PackageURL verifiedRepository = new("github", "example", "fork", null, null, "packages/widget");
        PackageURL rejectedRepository = new("github", "example", "missing", null, null, null);
        PackageURL upstream = new("github", "example", "upstream", null, null, verifiedRepository.Subpath);
        OptionalVerificationStage stage = new(new MappingRepositoryVerifier(new Dictionary<string, RepositoryVerification>
        {
            [verifiedRepository.ToString()] = new RepositoryVerification(
                verifiedRepository,
                RepositoryVerificationStatus.Verified,
                isFork: true,
                upstreamRepository: upstream),
            [rejectedRepository.ToString()] = new RepositoryVerification(
                rejectedRepository,
                RepositoryVerificationStatus.Rejected),
        }));
        SourceDiscoveryContext context = new(
            new PackageURL("pkg:npm/widget"),
            new Mock<IBaseProjectManager>().Object);

        IReadOnlyList<SourceRepositoryCandidate> results = await stage.ExecuteAsync(
            context,
            new[]
            {
                CreateCandidate(verifiedRepository),
                CreateCandidate(rejectedRepository),
            });

        SourceRepositoryCandidate candidate = Assert.Single(results);
        Assert.Equal(CandidateVerificationStatus.Verified, candidate.VerificationStatus);
        Assert.True(candidate.Relationships.HasFlag(SourceRepositoryRelationship.Fork));
        Assert.Equal("packages/widget", candidate.UpstreamIdentity?.Subpath);
    }

    [Fact]
    public async Task PipelineVerificationSanitizesInconclusiveFailure()
    {
        PackageURL repository = new("github", "example", "project", null, null, null);
        OptionalVerificationStage stage = new(new FakeRepositoryVerifier(new RepositoryVerification(
            repository,
            RepositoryVerificationStatus.Inconclusive,
            failureReason: "request https://user:secret@github.com/example/project?token=value failed")));
        SourceDiscoveryContext context = new(
            new PackageURL("pkg:npm/project"),
            new Mock<IBaseProjectManager>().Object);

        SourceRepositoryCandidate candidate = Assert.Single(await stage.ExecuteAsync(
            context,
            new[] { CreateCandidate(repository) }));
        SourceRepositoryWarning warning = Assert.Single(candidate.Warnings);

        Assert.Equal(SourceRepositoryWarningKind.OperationalFailure, warning.Kind);
        Assert.DoesNotContain("secret", warning.Detail);
        Assert.DoesNotContain("token=value", warning.Detail);
    }

    private static SourceRepositoryCandidate CreateCandidate(PackageURL repository)
    {
        return new SourceRepositoryCandidate(
            new SourceRepositoryIdentity(repository),
            new[]
            {
                new SourceRepositoryEvidence(
                    SourceRepositoryEvidenceKind.RegistryRepository,
                    "repository.url",
                    repository.ToString())
            },
            1);
    }

    private sealed class FakeRepositoryVerifier : IRepositoryVerifier
    {
        private readonly RepositoryVerification result;

        public FakeRepositoryVerifier(RepositoryVerification result)
        {
            this.result = result;
        }

        public Task<RepositoryVerification> VerifyAsync(
            PackageURL repository,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class MappingRepositoryVerifier : IRepositoryVerifier
    {
        private readonly IReadOnlyDictionary<string, RepositoryVerification> results;

        public MappingRepositoryVerifier(IReadOnlyDictionary<string, RepositoryVerification> results)
        {
            this.results = results;
        }

        public Task<RepositoryVerification> VerifyAsync(
            PackageURL repository,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(results[repository.ToString()]);
        }
    }
}
