// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.PackageManagers
{
    using Microsoft.CST.OpenSource.Model.SourceMapping;
    using PackageUrl;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Identifies the provenance of a source repository candidate. Numeric values are identifiers,
    /// not relative strengths; <see cref="SourceRepositoryConfidencePolicy"/> owns that ordering.
    /// </summary>
    public enum SourceRepositoryConfidenceEvidence
    {
        RequestedVersionAuthoritativeRepository = 101,
        RequestedVersionExplicitSource = 211,
        LabeledProjectUrl = 307,
        HistoricalVersionAuthoritativeRepository = 401,
        HomepageOrDownloadInference = 503,
        RawMetadataExtraction = 601,
        NameInference = 701,
        CuratedMapping = 809,
    }

    /// <summary>
    /// Narrow evidence adapter used until source candidates carry this information directly.
    /// </summary>
    public sealed record SourceRepositoryEvidenceDescriptor(SourceRepositoryConfidenceEvidence Evidence)
    {
        public bool IsVerified { get; init; }

        public bool IsAmbiguous { get; init; }

        public bool IsFork { get; init; }

        public bool IsUpstream { get; init; }

        public bool IsRootPath { get; init; } = true;

        public bool IsMalformed { get; init; }
    }

    /// <summary>
    /// Deterministically ranks package-to-source mappings without network verification.
    /// </summary>
    public static class SourceRepositoryConfidencePolicy
    {
        /// <summary>
        /// Calculates a bounded confidence score for one package-to-source mapping.
        /// </summary>
        public static double Rank(PackageURL package, PackageURL repository, SourceRepositoryEvidenceDescriptor evidence)
        {
            if (package is null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            if (repository is null)
            {
                throw new ArgumentNullException(nameof(repository));
            }

            if (evidence is null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            double score = GetBaseScore(evidence.Evidence);
            score += evidence.IsVerified ? 0.01 : -0.02;
            score += evidence.IsUpstream ? 0.01 : 0;
            score -= evidence.IsFork ? 0.04 : 0;
            score -= evidence.IsRootPath ? 0 : 0.03;
            score -= evidence.IsAmbiguous ? 0.05 : 0;
            score -= evidence.IsMalformed ? 0.15 : 0;
            score -= NamesMatch(package.Name, repository.Name) ? 0 : 0.05;

            return Math.Clamp(score, 0, 1);
        }

        /// <summary>
        /// Calculates confidence from the retained candidate evidence and verification metadata.
        /// </summary>
        public static double Rank(PackageURL package, SourceRepositoryCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            return Rank(package, candidate.ToPackageUrl(), CreateDescriptor(package, candidate));
        }

        /// <summary>
        /// Ranks and deduplicates candidate evidence. The strongest evidence for a repository wins.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<PackageURL, double>> RankCandidates(
            PackageURL package,
            IEnumerable<KeyValuePair<PackageURL, SourceRepositoryEvidenceDescriptor>> candidates)
        {
            if (package is null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            if (candidates is null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            Dictionary<SourceRepositoryIdentity, RankedCandidate> uniqueCandidates = new();
            foreach ((PackageURL repository, SourceRepositoryEvidenceDescriptor evidence) in candidates)
            {
                double score = Rank(package, repository, evidence);
                RankedEvidence current = new(score, GetEvidenceStrength(evidence.Evidence), evidence.IsVerified);
                SourceRepositoryIdentity identity = new(repository);
                if (!uniqueCandidates.TryGetValue(identity, out RankedCandidate existing) ||
                    current.CompareTo(existing.Evidence) > 0)
                {
                    uniqueCandidates[identity] = new RankedCandidate(repository, current);
                }
            }

            return uniqueCandidates
                .Select(entry => entry.Value)
                .OrderByDescending(candidate => candidate.Evidence.Score)
                .ThenByDescending(candidate => candidate.Evidence.EvidenceStrength)
                .ThenByDescending(candidate => candidate.Evidence.IsVerified)
                .ThenBy(candidate => candidate.Repository.ToString(), StringComparer.Ordinal)
                .Select(candidate => new KeyValuePair<PackageURL, double>(
                    candidate.Repository,
                    candidate.Evidence.Score))
                .ToList();
        }

        /// <summary>
        /// Scores and deterministically orders evidence-rich candidates.
        /// </summary>
        public static IReadOnlyList<SourceRepositoryCandidate> RankCandidates(
            PackageURL package,
            IEnumerable<SourceRepositoryCandidate> candidates)
        {
            ArgumentNullException.ThrowIfNull(package);
            ArgumentNullException.ThrowIfNull(candidates);

            return new SourceRepositoryCandidateSet(candidates)
                .Candidates
                .Select(candidate => new
                {
                    Candidate = candidate.WithConfidence(Rank(package, candidate)),
                    Descriptor = CreateDescriptor(package, candidate),
                })
                .OrderByDescending(entry => entry.Candidate.Confidence)
                .ThenByDescending(entry => GetEvidenceStrength(entry.Descriptor.Evidence))
                .ThenByDescending(entry => entry.Descriptor.IsVerified)
                .ThenBy(entry => entry.Candidate.ToPackageUrl().ToString(), StringComparer.Ordinal)
                .Select(entry => entry.Candidate)
                .ToList();
        }

        /// <summary>
        /// Orders the compatibility mapping deterministically for consumers that only receive scores.
        /// </summary>
        public static IEnumerable<KeyValuePair<PackageURL, double>> OrderMappings(
            IEnumerable<KeyValuePair<PackageURL, double>> mappings)
        {
            if (mappings is null)
            {
                throw new ArgumentNullException(nameof(mappings));
            }

            return mappings
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.ToString(), StringComparer.Ordinal);
        }

        /// <summary>
        /// Returns the policy-owned ordering of evidence categories.
        /// </summary>
        public static int GetEvidenceStrength(SourceRepositoryConfidenceEvidence evidence)
        {
            return evidence switch
            {
                SourceRepositoryConfidenceEvidence.RequestedVersionAuthoritativeRepository => 800,
                SourceRepositoryConfidenceEvidence.CuratedMapping => 750,
                SourceRepositoryConfidenceEvidence.RequestedVersionExplicitSource => 700,
                SourceRepositoryConfidenceEvidence.LabeledProjectUrl => 600,
                SourceRepositoryConfidenceEvidence.HistoricalVersionAuthoritativeRepository => 500,
                SourceRepositoryConfidenceEvidence.HomepageOrDownloadInference => 400,
                SourceRepositoryConfidenceEvidence.RawMetadataExtraction => 300,
                SourceRepositoryConfidenceEvidence.NameInference => 200,
                _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown source repository evidence."),
            };
        }

        private static double GetBaseScore(SourceRepositoryConfidenceEvidence evidence)
        {
            return evidence switch
            {
                SourceRepositoryConfidenceEvidence.RequestedVersionAuthoritativeRepository => 0.99,
                SourceRepositoryConfidenceEvidence.CuratedMapping => 0.98,
                SourceRepositoryConfidenceEvidence.RequestedVersionExplicitSource => 0.95,
                SourceRepositoryConfidenceEvidence.LabeledProjectUrl => 0.90,
                SourceRepositoryConfidenceEvidence.HistoricalVersionAuthoritativeRepository => 0.84,
                SourceRepositoryConfidenceEvidence.HomepageOrDownloadInference => 0.74,
                SourceRepositoryConfidenceEvidence.RawMetadataExtraction => 0.60,
                SourceRepositoryConfidenceEvidence.NameInference => 0.48,
                _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown source repository evidence."),
            };
        }

        private static bool NamesMatch(string? packageName, string? repositoryName)
        {
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(repositoryName))
            {
                return false;
            }

            string normalizedPackageName = NormalizeName(packageName);
            string normalizedRepositoryName = NormalizeName(repositoryName);
            if (normalizedPackageName.Length == 0 || normalizedRepositoryName.Length == 0)
            {
                return false;
            }

            return normalizedPackageName.Equals(normalizedRepositoryName, StringComparison.Ordinal) ||
                normalizedRepositoryName.Contains(normalizedPackageName, StringComparison.Ordinal) ||
                normalizedPackageName.Contains(normalizedRepositoryName, StringComparison.Ordinal);
        }

        private static string NormalizeName(string name)
        {
            return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static SourceRepositoryEvidenceDescriptor CreateDescriptor(
            PackageURL package,
            SourceRepositoryCandidate candidate)
        {
            SourceRepositoryConfidenceEvidence strongestEvidence = candidate.Evidence
                .Select(evidence => MapEvidence(package, evidence))
                .OrderByDescending(GetEvidenceStrength)
                .FirstOrDefault(SourceRepositoryConfidenceEvidence.RawMetadataExtraction);

            return new SourceRepositoryEvidenceDescriptor(strongestEvidence)
            {
                IsVerified = candidate.VerificationStatus == SourceRepositoryVerificationStatus.Verified,
                IsAmbiguous = candidate.Warnings.Any(warning =>
                    warning.Kind == SourceRepositoryWarningKind.Ambiguous),
                IsFork = candidate.Relationships.HasFlag(SourceRepositoryRelationship.Fork),
                IsUpstream = candidate.Relationships.HasFlag(SourceRepositoryRelationship.Upstream),
                IsRootPath = string.IsNullOrWhiteSpace(candidate.Identity.Subpath),
                IsMalformed = candidate.Warnings.Any(warning =>
                    warning.Kind == SourceRepositoryWarningKind.Malformed),
            };
        }

        private static SourceRepositoryConfidenceEvidence MapEvidence(
            PackageURL package,
            Microsoft.CST.OpenSource.Model.SourceMapping.SourceRepositoryEvidence evidence)
        {
            bool isHistorical =
                !string.IsNullOrWhiteSpace(package.Version) &&
                !string.IsNullOrWhiteSpace(evidence.MetadataPackageVersion) &&
                !string.Equals(
                    package.Version,
                    evidence.MetadataPackageVersion,
                    StringComparison.OrdinalIgnoreCase);

            return evidence.Kind switch
            {
                SourceRepositoryEvidenceKind.RegistryRepository =>
                    isHistorical
                        ? SourceRepositoryConfidenceEvidence.HistoricalVersionAuthoritativeRepository
                        : SourceRepositoryConfidenceEvidence.RequestedVersionAuthoritativeRepository,
                SourceRepositoryEvidenceKind.RegistrySource =>
                    isHistorical
                        ? SourceRepositoryConfidenceEvidence.HistoricalVersionAuthoritativeRepository
                        : SourceRepositoryConfidenceEvidence.RequestedVersionExplicitSource,
                SourceRepositoryEvidenceKind.RegistryProjectUrl =>
                    SourceRepositoryConfidenceEvidence.LabeledProjectUrl,
                SourceRepositoryEvidenceKind.Homepage or SourceRepositoryEvidenceKind.DownloadUrl =>
                    SourceRepositoryConfidenceEvidence.HomepageOrDownloadInference,
                SourceRepositoryEvidenceKind.RepositorySearch or SourceRepositoryEvidenceKind.NameInference =>
                    SourceRepositoryConfidenceEvidence.NameInference,
                SourceRepositoryEvidenceKind.BuiltInMapping =>
                    SourceRepositoryConfidenceEvidence.CuratedMapping,
                _ => SourceRepositoryConfidenceEvidence.RawMetadataExtraction,
            };
        }

        private readonly record struct RankedEvidence(double Score, int EvidenceStrength, bool IsVerified) : IComparable<RankedEvidence>
        {
            public int CompareTo(RankedEvidence other)
            {
                int comparison = EvidenceStrength.CompareTo(other.EvidenceStrength);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = Score.CompareTo(other.Score);
                return comparison != 0 ? comparison : IsVerified.CompareTo(other.IsVerified);
            }
        }

        private readonly record struct RankedCandidate(
            PackageURL Repository,
            RankedEvidence Evidence);
    }
}
