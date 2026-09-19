// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Shared
{
    using Contracts;
    using PackageManagers;
    using PackageUrl;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// A minimal candidate used only while source discovery is migrated to the richer candidate
    /// model. Merge this seam with the dedicated candidate-model worktree before adding candidate
    /// evidence, provider verification, or ecosystem-specific scoring.
    /// </summary>
    public record SourceDiscoveryCandidate(PackageURL PackageUrl, double Score, string DiscoveryStage);

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
        Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates);
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
        public async Task<IReadOnlyList<SourceDiscoveryCandidate>> DiscoverAsync(SourceDiscoveryContext context)
        {
            IReadOnlyList<SourceDiscoveryCandidate> candidates = Array.Empty<SourceDiscoveryCandidate>();
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
        public async Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates)
        {
            Dictionary<PackageURL, double> ecosystemCandidates =
                await context.ProjectManager.IdentifySourceRepositoryAsync(context.PackageUrl);

            return candidates.Concat(ecosystemCandidates.Select(candidate =>
                new SourceDiscoveryCandidate(candidate.Key, candidate.Value, nameof(EcosystemDiscoveryStage)))).ToList();
        }
    }

    /// <summary>
    /// Discovers GitHub repository references that ecosystem-specific parsing did not recognize.
    /// </summary>
    public sealed class GenericMetadataDiscoveryStage : ISourceDiscoveryStage
    {
        private const double GenericMetadataScore = 0.8;

        public async Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates)
        {
            string metadata = await context.ProjectManager.GetMetadataAsync(context.PackageUrl) ?? string.Empty;
            IEnumerable<SourceDiscoveryCandidate> genericCandidates =
                BaseProjectManager.ExtractGitHubPackageURLs(metadata)
                    .Select(packageUrl => new SourceDiscoveryCandidate(
                        packageUrl,
                        GenericMetadataScore,
                        nameof(GenericMetadataDiscoveryStage)));

            return candidates.Concat(genericCandidates).ToList();
        }
    }

    /// <summary>
    /// Provides a stable boundary for candidate normalization as the richer candidate model lands.
    /// </summary>
    public sealed class CandidateNormalizationStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates) =>
            Task.FromResult<IReadOnlyList<SourceDiscoveryCandidate>>(candidates
                .Select(candidate => new SourceDiscoveryCandidate(
                    candidate.PackageUrl,
                    candidate.Score,
                    candidate.DiscoveryStage))
                .ToList());
    }

    /// <summary>
    /// Retains the highest-scoring candidate for each case-insensitive canonical PackageURL.
    /// </summary>
    public sealed class CandidateDeduplicationStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates) =>
            Task.FromResult<IReadOnlyList<SourceDiscoveryCandidate>>(candidates
                .GroupBy(candidate => candidate.PackageUrl.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                .ToList());
    }

    /// <summary>
    /// Reserved for optional provider verification. The default branch intentionally performs no
    /// remote verification so existing discovery behavior and network usage are preserved.
    /// </summary>
    public sealed class OptionalVerificationStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates) =>
            Task.FromResult(candidates);
    }

    /// <summary>
    /// Preserves existing confidence scores until the richer candidate model owns scoring.
    /// </summary>
    public sealed class CandidateScoringStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates) =>
            Task.FromResult(candidates);
    }

    /// <summary>
    /// Orders scores descending and resolves ties by canonical PackageURL.
    /// </summary>
    public sealed class DeterministicRankingStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceDiscoveryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceDiscoveryCandidate> candidates) =>
            Task.FromResult<IReadOnlyList<SourceDiscoveryCandidate>>(candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.PackageUrl.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.PackageUrl.ToString(), StringComparer.Ordinal)
                .ToList());
    }
}
