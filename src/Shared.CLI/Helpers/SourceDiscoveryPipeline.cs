// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Shared
{
    using Contracts;
    using Microsoft.CST.OpenSource.Helpers;
    using Microsoft.CST.OpenSource.Model.SourceMapping;
    using PackageManagers;
    using PackageUrl;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// Supplies the package manager and package being resolved to a discovery pipeline stage.
    /// </summary>
    public record SourceDiscoveryContext(PackageURL PackageUrl, IBaseProjectManager ProjectManager);

    /// <summary>
    /// An independently testable source-discovery pipeline stage.
    /// </summary>
    public interface ISourceDiscoveryStage
    {
        /// <summary>
        /// Runs this stage against the candidates produced by preceding stages.
        /// </summary>
        Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates);
    }

    /// <summary>
    /// Executes source discovery as explicit, ordered stages.
    /// </summary>
    public sealed class SourceDiscoveryPipeline
    {
        private readonly IReadOnlyList<ISourceDiscoveryStage> _stages;

        /// <summary>
        /// Initializes a pipeline with the supplied stages. This constructor is primarily intended
        /// for focused stage and orchestration tests.
        /// </summary>
        public SourceDiscoveryPipeline(IEnumerable<ISourceDiscoveryStage> stages)
        {
            _stages = stages?.ToList() ?? throw new ArgumentNullException(nameof(stages));
        }

        /// <summary>
        /// Creates the standard pipeline. Verification is intentionally pass-through until a
        /// provider-verification implementation is introduced.
        /// </summary>
        public static SourceDiscoveryPipeline CreateDefault() => new(new ISourceDiscoveryStage[]
        {
            new EcosystemDiscoveryStage(),
            new GenericMetadataDiscoveryStage(),
            new CandidateNormalizationStage(),
            new CandidateDeduplicationStage(),
            new OptionalVerificationStage(),
            new CandidateScoringStage(),
            new DeterministicRankingStage(),
        });

        /// <summary>
        /// Resolves source candidates through every configured stage in order.
        /// </summary>
        public async Task<IReadOnlyList<SourceRepositoryCandidate>> DiscoverAsync(SourceDiscoveryContext context)
        {
            IReadOnlyList<SourceRepositoryCandidate> candidates = Array.Empty<SourceRepositoryCandidate>();
            foreach (ISourceDiscoveryStage stage in _stages)
            {
                candidates = await stage.ExecuteAsync(context, candidates);
            }

            return candidates;
        }
    }

    /// <summary>
    /// Gets ecosystem-specific candidates from the package manager.
    /// </summary>
    public sealed class EcosystemDiscoveryStage : ISourceDiscoveryStage
    {
        public async Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates)
        {
            IReadOnlyList<SourceRepositoryCandidate> ecosystemCandidates =
                await context.ProjectManager.IdentifySourceRepositoryCandidatesAsync(context.PackageUrl);

            return candidates.Concat(ecosystemCandidates).ToList();
        }
    }

    /// <summary>
    /// Discovers GitHub repository references that ecosystem-specific parsing did not recognize.
    /// </summary>
    public sealed class GenericMetadataDiscoveryStage : ISourceDiscoveryStage
    {
        private const double GenericMetadataScore = 0.8;

        public async Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates)
        {
            string metadata = await context.ProjectManager.GetMetadataAsync(context.PackageUrl) ?? string.Empty;
            IEnumerable<SourceRepositoryCandidate> genericCandidates =
                RepositoryUrlParser.Extract(metadata)
                    .Select(packageUrl => new SourceRepositoryCandidate(
                        new SourceRepositoryIdentity(packageUrl),
                        new[]
                        {
                            new SourceRepositoryEvidence(
                                SourceRepositoryEvidenceKind.MetadataText,
                                nameof(GenericMetadataDiscoveryStage),
                                packageUrl.ToString(),
                                context.PackageUrl.Version)
                        },
                        GenericMetadataScore,
                        context.PackageUrl.Version,
                        packageUrl.Version,
                        packageUrl.Qualifiers));

            return candidates.Concat(genericCandidates).ToList();
        }
    }

    /// <summary>
    /// Provides a stable boundary for candidate normalization as the richer candidate model lands.
    /// </summary>
    public sealed class CandidateNormalizationStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates) =>
            Task.FromResult(candidates);
    }

    /// <summary>
    /// Retains the highest-scoring candidate for each case-insensitive canonical PackageURL.
    /// </summary>
    public sealed class CandidateDeduplicationStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates) =>
            Task.FromResult(new SourceRepositoryCandidateSet(candidates).Candidates);
    }

    /// <summary>
    /// Reserved for optional provider verification. The default branch intentionally performs no
    /// remote verification so existing discovery behavior and network usage are preserved.
    /// </summary>
    public sealed class OptionalVerificationStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates) =>
            Task.FromResult(candidates);
    }

    /// <summary>
    /// Preserves existing confidence scores until the richer candidate model owns scoring.
    /// </summary>
    public sealed class CandidateScoringStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates) =>
            Task.FromResult(candidates);
    }

    /// <summary>
    /// Orders scores descending and resolves ties by canonical PackageURL.
    /// </summary>
    public sealed class DeterministicRankingStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates) =>
            Task.FromResult<IReadOnlyList<SourceRepositoryCandidate>>(candidates
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.ToPackageUrl().ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.ToPackageUrl().ToString(), StringComparer.Ordinal)
                .ToList());
    }
}
