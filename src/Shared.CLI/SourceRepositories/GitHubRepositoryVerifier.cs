// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.SourceRepositories;

using Octokit;
using PackageUrl;
using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class GitHubRepositoryVerifier : IRepositoryVerifier
{
    private readonly IGitHubClient client;

    public GitHubRepositoryVerifier(IGitHubClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public GitHubRepositoryVerifier(string? accessToken = null)
    {
        GitHubClient githubClient = new(new ProductHeaderValue("OSSGadget"));
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            githubClient.Credentials = new Credentials(accessToken);
        }

        client = githubClient;
    }

    public async Task<RepositoryVerification> VerifyAsync(
        PackageURL repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (!repository.Type.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(repository.Namespace) ||
            string.IsNullOrWhiteSpace(repository.Name))
        {
            return new RepositoryVerification(
                repository,
                RepositoryVerificationStatus.Inconclusive,
                failureReason: "Only GitHub repository Package URLs can be verified.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Octokit.Repository githubRepository = await client.Repository.Get(repository.Namespace, repository.Name);
            PackageURL? upstreamRepository = githubRepository.Parent is null
                ? null
                : new PackageURL(
                    "github",
                    githubRepository.Parent.Owner.Login,
                    githubRepository.Parent.Name,
                    null,
                    null,
                    repository.Subpath);

            return new RepositoryVerification(
                repository,
                RepositoryVerificationStatus.Verified,
                githubRepository.Fork,
                upstreamRepository);
        }
        catch (NotFoundException)
        {
            return new RepositoryVerification(repository, RepositoryVerificationStatus.Rejected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RepositoryVerification(
                repository,
                RepositoryVerificationStatus.Inconclusive,
                failureReason: ex.Message);
        }
    }
}
