// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

using PackageUrl;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Represents a possible source repository and the retained evidence for that mapping.
/// </summary>
public sealed class SourceRepositoryCandidate
{
    public SourceRepositoryCandidate(
        SourceRepositoryIdentity identity,
        IEnumerable<SourceRepositoryEvidence> evidence,
        double confidence,
        string? packageVersion = null,
        string? sourceVersion = null,
        SortedDictionary<string, string>? sourceQualifiers = null,
        SourceRepositoryVerificationStatus verificationStatus = SourceRepositoryVerificationStatus.NotAttempted,
        IEnumerable<SourceRepositoryWarning>? warnings = null,
        SourceRepositoryRelationship relationships = SourceRepositoryRelationship.None,
        SourceRepositoryIdentity? upstreamIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);

        Identity = identity;
        Evidence = evidence.Distinct().ToArray();
        Confidence = confidence;
        PackageVersion = packageVersion;
        SourceVersion = sourceVersion;
        SourceQualifiers = sourceQualifiers;
        VerificationStatus = verificationStatus;
        Warnings = (warnings ?? Array.Empty<SourceRepositoryWarning>()).Distinct().ToArray();
        Relationships = relationships;
        UpstreamIdentity = upstreamIdentity;
    }

    public SourceRepositoryIdentity Identity { get; }

    public IReadOnlyList<SourceRepositoryEvidence> Evidence { get; }

    public string? PackageVersion { get; }

    public string? SourceVersion { get; }

    public SortedDictionary<string, string>? SourceQualifiers { get; }

    public double Confidence { get; }

    public SourceRepositoryVerificationStatus VerificationStatus { get; }

    public IReadOnlyList<SourceRepositoryWarning> Warnings { get; }

    public SourceRepositoryRelationship Relationships { get; }

    public SourceRepositoryIdentity? UpstreamIdentity { get; }

    public PackageURL ToPackageUrl()
    {
        PackageURL canonicalPurl = Identity.CanonicalRepositoryPurl;
        return new PackageURL(
            canonicalPurl.Type,
            canonicalPurl.Namespace,
            canonicalPurl.Name,
            SourceVersion,
            SourceQualifiers,
            Identity.Subpath);
    }

    public SourceRepositoryCandidate WithConfidence(double confidence)
    {
        return new SourceRepositoryCandidate(
            Identity,
            Evidence,
            confidence,
            PackageVersion,
            SourceVersion,
            SourceQualifiers,
            VerificationStatus,
            Warnings,
            Relationships,
            UpstreamIdentity);
    }

    public SourceRepositoryCandidate WithVerification(
        SourceRepositoryVerificationStatus verificationStatus,
        IEnumerable<SourceRepositoryWarning>? warnings = null,
        SourceRepositoryRelationship relationships = SourceRepositoryRelationship.None,
        SourceRepositoryIdentity? upstreamIdentity = null)
    {
        return new SourceRepositoryCandidate(
            Identity,
            Evidence,
            Confidence,
            PackageVersion,
            SourceVersion,
            SourceQualifiers,
            verificationStatus,
            Warnings.Concat(warnings ?? Array.Empty<SourceRepositoryWarning>()),
            Relationships | relationships,
            upstreamIdentity ?? UpstreamIdentity);
    }

    internal SourceRepositoryCandidate Merge(SourceRepositoryCandidate other)
    {
        if (!Identity.Equals(other.Identity))
        {
            throw new ArgumentException("Only candidates with the same repository identity can be merged.", nameof(other));
        }

        SourceRepositoryCandidate preferred = other.Confidence > Confidence ? other : this;
        return new SourceRepositoryCandidate(
            Identity,
            Evidence.Concat(other.Evidence),
            Math.Max(Confidence, other.Confidence),
            PackageVersion ?? other.PackageVersion,
            preferred.SourceVersion ?? SourceVersion ?? other.SourceVersion,
            preferred.SourceQualifiers ?? SourceQualifiers ?? other.SourceQualifiers,
            MergeVerificationStatus(VerificationStatus, other.VerificationStatus),
            Warnings.Concat(other.Warnings),
            Relationships | other.Relationships,
            preferred.UpstreamIdentity ?? UpstreamIdentity ?? other.UpstreamIdentity);
    }

    private static SourceRepositoryVerificationStatus MergeVerificationStatus(
        SourceRepositoryVerificationStatus first,
        SourceRepositoryVerificationStatus second)
    {
        if (first == SourceRepositoryVerificationStatus.Verified ||
            second == SourceRepositoryVerificationStatus.Verified)
        {
            return SourceRepositoryVerificationStatus.Verified;
        }

        if (first == SourceRepositoryVerificationStatus.Rejected ||
            second == SourceRepositoryVerificationStatus.Rejected)
        {
            return SourceRepositoryVerificationStatus.Rejected;
        }

        if (first == SourceRepositoryVerificationStatus.Inconclusive ||
            second == SourceRepositoryVerificationStatus.Inconclusive)
        {
            return SourceRepositoryVerificationStatus.Inconclusive;
        }

        return SourceRepositoryVerificationStatus.NotAttempted;
    }
}
