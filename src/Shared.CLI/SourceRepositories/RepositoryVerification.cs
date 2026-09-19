// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.SourceRepositories;

using PackageUrl;

public enum RepositoryVerificationStatus
{
    NotAttempted,
    Verified,
    Rejected,
    Inconclusive
}

public sealed class RepositoryVerification
{
    public RepositoryVerification(
        PackageURL repository,
        RepositoryVerificationStatus status,
        bool? isFork = null,
        PackageURL? upstreamRepository = null,
        string? failureReason = null)
    {
        Repository = repository;
        Status = status;
        IsFork = isFork;
        UpstreamRepository = upstreamRepository;
        FailureReason = failureReason;
    }

    public PackageURL Repository { get; }

    public RepositoryVerificationStatus Status { get; }

    public bool? IsFork { get; }

    public PackageURL? UpstreamRepository { get; }

    public string? FailureReason { get; }
}
