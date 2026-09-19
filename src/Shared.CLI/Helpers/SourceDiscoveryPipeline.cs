// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Shared
{
    using Contracts;
    using Microsoft.CST.OpenSource.Model.SourceMapping;
    using PackageManagers;
    using PackageUrl;
    using SourceRepositories;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using CandidateVerificationStatus = Microsoft.CST.OpenSource.Model.SourceMapping.SourceRepositoryVerificationStatus;
    using VerificationStatus = Microsoft.CST.OpenSource.SourceRepositories.RepositoryVerificationStatus;

    /// <summary>
    /// Supplies the package manager and package being resolved to a discovery pipeline stage.
    /// </summary>
    public sealed class SourceDiscoveryContext
    {
        private readonly Lazy<Task<string>> metadata;

        public SourceDiscoveryContext(PackageURL packageUrl, IBaseProjectManager projectManager)
        {
            PackageUrl = packageUrl ?? throw new ArgumentNullException(nameof(packageUrl));
            ProjectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            metadata = new Lazy<Task<string>>(LoadMetadataAsync);
        }

        public PackageURL PackageUrl { get; }

        public IBaseProjectManager ProjectManager { get; }

        public Task<string> GetMetadataAsync() => metadata.Value;

        private async Task<string> LoadMetadataAsync()
        {
            return await ProjectManager.GetMetadataAsync(PackageUrl) ?? string.Empty;
        }
    }

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
        /// Creates the standard pipeline. Verification remains offline by default and uses the
        /// supplied provider verifier when one is explicitly configured.
        /// </summary>
        public static SourceDiscoveryPipeline CreateDefault(IRepositoryVerifier? repositoryVerifier = null) =>
            new(new ISourceDiscoveryStage[]
        {
            new EcosystemDiscoveryStage(),
            new GenericMetadataDiscoveryStage(),
            new CandidateNormalizationStage(),
            new CandidateDeduplicationStage(),
            new OptionalVerificationStage(repositoryVerifier),
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
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public async Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates)
        {
            try
            {
                string metadata = await context.GetMetadataAsync();
                IReadOnlyList<SourceRepositoryCandidate> ecosystemCandidates =
                    await context.ProjectManager.SearchSourceRepositoryCandidatesInPackageMetadataAsync(
                        context.PackageUrl,
                        metadata);

                return candidates.Concat(ecosystemCandidates).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "Ecosystem source discovery failed for {0} ({1}): {2}",
                    context.PackageUrl,
                    ex.GetType().Name,
                    SourceRepositoryEvidence.SanitizeSensitiveText(ex.Message));
                return candidates;
            }
        }
    }

    /// <summary>
    /// Discovers GitHub repository references that ecosystem-specific parsing did not recognize.
    /// </summary>
    public sealed class GenericMetadataDiscoveryStage : ISourceDiscoveryStage
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public async Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates)
        {
            try
            {
                string metadata = await context.GetMetadataAsync();
                IEnumerable<SourceRepositoryCandidate> genericCandidates =
                    BaseProjectManager.ExtractSourceRepositoryCandidatesFromMetadata(
                        context.PackageUrl,
                        metadata);

                return candidates.Concat(genericCandidates).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "Generic source discovery failed for {0} ({1}): {2}",
                    context.PackageUrl,
                    ex.GetType().Name,
                    SourceRepositoryEvidence.SanitizeSensitiveText(ex.Message));
                return candidates;
            }
        }
    }

    /// <summary>
    /// Provides a stable boundary for any normalization that must occur after discovery.
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
    /// Applies optional provider verification. The default verifier is a no-op so ordinary
    /// discovery remains offline.
    /// </summary>
    public sealed class OptionalVerificationStage : ISourceDiscoveryStage
    {
        private readonly RepositoryVerificationStage verificationStage;

        public OptionalVerificationStage(IRepositoryVerifier? repositoryVerifier = null)
        {
            verificationStage = new RepositoryVerificationStage(repositoryVerifier);
        }

        public async Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates)
        {
            IReadOnlyDictionary<PackageURL, RepositoryVerification> verificationResults =
                await verificationStage.VerifyAsync(candidates.Select(candidate => candidate.ToPackageUrl()));
            IReadOnlyDictionary<string, RepositoryVerification> verificationByPurl =
                verificationResults.ToDictionary(
                    result => result.Key.ToString(),
                    result => result.Value,
                    StringComparer.OrdinalIgnoreCase);
            List<SourceRepositoryCandidate> verifiedCandidates = new();

            foreach (SourceRepositoryCandidate candidate in candidates)
            {
                RepositoryVerification verification = verificationByPurl[candidate.ToPackageUrl().ToString()];
                if (verification.Status == VerificationStatus.Rejected)
                {
                    continue;
                }

                CandidateVerificationStatus status = verification.Status switch
                {
                    VerificationStatus.Verified => CandidateVerificationStatus.Verified,
                    VerificationStatus.Inconclusive => CandidateVerificationStatus.Inconclusive,
                    _ => CandidateVerificationStatus.NotAttempted,
                };
                SourceRepositoryRelationship relationships = verification.IsFork == true
                    ? SourceRepositoryRelationship.Fork
                    : SourceRepositoryRelationship.None;
                SourceRepositoryIdentity? upstreamIdentity = verification.UpstreamRepository is null
                    ? null
                    : new SourceRepositoryIdentity(verification.UpstreamRepository);
                SourceRepositoryWarning[] warnings =
                    verification.Status == VerificationStatus.Inconclusive &&
                    !string.IsNullOrWhiteSpace(verification.FailureReason)
                        ? new[]
                        {
                            new SourceRepositoryWarning(
                                SourceRepositoryWarningKind.OperationalFailure,
                                verification.FailureReason)
                        }
                        : Array.Empty<SourceRepositoryWarning>();

                verifiedCandidates.Add(candidate.WithVerification(
                    status,
                    warnings,
                    relationships,
                    upstreamIdentity));
            }

            return verifiedCandidates;
        }
    }

    /// <summary>
    /// Applies the evidence-aware confidence policy to every candidate.
    /// </summary>
    public sealed class CandidateScoringStage : ISourceDiscoveryStage
    {
        public Task<IReadOnlyList<SourceRepositoryCandidate>> ExecuteAsync(
            SourceDiscoveryContext context,
            IReadOnlyList<SourceRepositoryCandidate> candidates) =>
            Task.FromResult(SourceRepositoryConfidencePolicy.RankCandidates(
                context.PackageUrl,
                candidates));
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
