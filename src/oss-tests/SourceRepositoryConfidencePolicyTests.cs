// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

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
        double authoritative = Rank(SourceRepositoryEvidence.RequestedVersionAuthoritativeRepository);
        double explicitSource = Rank(SourceRepositoryEvidence.RequestedVersionExplicitSource);
        double labeledProjectUrl = Rank(SourceRepositoryEvidence.LabeledProjectUrl);
        double historical = Rank(SourceRepositoryEvidence.HistoricalVersionAuthoritativeRepository);
        double homepage = Rank(SourceRepositoryEvidence.HomepageOrDownloadInference);
        double rawMetadata = Rank(SourceRepositoryEvidence.RawMetadataExtraction);
        double nameInference = Rank(SourceRepositoryEvidence.NameInference);

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
            new SourceRepositoryEvidenceDescriptor(SourceRepositoryEvidence.NameInference)
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
        SourceRepositoryEvidenceDescriptor authoritative = new(SourceRepositoryEvidence.RequestedVersionAuthoritativeRepository)
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
                    Repository,
                    new SourceRepositoryEvidenceDescriptor(SourceRepositoryEvidence.RequestedVersionExplicitSource)),
                new KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>(
                    Repository,
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
                    new SourceRepositoryEvidenceDescriptor(SourceRepositoryEvidence.RawMetadataExtraction)),
                new KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>(
                    alpha,
                    new SourceRepositoryEvidenceDescriptor(SourceRepositoryEvidence.RawMetadataExtraction)),
            });

        Assert.Equal(alpha, ranked[0].Key);
    }

    [Fact]
    public void AmbiguousCandidate_RanksBelowEquivalentUnambiguousCandidate()
    {
        double unambiguous = Rank(SourceRepositoryEvidence.LabeledProjectUrl);
        double ambiguous = SourceRepositoryConfidencePolicy.Rank(
            Package,
            Repository,
            new SourceRepositoryEvidenceDescriptor(SourceRepositoryEvidence.LabeledProjectUrl)
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

    private static double Rank(SourceRepositoryEvidence evidence)
    {
        return SourceRepositoryConfidencePolicy.Rank(
            Package,
            Repository,
            new SourceRepositoryEvidenceDescriptor(evidence));
    }
}
