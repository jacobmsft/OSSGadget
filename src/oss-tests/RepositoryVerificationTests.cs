// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Microsoft.CST.OpenSource.SourceRepositories;
using Moq;
using Octokit;
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

    [Fact]
    public async Task GitHubVerifierTreatsNotFoundAsInconclusive()
    {
        PackageURL repository = new("pkg:github/example/private-repo");
        Mock<IGitHubClient> client = new();
        client.Setup(github => github.Repository.Get(repository.Namespace, repository.Name))
            .ThrowsAsync(new NotFoundException("Not Found", System.Net.HttpStatusCode.NotFound));
        GitHubRepositoryVerifier verifier = new(client.Object);

        RepositoryVerification result = await verifier.VerifyAsync(repository);

        Assert.Equal(RepositoryVerificationStatus.Inconclusive, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Equal(repository, result.Repository);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    public async Task GitHubVerifierPreservesInconclusiveProviderFailures(System.Net.HttpStatusCode statusCode)
    {
        PackageURL repository = new("pkg:github/example/repo");
        Mock<IGitHubClient> client = new();
        client.Setup(github => github.Repository.Get(repository.Namespace, repository.Name))
            .ThrowsAsync(new ApiException("Provider access failed.", statusCode));
        GitHubRepositoryVerifier verifier = new(client.Object);

        RepositoryVerification result = await verifier.VerifyAsync(repository);

        Assert.Equal(RepositoryVerificationStatus.Inconclusive, result.Status);
        Assert.Equal("Provider access failed.", result.FailureReason);
    }

    [Fact]
    public async Task GitHubVerifierDoesNotRequestWhenAlreadyCancelled()
    {
        PackageURL repository = new("pkg:github/example/repo");
        Mock<IGitHubClient> client = new();
        client.Setup(github => github.Repository.Get(repository.Namespace, repository.Name))
            .ReturnsAsync(new Repository());
        GitHubRepositoryVerifier verifier = new(client.Object);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.VerifyAsync(repository, cancellation.Token));

        client.Verify(github => github.Repository.Get(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GitHubVerifierCancelsWhileRequestIsPending()
    {
        PackageURL repository = new("pkg:github/example/repo");
        TaskCompletionSource<Repository> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IGitHubClient> client = new();
        client.Setup(github => github.Repository.Get(repository.Namespace, repository.Name))
            .Returns(response.Task);
        GitHubRepositoryVerifier verifier = new(client.Object);
        using CancellationTokenSource cancellation = new();

        Task<RepositoryVerification> verification = verifier.VerifyAsync(repository, cancellation.Token);
        cancellation.Cancel();

        try
        {
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.False(response.Task.IsCompleted);
        }
        finally
        {
            response.TrySetResult(new Repository());
        }
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
