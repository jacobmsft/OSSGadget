// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Microsoft.CST.OpenSource.Model.SourceMapping;
using Microsoft.CST.OpenSource.PackageManagers;
using PackageUrl;
using System.Collections.Generic;
using System.Linq;

public class SourceRepositoryConfidencePolicyTests
{
    private static readonly PackageURL Package = new("pkg:npm/widgets");
    private static readonly PackageURL Repository = new("pkg:github/example/widgets");

    [Fact]
    public void EvidenceOrdering_IsExplicitAndStable()
    {
        double authoritative = Rank(SourceRepositoryConfidenceEvidence.RequestedVersionAuthoritativeRepository);
        double explicitSource = Rank(SourceRepositoryConfidenceEvidence.RequestedVersionExplicitSource);
        double labeledProjectUrl = Rank(SourceRepositoryConfidenceEvidence.LabeledProjectUrl);
        double historical = Rank(SourceRepositoryConfidenceEvidence.HistoricalVersionAuthoritativeRepository);
        double homepage = Rank(SourceRepositoryConfidenceEvidence.HomepageOrDownloadInference);
        double rawMetadata = Rank(SourceRepositoryConfidenceEvidence.RawMetadataExtraction);
        double nameInference = Rank(SourceRepositoryConfidenceEvidence.NameInference);

        Assert.True(authoritative > explicitSource);
        Assert.True(explicitSource > labeledProjectUrl);
        Assert.True(labeledProjectUrl > historical);
        Assert.True(historical > homepage);
        Assert.True(homepage > rawMetadata);
        Assert.True(rawMetadata > nameInference);
    }

    [Fact]
    public void Scores_AreBounded_WhenAllPenaltiesApply()
    {
        double score = SourceRepositoryConfidencePolicy.Rank(
            Package,
            Repository,
            new SourceRepositoryEvidenceDescriptor(SourceRepositoryConfidenceEvidence.NameInference)
            {
                IsAmbiguous = true,
                IsFork = true,
                IsMalformed = true,
                IsRootPath = false,
            });

        Assert.InRange(score, 0, 1);
    }

    [Fact]
    public void RankCandidates_KeepsStrongestEvidenceForDuplicateRepository()
    {
        SourceRepositoryEvidenceDescriptor authoritative = new(SourceRepositoryConfidenceEvidence.RequestedVersionAuthoritativeRepository)
        {
            IsAmbiguous = true,
            IsFork = true,
            IsMalformed = true,
            IsRootPath = false,
        };

        IReadOnlyList<KeyValuePair<PackageURL, double>> ranked = SourceRepositoryConfidencePolicy.RankCandidates(
            Package,
            new[]
            {
                new KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>(
                    new PackageURL(Repository.ToString()),
                    new SourceRepositoryEvidenceDescriptor(SourceRepositoryConfidenceEvidence.RequestedVersionExplicitSource)),
                new KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>(
                    new PackageURL(Repository.ToString()),
                    authoritative),
            });

        Assert.Single(ranked);
        Assert.Equal(SourceRepositoryConfidencePolicy.Rank(Package, Repository, authoritative), ranked[0].Value);
    }

    [Fact]
    public void RankCandidates_BreaksEqualScoresByCanonicalPackageUrl()
    {
        PackageURL alpha = new("pkg:github/alpha/widgets");
        PackageURL zulu = new("pkg:github/zulu/widgets");

        IReadOnlyList<KeyValuePair<PackageURL, double>> ranked = SourceRepositoryConfidencePolicy.RankCandidates(
            Package,
            new[]
            {
                new KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>(
                    zulu,
                    new SourceRepositoryEvidenceDescriptor(SourceRepositoryConfidenceEvidence.RawMetadataExtraction)),
                new KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>(
                    alpha,
                    new SourceRepositoryEvidenceDescriptor(SourceRepositoryConfidenceEvidence.RawMetadataExtraction)),
            });

        Assert.Equal(alpha, ranked[0].Key);
    }

    [Fact]
    public void AmbiguousCandidate_RanksBelowEquivalentUnambiguousCandidate()
    {
        double unambiguous = Rank(SourceRepositoryConfidenceEvidence.LabeledProjectUrl);
        double ambiguous = SourceRepositoryConfidencePolicy.Rank(
            Package,
            Repository,
            new SourceRepositoryEvidenceDescriptor(SourceRepositoryConfidenceEvidence.LabeledProjectUrl)
            {
                IsAmbiguous = true,
            });

        Assert.True(unambiguous > ambiguous);
    }

    [Fact]
    public void OrderMappings_SelectsHighestConfidenceThenCanonicalPackageUrl()
    {
        PackageURL alpha = new("pkg:github/alpha/widgets");
        PackageURL zulu = new("pkg:github/zulu/widgets");
        List<KeyValuePair<PackageURL, double>> ordered = SourceRepositoryConfidencePolicy.OrderMappings(
            new Dictionary<PackageURL, double>
            {
                [zulu] = 0.70,
                [alpha] = 0.70,
                [Repository] = 0.90,
            }).ToList();

        Assert.Equal(Repository, ordered[0].Key);
        Assert.Equal(alpha, ordered[1].Key);
    }

    [Fact]
    public void EvidenceRichRankingUsesVersionAndVerificationMetadata()
    {
        PackageURL versionedPackage = new("pkg:npm/widgets@2.0.0");
        SourceRepositoryCandidate requested = CreateCandidate(
            SourceRepositoryEvidenceKind.RegistryRepository,
            metadataVersion: "2.0.0",
            SourceRepositoryVerificationStatus.Verified);
        SourceRepositoryCandidate historical = CreateCandidate(
            SourceRepositoryEvidenceKind.RegistryRepository,
            metadataVersion: "1.0.0",
            SourceRepositoryVerificationStatus.NotAttempted);

        double requestedScore = SourceRepositoryConfidencePolicy.Rank(versionedPackage, requested);
        double historicalScore = SourceRepositoryConfidencePolicy.Rank(versionedPackage, historical);

        Assert.True(requestedScore > historicalScore);
    }

    private static double Rank(SourceRepositoryConfidenceEvidence evidence)
    {
        return SourceRepositoryConfidencePolicy.Rank(
            Package,
            Repository,
            new SourceRepositoryEvidenceDescriptor(evidence));
    }

    private static SourceRepositoryCandidate CreateCandidate(
        SourceRepositoryEvidenceKind evidenceKind,
        string metadataVersion,
        SourceRepositoryVerificationStatus verificationStatus)
    {
        return new SourceRepositoryCandidate(
            new SourceRepositoryIdentity(Repository),
            new[]
            {
                new SourceRepositoryEvidence(
                    evidenceKind,
                    "repository.url",
                    Repository.ToString(),
                    metadataVersion)
            },
            0,
            metadataVersion,
            verificationStatus: verificationStatus);
    }
}
