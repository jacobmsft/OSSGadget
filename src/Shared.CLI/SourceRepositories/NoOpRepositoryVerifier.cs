// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.SourceRepositories;

using PackageUrl;
using System.Threading;
using System.Threading.Tasks;

public sealed class NoOpRepositoryVerifier : IRepositoryVerifier
{
    public Task<RepositoryVerification> VerifyAsync(
        PackageURL repository,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepositoryVerification(
            repository,
            RepositoryVerificationStatus.NotAttempted));
    }
}
