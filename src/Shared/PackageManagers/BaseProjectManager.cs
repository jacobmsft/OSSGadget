// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.PackageManagers
{
    using Contracts;
    using F23.StringSimilarity;
    using Helpers;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.CST.OpenSource.Model;
    using Model.Enums;
    using Model.PackageExistence;
    using Model.SourceMapping;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Utilities;
    using Version = SemanticVersioning.Version;
    using PackageUrl;
    using System.IO;
    using SharpCompress;

    public abstract class BaseProjectManager : IBaseProjectManager
    {
        /// <inheritdoc />
        public abstract string ManagerType { get; }

        /// <inheritdoc/>
        public TimeSpan? Timeout { get; } = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseProjectManager"/> class.
        /// </summary>
        public BaseProjectManager(IHttpClientFactory httpClientFactory, string destinationDirectory = ".", TimeSpan? timeout = null)
        {
            EnvironmentHelper.OverrideEnvironmentVariables(this);
            Options = new Dictionary<string, object>();
            TopLevelExtractionDirectory = destinationDirectory;
            HttpClientFactory = httpClientFactory;
            Timeout = timeout;
        }

        public BaseProjectManager(string destinationDirectory = ".", TimeSpan? timeout = null) : this(new DefaultHttpClientFactory(), destinationDirectory, timeout)
        {
        }

        /// <inheritdoc />
        public Dictionary<string, object> Options { get; private set; }

        /// <inheritdoc />
        public string TopLevelExtractionDirectory { get; init; }

        /// <inheritdoc />
        public IHttpClientFactory HttpClientFactory { get; }

        /// <summary>
        /// Extracts GitHub URLs from a given piece of text.
        /// </summary>
        /// <param name="content">text to analyze</param>
        /// <returns>PackageURLs (type=GitHub) located in the text.</returns>
        public static IEnumerable<PackageURL> ExtractGitHubPackageURLs(string content)
        {
            Logger.Trace("ExtractGitHubPackageURLs({0})", content?.Take(30));
            return RepositoryUrlParser.Extract(content)
                .Where(packageUrl => packageUrl.Type.Equals("github", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Retrieves HTTP content from a given URI.
        /// </summary>
        /// <param name="client">The <see cref="HttpClient"/> to make the request on.</param>
        /// <param name="uri">The URI to load.</param>
        /// <param name="useCache">If cache should be used.</param>
        /// <param name="neverThrow">If an exception gets raised, should it not be thrown.</param>
        /// <returns>The string response from the http result content.</returns>
        public static async Task<string?> GetHttpStringCache(HttpClient client, string uri, bool useCache = true, bool neverThrow = false)
        {
            Logger.Trace("GetHttpStringCache({0}, {1})", uri, useCache);

            string? resultString = null;

            try
            {
                if (useCache)
                {
                    lock (DataCache)
                    {
                        if (DataCache.TryGetValue(uri, out string s))
                        {
                            return s;
                        }
                    }
                }

                HttpResponseMessage result = await client.GetAsync(uri);
                result.EnsureSuccessStatusCode(); // Don't cache error codes.
                resultString = await result.Content.ReadAsStringAsync();
                long contentLength = resultString.Length;

                if (useCache)
                {
                    lock (DataCache)
                    {
                        MemoryCacheEntryOptions mce = new() { 
                            Size = contentLength,
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                        };
                        DataCache.Set<string>(uri, resultString, mce);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Always propagate cancellation, regardless of neverThrow.
                throw;
            }
            catch (Exception ex)
            {
                if (!neverThrow)
                {
                    throw;
                }
                Logger.Debug(ex, "Suppressed error fetching {0}: {1}", OssUtilities.RedactUrlCredentials(uri), ex.Message);
            }

            return resultString;
        }

        /// <summary>
        /// Checks <see cref="GetHttpStringCache(HttpClient, string, bool, bool)"/> to see if the package exists.
        /// </summary>
        /// <param name="client">The <see cref="HttpClient"/> to make the request on.</param>
        /// <param name="url">The URL to check.</param>
        /// <param name="useCache">If cache should be used.</param>
        /// <returns>true if the package exists.</returns>
        internal static async Task<bool> CheckHttpCacheForPackage(HttpClient client, string url, bool useCache = true)
        {
            Logger.Trace("CheckHttpCacheForPackage {0}", url);
            try
            {
                // GetHttpStringCache throws an exception if it has trouble finding the package.
                _ = await GetHttpStringCache(client, url, useCache);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                string redactedUrl = OssUtilities.RedactUrlCredentials(url) ?? url;
                if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
                {
                    Logger.Trace("Package not found at: {0}", redactedUrl);
                    return false;
                }
                Logger.Debug("Unable to check if package exists at {0}: {1}", redactedUrl, e.Message);
            }
            return false;
        }

        /// <summary>
        /// Checks <see cref="GetJsonCache(HttpClient, string, bool)"/> to see if the package exists.
        /// </summary>
        /// <param name="client">The <see cref="HttpClient"/> to make the request on.</param>
        /// <param name="url">The URL to check.</param>
        /// <param name="useCache">If cache should be used.</param>
        /// <param name="jsonParsingOption">Any special json parsing rules.</param>
        /// <returns>true if the package exists.</returns>
        internal static async Task<bool> CheckJsonCacheForPackage(HttpClient client, string url, bool useCache = true, JsonParsingOption jsonParsingOption = JsonParsingOption.None)
        {
            Logger.Trace("CheckJsonCacheForPackage {0}", url);
            try
            {
                // GetJsonCache throws an exception if it has trouble finding the package.
                _ = await GetJsonCache(client, url, useCache, jsonParsingOption);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                string redactedUrl = OssUtilities.RedactUrlCredentials(url) ?? url;
                if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
                {
                    Logger.Trace("Package not found at: {0}", redactedUrl);
                    return false;
                }
                Logger.Debug("Unable to check if package exists at {0}: {1}", redactedUrl, e.Message);
            }
            return false;
        }

        /// <summary>
        /// Retrieves JSON content from a given URI.
        /// </summary>
        /// <param name="client">The <see cref="HttpClient"/> to make the request on.</param>
        /// <param name="uri">URI to load.</param>
        /// <param name="useCache">If cache should be used. If false will make a direct WebClient request.</param>
        /// <param name="jsonParsingOption">Any special json parsing rules.</param>
        /// <returns>Content, as a JsonDocument, possibly from cache.</returns>
        public static async Task<JsonDocument> GetJsonCache(HttpClient client, string uri, bool useCache = true, JsonParsingOption jsonParsingOption = JsonParsingOption.None)
        {
            Logger.Trace("GetJsonCache({0}, {1})", uri, useCache);
            var cacheKey = $"{uri}/json";
            if (useCache)
            {
                lock (DataCache)
                {
                    if (DataCache.TryGetValue(cacheKey, out JsonDocument js))
                    {
                        return js;
                    }
                }
            }
            Logger.Trace("Loading Uri...");
            HttpResponseMessage result = await client.GetAsync(uri);
            result.EnsureSuccessStatusCode(); // Don't cache error codes.
            long contentLength = 0;
            JsonDocument doc;

            switch (jsonParsingOption)
            {
                case JsonParsingOption.NotInArrayNotCsv:
                    string data = await result.Content.ReadAsStringAsync();
                    contentLength = data.Length;
                    data = Regex.Replace(data, @"\r\n?|\n", ",");
                    data = $"[{data}]";

                    doc = JsonDocument.Parse(data, new JsonDocumentOptions()
                    {
                        AllowTrailingCommas = true,
                    });
                    break;
                default:
                    Stream responseStream = await result.Content.ReadAsStreamAsync();
                    contentLength = responseStream.Length;
                    doc = await JsonDocument.ParseAsync(responseStream);
                    break;
            }
            
            if (useCache)
            {
                lock (DataCache)
                {
                    MemoryCacheEntryOptions? mce = new() 
                    {
                        Size = contentLength,
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                    };
                    DataCache.Set<JsonDocument>(cacheKey, doc, mce);
                }
            }

            return doc;
        }

        /// <summary>
        /// Sorts the versions of a package in descending order.
        /// </summary>
        /// <param name="versionList">The available list of versions on a package.</param>
        /// <returns>The sorted list of versions.</returns>
        public static IEnumerable<string> SortVersions(IEnumerable<string> versionList)
        {
            List<string> versionListEnumerated = versionList.ToList();
            if (!versionListEnumerated.Any())
            {
                return Array.Empty<string>();
            }

            // Split Versions
            List<List<string>> versionPartsList = versionListEnumerated.Select(VersionComparer.Parse).ToList();
            versionPartsList.Sort(new VersionComparer());
            return versionPartsList.Select(s => string.Join("", s));
        }

        /// <inheritdoc />
        public virtual Task<IEnumerable<string>> DownloadVersionAsync(PackageURL purl, bool doExtract, bool cached = false)
        {
            throw new NotImplementedException("BaseProjectManager does not implement DownloadVersionAsync.");
        }

        /// <inheritdoc />
        public abstract Task<IEnumerable<string>> EnumerateVersionsAsync(PackageURL purl, bool useCache = true, bool includePrerelease = true);

        /// <inheritdoc />
        public Version? GetLatestVersion(JsonDocument metadata)
        {
            List<Version> versions = GetVersions(metadata);
            return GetLatestVersion(versions);
        }

        /// <inheritdoc />
        public virtual async Task<bool> PackageExistsAsync(PackageURL purl, bool useCache = true)
        {
            Logger.Trace("PackageExists {0}", purl?.ToString());
            if (purl is null)
            {
                Logger.Trace("Provided PackageURL was null.");
                throw new ArgumentNullException(nameof(purl), "Provided PackageURL was null.");
            }
            return (await EnumerateVersionsAsync(purl, useCache)).Any();
        }
        
        /// <inheritdoc />
        public virtual async Task<IPackageExistence> DetailedPackageExistsAsync(PackageURL purl, bool useCache = true)
        {
            bool exists = await PackageExistsAsync(purl, useCache);

            if (exists)
            {
                return new PackageExists();
            }

            return new PackageNotFound();
        }

        /// <inheritdoc />
        public virtual async Task<bool> PackageVersionExistsAsync(PackageURL purl, bool useCache = true)
        {
            Logger.Trace("PackageExists {0}", purl?.ToString());
            if (purl is null)
            {
                Logger.Trace("Provided PackageURL was null.");
                return false;
            }

            if(purl.Version.IsBlank())
            {
                Logger.Trace("Provided PackageURL version was null or blank.");
                return false;
            }

            return (await EnumerateVersionsAsync(purl, useCache)).Contains(purl.Version);
        }
        
        /// <inheritdoc />
        public virtual async Task<IPackageExistence> DetailedPackageVersionExistsAsync(PackageURL purl, bool useCache = true)
        {
            bool exists = await PackageVersionExistsAsync(purl, useCache);

            if (exists)
            {
                return new PackageVersionExists();
            }

            return new PackageVersionNotFound();
        }

        /// <summary>
        /// Static overload for getting the latest version.
        /// </summary>
        /// <param name="versions">The list of versions.</param>
        /// <returns>The latest version from the list.</returns>
        public static Version? GetLatestVersion(List<Version> versions)
        {
            if (versions.Any())
            {
                Version? maxVersion = versions.Max();
                return maxVersion;
            }
            return null;
        }

        /// <inheritdoc />
        public abstract Task<string?> GetMetadataAsync(PackageURL purl, bool useCache = true);

        /// <inheritdoc />
        public virtual Uri? GetPackageAbsoluteUri(PackageURL purl)
        {
            throw new NotImplementedException($"{GetType().Name} does not implement GetPackageAbsoluteUri.");
        }

        /// <inheritdoc />
        public virtual Task<PackageMetadata?> GetPackageMetadataAsync(PackageURL purl, bool includePrerelease = false, bool useCache = true, bool includeRepositoryMetadata = true)
        {
            string typeName = GetType().Name;
            throw new NotImplementedException($"{typeName} does not implement GetPackageMetadata.");
        }

        /// <inheritdoc />
        public virtual JsonElement? GetVersionElement(JsonDocument contentJSON, Version version)
        {
            string typeName = GetType().Name;
            throw new NotImplementedException($"{typeName} does not implement GetVersionElement.");
        }

            /// <inheritdoc />
        public virtual JsonElement? GetVersionElement(JsonDocument contentJSON, string version)
        {
            string typeName = GetType().Name;
            throw new NotImplementedException($"{typeName} does not implement GetVersionElement.");
        }

        /// <inheritdoc />
        public virtual List<Version> GetVersions(JsonDocument? metadata)
        {
            string typeName = GetType().Name;
            throw new NotImplementedException($"{typeName} does not implement GetVersions.");
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SourceRepositoryCandidate>> IdentifySourceRepositoryCandidatesAsync(
            PackageURL purl,
            bool useCache = true)
        {
            Logger.Trace("IdentifySourceRepositoryCandidatesAsync({0})", purl);

            string rawMetadataString;
            try
            {
                rawMetadataString = await GetMetadataAsync(purl, useCache) ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                // Always propagate cancellation to the caller.
                throw;
            }
            catch (Exception ex)
            {
                // Individual managers are expected to handle their own transient/HTTP failures and
                // return null; this is a defensive fallback in case one doesn't.
                Logger.Trace(ex, "Error fetching package metadata for {0}: {1}", purl, ex.Message);
                rawMetadataString = string.Empty;
            }

            // Check the specific PackageManager-specific implementation first
            try
            {
                IReadOnlyList<SourceRepositoryCandidate> packageManagerCandidates =
                    await SearchSourceRepositoryCandidatesInPackageMetadataAsync(purl, rawMetadataString);

                // Return now if we've succeeded
                if (packageManagerCandidates.Any())
                {
                    return new SourceRepositoryCandidateSet(packageManagerCandidates).Candidates;
                }
            }
            catch (OperationCanceledException)
            {
                // Always propagate cancellation to the caller.
                throw;
            }
            catch (JsonException ex)
            {
                Logger.Trace(ex, "Malformed metadata encountered while searching package metadata for {0}: {1}", purl, ex.Message);
            }
            catch (HttpRequestException ex)
            {
                Logger.Trace(ex, "HTTP error while searching package metadata for {0}: {1}", purl, ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Trace(ex, "Error searching package metadata for {0}: {1}", purl, ex.Message);
            }

            // Fall back to searching metadata for canonical repository URLs.
            SourceRepositoryCandidateSet metadataTextCandidates =
                SourceRepositoryCandidateSet.FromCompatibilityDictionary(
                    purl,
                    ExtractRankedSourceRepositories(purl, rawMetadataString),
                    SourceRepositoryEvidenceKind.MetadataText,
                    "packageMetadata");

            return metadataTextCandidates.Candidates;
        }

        /// <inheritdoc />
        public async Task<Dictionary<PackageURL, double>> IdentifySourceRepositoryAsync(PackageURL purl, bool useCache = true)
        {
            IReadOnlyList<SourceRepositoryCandidate> candidates =
                await IdentifySourceRepositoryCandidatesAsync(purl, useCache);
            return new SourceRepositoryCandidateSet(candidates).ToCompatibilityDictionary();
        }

        /// <inheritdoc />
        public virtual async Task<DateTime?> GetPublishedAtUtcAsync(PackageURL purl, bool useCache = true)
        {
            Check.NotNull(nameof(purl.Version), purl.Version);
            DateTime? uploadTime = (await GetPackageMetadataAsync(purl, useCache, includeRepositoryMetadata: false))?.UploadTime?.ToUniversalTime();
            return uploadTime;
        }

        /// <summary>
        /// Protected memory cache to make subsequent loads of the same URL fast and transparent.
        /// </summary>
        protected static readonly MemoryCache DataCache = new(
            new MemoryCacheOptions
            {
                SizeLimit = 1024 * 1024 * 100
            }
        );

        /// <summary>
        /// Logger for each of the subclasses
        /// </summary>
        protected static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Ranks GitHub URLs extracted from otherwise unstructured metadata.
        /// </summary>
        /// <param name="purl">the package</param>
        /// <param name="rawMetadataString">metadata of the package</param>
        /// <returns>Possible candidates of the package/empty dictionary</returns>
        protected static Dictionary<PackageURL, double> ExtractRankedSourceRepositories(PackageURL purl, string rawMetadataString)
        {
            Logger.Trace("ExtractRankedSourceRepositories({0})", purl);
            Dictionary<PackageURL, double> sourceRepositoryMap = new();

            if (purl == null || string.IsNullOrWhiteSpace(rawMetadataString))
            {
                return sourceRepositoryMap;   // Empty
            }

            IEnumerable<PackageURL> sourceUrls = RepositoryUrlParser.Extract(rawMetadataString);
            foreach (PackageURL sourceUrl in sourceUrls)
            {
                SourceRepositoryEvidenceDescriptor evidence = new(SourceRepositoryEvidence.RawMetadataExtraction)
                {
                    IsRootPath = string.IsNullOrEmpty(sourceUrl.Subpath),
                    IsMalformed = string.IsNullOrWhiteSpace(sourceUrl.Namespace) || string.IsNullOrWhiteSpace(sourceUrl.Name),
                };
                sourceRepositoryMap[sourceUrl] = SourceRepositoryConfidencePolicy.Rank(purl, sourceUrl, evidence);
            }

            return sourceRepositoryMap;
        }

        /// <summary>
        /// Implemented by all package managers to search the metadata, and either return a
        /// successful result for the package repository, or return a null in case of
        /// failure/nothing to do.
        /// </summary>
        /// <returns></returns>
        protected virtual Task<Dictionary<PackageURL, double>> SearchRepoUrlsInPackageMetadata(PackageURL purl, string metadata)
        {
            return Task.FromResult(new Dictionary<PackageURL, double>());
        }

        /// <summary>
        /// Searches package metadata for source repository candidates.
        /// </summary>
        /// <remarks>
        /// Package managers can override this seam to retain exact fields and raw values. The default
        /// adapter preserves existing dictionary-based extractors during migration.
        /// </remarks>
        protected virtual async Task<IReadOnlyList<SourceRepositoryCandidate>> SearchSourceRepositoryCandidatesInPackageMetadataAsync(
            PackageURL purl,
            string metadata)
        {
            Dictionary<PackageURL, double> mappings = await SearchRepoUrlsInPackageMetadata(purl, metadata);
            return SourceRepositoryCandidateSet.FromCompatibilityDictionary(
                purl,
                mappings,
                SourceRepositoryEvidenceKind.RegistryRepository,
                $"{GetType().Name}.packageMetadata").Candidates;
        }

        protected HttpClient CreateHttpClient()
        {
            HttpClient client = HttpClientFactory.CreateClient(GetType().Name);
            if (Timeout.HasValue)
            {
                client.Timeout = Timeout.Value;
            }
            return client;
        }
    }
}
