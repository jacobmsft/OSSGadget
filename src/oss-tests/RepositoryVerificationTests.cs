// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Microsoft.CST.OpenSource.SourceRepositories;
using PackageUrl;

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
}
