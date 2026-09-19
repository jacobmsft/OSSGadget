// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.PackageManagers
{
    using Microsoft.CST.OpenSource.Helpers;
    using PackageUrl;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Utilities;

    internal class GitHubProjectManager : BaseProjectManager
    {
        /// <summary>
        /// The type of the project manager from the package-url type specifications.
        /// </summary>
        /// <seealso href="https://www.github.com/package-url/purl-spec/blob/master/PURL-TYPES.rst"/>
        public const string Type = "github";

        public override string ManagerType => Type;

        private const string DEFAULT_GITHUB_ENDPOINT = "https://github.com";
        public string ENV_GITHUB_ENDPOINT { get; set; } = DEFAULT_GITHUB_ENDPOINT;

        public GitHubProjectManager(IHttpClientFactory httpClientFactory, string destinationDirectory, TimeSpan? timeout = null) : base(httpClientFactory, destinationDirectory, timeout)
        {
        }

        public GitHubProjectManager(string destinationDirectory, TimeSpan? timeout = null) : base(destinationDirectory, timeout)
        {
        }

        /// <summary>
        ///     Return all github repo patterns in the searchText.
        /// </summary>
        /// <param name="purl"> </param>
        /// <param name="searchText"> </param>
        /// <returns> </returns>
        public static IEnumerable<PackageURL> ExtractGitHubUris(PackageURL purl, string searchText)
        {
            return RepositoryUrlParser.Extract(searchText)
                .Where(packageUrl => packageUrl.Type.Equals(Type, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        ///     Parse a GitHub URL into a PackageURL object.
        /// </summary>
        /// <param name="purl"> Package URL of the package to download. </param>
        /// <returns>PackageURL object for the provided Uri</returns>
        public static PackageURL ParseUri(Uri uri)
        {
            if (uri is null || !RepositoryUrlParser.TryParse(uri.AbsoluteUri, out PackageURL? packageUrl))
            {
                throw new UriFormatException($"'{uri}' is not a supported repository URL.");
            }
            return packageUrl!;
        }

        /// <summary>
        ///     Download one GitHub package and extract it to the target directory.
        /// </summary>
        /// <param name="purl"> Package URL of the package to download. </param>
        /// <returns> n/a </returns>
        public override async Task<IEnumerable<string>> DownloadVersionAsync(PackageURL purl, bool doExtract, bool cached = false)
        {
            List<string> downloadedPaths = new();

            if (purl == null)
            {
                Logger.Debug("'purl' argument must not be null.");
                return downloadedPaths;
            }

            Logger.Trace("DownloadVersion {0}", purl.ToString());

            string? packageNamespace = purl?.Namespace;
            string? packageName = purl?.Name;
            string? packageVersion = purl?.Version;

            if (string.IsNullOrWhiteSpace(packageNamespace) || string.IsNullOrWhiteSpace(packageName)
                || string.IsNullOrWhiteSpace(packageVersion))
            {
                Logger.Debug("Unable to download [{0} {1}]. Both must be defined.", packageNamespace, packageName);
                return downloadedPaths;
            }

            // Cut the .git off the end of the package name.
            if (packageName.EndsWith(".git"))
            {
                packageName = packageName[0..^4];
            }

            try
            {
                string url = $"{ENV_GITHUB_ENDPOINT}/{packageNamespace}/{packageName}";
                string fsNamespace = OssUtilities.NormalizeStringForFileSystem(packageNamespace);
                string fsName = OssUtilities.NormalizeStringForFileSystem(packageName);
                string fsVersion = OssUtilities.NormalizeStringForFileSystem(packageVersion);

                string relativeWorkingDirectory = string.IsNullOrWhiteSpace(packageVersion) ?
                                                $"github-{fsNamespace}-{fsName}" :
                                                $"github-{fsNamespace}-{fsName}-{fsVersion}";
                string extractionPath = Path.Combine(TopLevelExtractionDirectory, relativeWorkingDirectory);

                if (doExtract && Directory.Exists(extractionPath) && cached == true)
                {
                    downloadedPaths.Add(extractionPath);
                    return downloadedPaths;
                }

                // First, try a tag (most likely what we're looking for)
                List<string> archiveUrls = new();
                foreach (string prefix in new[] { "", "v" })
                {
                    archiveUrls.AddRange(new[] {
                        $"{url}/archive/refs/tags/{prefix}{packageVersion}.zip",
                        $"{url}/archive/{prefix}{packageVersion}.zip",
                        $"{url}/archive/refs/heads/{prefix}{packageVersion}.zip",
                    });
                }
                PackageURL purlNoVersion = new(purl!.Type, purl.Namespace, purl.Name, null, purl.Qualifiers, purl.Subpath);
                foreach (string v in await EnumerateVersionsAsync(purlNoVersion))
                {
                    if (Regex.IsMatch(purl.Version!, @"(^|[^\d\.])" + Regex.Escape(v)))
                    {
                        archiveUrls.Add($"{url}/archive/refs/tags/{v}.zip");
                    }
                }

                foreach (string archiveUrl in archiveUrls)
                {
                    Logger.Debug("Attemping to download {0}", archiveUrl);
                    HttpClient httpClient = CreateHttpClient();

                    System.Net.Http.HttpResponseMessage? result = await httpClient.GetAsync(archiveUrl);
                    if (result.IsSuccessStatusCode)
                    {
                        Logger.Debug("Download successful.");
                        if (doExtract)
                        {
                            downloadedPaths.Add(await ArchiveHelper.ExtractArchiveAsync(TopLevelExtractionDirectory, relativeWorkingDirectory, await result.Content.ReadAsStreamAsync(), cached));
                        }
                        else
                        {
                            Directory.CreateDirectory(extractionPath);
                            string targetName = Path.Join(extractionPath, $"{fsVersion}.zip");
                            await File.WriteAllBytesAsync(targetName, await result.Content.ReadAsByteArrayAsync());
                            downloadedPaths.Add(targetName);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Error downloading GitHub package: {0}", ex.Message);
            }
            return downloadedPaths;
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<string>> EnumerateVersionsAsync(PackageURL purl, bool useCache = true, bool includePrerelease = true)
        {
            try
            {
                List<string> versionList = new();
                string githubUrl = GeneratePackageUriString(purl);

                ProcessStartInfo gitLsRemoteStartInfo = new()
                {
                    FileName = "git",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                gitLsRemoteStartInfo.ArgumentList.Add("ls-remote");
                gitLsRemoteStartInfo.ArgumentList.Add("--tags");
                gitLsRemoteStartInfo.ArgumentList.Add("--ref");
                gitLsRemoteStartInfo.ArgumentList.Add(githubUrl);

                Process? gitLsRemoteProcess = Process.Start(gitLsRemoteStartInfo);
                if (gitLsRemoteProcess != null)
                {
                    StreamReader? stdout = gitLsRemoteProcess.StandardOutput;
                    string? outputLine;
                    while ((outputLine = await gitLsRemoteProcess.StandardOutput.ReadLineAsync()) != null)
                    {
                        Match? match = Regex.Match(outputLine, "^.+refs/tags/(.*)$");
                        if (match.Success)
                        {
                            string? tagName = match.Groups[1].Value;
                            Logger.Debug("Adding tag: {0}", tagName);
                            versionList.Add(tagName);
                        }
                    }
                    string stderr = await gitLsRemoteProcess.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        Logger.Warn("Error running 'git', error: {0}", stderr);
                    }
                }
                else
                {
                    Logger.Warn("Unable to run 'git'. Is it installed?");
                }
                return SortVersions(versionList);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.Debug("Unable to enumerate versions (404): {0}", ex.Message);
                return Array.Empty<string>();
            }
            catch (Exception ex)
            {
                Logger.Debug("Unable to enumerate versions: {0}", ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public override async Task<string?> GetMetadataAsync(PackageURL purl, bool useCache = true)
        {
            await Task.CompletedTask;
            return GeneratePackageUriString(purl);
        }

        public override Uri GetPackageAbsoluteUri(PackageURL purl)
        {
            return new Uri(GeneratePackageUriString(purl));
        }

        private string GeneratePackageUriString(PackageURL purl) =>
            $"{ENV_GITHUB_ENDPOINT}/{purl.Namespace}/{purl.Name}";
        
        /// <summary>
        /// Gets if the URL is a GitHub repo URL using the default endpoint.
        /// </summary>
        /// <param name="url">A URL to check if it's from GitHub.</param>
        /// <param name="purl">The variable to set the purl of if it is a GitHub repo.</param>
        /// <returns>If the url is a GitHubRepo.</returns>
        public static bool IsGitHubRepoUrl(string url, out PackageURL? purl)
        {
            Check.NotNull(nameof(url), url);
            purl = null;

            if (RepositoryUrlParser.TryParse(url, out PackageURL? parsed) &&
                parsed?.Type.Equals(Type, StringComparison.OrdinalIgnoreCase) == true)
            {
                purl = parsed;
                return true;
            }
            return false;
        }

    }
}
