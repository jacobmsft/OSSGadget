// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.PackageManagers
{
    using PackageUrl;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Identifies the provenance of a source repository candidate. Numeric values are identifiers,
    /// not relative strengths; <see cref="SourceRepositoryConfidencePolicy"/> owns that ordering.
    /// </summary>
    public enum SourceRepositoryEvidence
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
    public sealed record SourceRepositoryEvidenceDescriptor(SourceRepositoryEvidence Evidence)
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

            Dictionary<PackageURL, RankedEvidence> uniqueCandidates = new();
            foreach ((PackageURL repository, SourceRepositoryEvidenceDescriptor evidence) in candidates)
            {
                double score = Rank(package, repository, evidence);
                RankedEvidence current = new(score, GetEvidenceStrength(evidence.Evidence), evidence.IsVerified);
                if (!uniqueCandidates.TryGetValue(repository, out RankedEvidence existing) ||
                    current.CompareTo(existing) > 0)
                {
                    uniqueCandidates[repository] = current;
                }
            }

            return uniqueCandidates
                .OrderByDescending(entry => entry.Value.Score)
                .ThenByDescending(entry => entry.Value.EvidenceStrength)
                .ThenByDescending(entry => entry.Value.IsVerified)
                .ThenBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
                .Select(entry => new KeyValuePair<PackageURL, double>(entry.Key, entry.Value.Score))
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
        public static int GetEvidenceStrength(SourceRepositoryEvidence evidence)
        {
            return evidence switch
            {
                SourceRepositoryEvidence.RequestedVersionAuthoritativeRepository => 800,
                SourceRepositoryEvidence.CuratedMapping => 750,
                SourceRepositoryEvidence.RequestedVersionExplicitSource => 700,
                SourceRepositoryEvidence.LabeledProjectUrl => 600,
                SourceRepositoryEvidence.HistoricalVersionAuthoritativeRepository => 500,
                SourceRepositoryEvidence.HomepageOrDownloadInference => 400,
                SourceRepositoryEvidence.RawMetadataExtraction => 300,
                SourceRepositoryEvidence.NameInference => 200,
                _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown source repository evidence."),
            };
        }

        private static double GetBaseScore(SourceRepositoryEvidence evidence)
        {
            return evidence switch
            {
                SourceRepositoryEvidence.RequestedVersionAuthoritativeRepository => 0.99,
                SourceRepositoryEvidence.CuratedMapping => 0.98,
                SourceRepositoryEvidence.RequestedVersionExplicitSource => 0.95,
                SourceRepositoryEvidence.LabeledProjectUrl => 0.90,
                SourceRepositoryEvidence.HistoricalVersionAuthoritativeRepository => 0.84,
                SourceRepositoryEvidence.HomepageOrDownloadInference => 0.74,
                SourceRepositoryEvidence.RawMetadataExtraction => 0.60,
                SourceRepositoryEvidence.NameInference => 0.48,
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
    }
}
