// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.SourceRepositories;

using PackageUrl;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public sealed class RepositoryVerificationStage
{
    private readonly IRepositoryVerifier repositoryVerifier;

    public RepositoryVerificationStage(IRepositoryVerifier? repositoryVerifier = null)
    {
        // Default checks must not add provider traffic to offline or cached discovery.
        this.repositoryVerifier = repositoryVerifier ?? new NoOpRepositoryVerifier();
    }

    public async Task<IReadOnlyDictionary<PackageURL, RepositoryVerification>> VerifyAsync(
        IEnumerable<PackageURL> repositories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositories);

        Dictionary<PackageURL, RepositoryVerification> results = new();
        foreach (PackageURL repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[repository] = await repositoryVerifier.VerifyAsync(repository, cancellationToken);
        }

        return results;
    }
}
