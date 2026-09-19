// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.SourceRepositories;

using PackageUrl;
using System.Threading;
using System.Threading.Tasks;

public interface IRepositoryVerifier
{
    Task<RepositoryVerification> VerifyAsync(PackageURL repository, CancellationToken cancellationToken = default);
}
