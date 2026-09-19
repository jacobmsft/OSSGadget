// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

using PackageUrl;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Owns deterministic candidate deduplication and conversion to the legacy source mapping shape.
/// </summary>
public sealed class SourceRepositoryCandidateSet
{
    public SourceRepositoryCandidateSet(IEnumerable<SourceRepositoryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<SourceRepositoryCandidate> deduplicated = new();
        Dictionary<SourceRepositoryIdentity, int> candidateIndexes = new();
        foreach (SourceRepositoryCandidate candidate in candidates)
        {
            if (candidateIndexes.TryGetValue(candidate.Identity, out int index))
            {
                deduplicated[index] = deduplicated[index].Merge(candidate);
            }
            else
            {
                candidateIndexes.Add(candidate.Identity, deduplicated.Count);
                deduplicated.Add(candidate);
            }
        }

        Candidates = deduplicated;
    }

    public IReadOnlyList<SourceRepositoryCandidate> Candidates { get; }

    public Dictionary<PackageURL, double> ToCompatibilityDictionary()
    {
        Dictionary<PackageURL, double> result = new();
        foreach (SourceRepositoryCandidate candidate in Candidates)
        {
            result[candidate.ToPackageUrl()] = candidate.Confidence;
        }
        return result;
    }

    public static SourceRepositoryCandidateSet FromCompatibilityDictionary(
        PackageURL packagePurl,
        IEnumerable<KeyValuePair<PackageURL, double>> mappings,
        SourceRepositoryEvidenceKind evidenceKind,
        string? sourceField)
    {
        ArgumentNullException.ThrowIfNull(packagePurl);
        ArgumentNullException.ThrowIfNull(mappings);

        IEnumerable<SourceRepositoryCandidate> candidates = mappings.Select(mapping =>
            new SourceRepositoryCandidate(
                new SourceRepositoryIdentity(mapping.Key),
                new[]
                {
                    new SourceRepositoryEvidence(
                        evidenceKind,
                        sourceField,
                        mapping.Key.ToString(),
                        packagePurl.Version)
                },
                mapping.Value,
                packagePurl.Version,
                mapping.Key.Version,
                mapping.Key.Qualifiers));

        return new SourceRepositoryCandidateSet(candidates);
    }
}
