// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests.ProjectManagerTests;

using Microsoft.CST.OpenSource.PackageActions;
using Microsoft.CST.OpenSource.PackageManagers;
using Microsoft.CST.OpenSource.Model.SourceMapping;
using Microsoft.CST.OpenSource.Shared;
using Microsoft.CST.OpenSource.Utilities;
using Moq;
using oss;
using PackageUrl;
using RichardSzalay.MockHttp;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

/// <summary>
/// Deterministic tests covering improvement 7 of the package-to-source redesign:
/// resilient, observable source-discovery failure handling in <see cref="BaseProjectManager"/>,
/// the NPM/PyPI/NuGet metadata extraction paths, <see cref="RepoSearch"/>, and related helpers.
/// These tests intentionally avoid real network access and are self-contained/deterministic.
/// </summary>
public class SourceDiscoveryResilienceTests
{
    private static IHttpClientFactory CreateHttpFactory(MockHttpMessageHandler mockHttp)
    {
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(mockHttp.ToHttpClient());
        return mockFactory.Object;
    }

    /// <summary>
    /// Invokes the protected/private "SearchRepoUrlsInPackageMetadata(PackageURL, string)" overload
    /// via reflection so tests can supply arbitrary (including malformed) metadata JSON directly,
    /// without needing to route it through a mocked HTTP metadata fetch.
    /// </summary>
    private static async Task<Dictionary<PackageURL, double>> InvokeSearchRepoUrls(BaseProjectManager manager, PackageURL purl, string metadata)
    {
        MethodInfo? method = manager.GetType().GetMethod(
            "SearchRepoUrlsInPackageMetadata",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(PackageURL), typeof(string) },
            modifiers: null);

        Assert.NotNull(method);

        Task<Dictionary<PackageURL, double>> task = (Task<Dictionary<PackageURL, double>>)method!.Invoke(manager, new object[] { purl, metadata })!;
        return await task;
    }

    #region OssUtilities.RedactUrlCredentials

    [Theory]
    [InlineData("https://user:pass@example.com/feed/index.json", "https://example.com/feed/index.json")]
    [InlineData("https://token@example.com/feed/index.json", "https://example.com/feed/index.json")]
    [InlineData("https://example.com/feed/index.json", "https://example.com/feed/index.json")]
    [InlineData(null, null)]
    [InlineData("not a url", "not a url")]
    public void RedactUrlCredentials_RemovesUserInfo_WhenPresent(string? input, string? expected)
    {
        string? actual = OssUtilities.RedactUrlCredentials(input);

        Assert.Equal(expected, actual);
    }

    #endregion

    #region SourceRepositoryCandidateSet duplicate aggregation

    [Fact]
    public void CandidateSet_DuplicateKey_KeepsHighestScore_WithoutThrowing()
    {
        PackageURL purlA = new("pkg:github/foo/bar");
        PackageURL purlB = new("pkg:github/foo/baz");
        SourceRepositoryCandidate[] candidates =
        {
            CreateCandidate(purlA, 0.5),
            CreateCandidate(purlA, 0.3),
            CreateCandidate(purlA, 0.9),
            CreateCandidate(purlB, 0.4),
        };

        SourceRepositoryCandidateSet result = new(candidates);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(0.9, result.Candidates.Single(candidate =>
            candidate.ToPackageUrl().ToString() == purlA.ToString()).Confidence);
        Assert.Equal(0.4, result.Candidates.Single(candidate =>
            candidate.ToPackageUrl().ToString() == purlB.ToString()).Confidence);
    }

    private static SourceRepositoryCandidate CreateCandidate(PackageURL repository, double confidence)
    {
        return new SourceRepositoryCandidate(
            new SourceRepositoryIdentity(repository),
            new[]
            {
                new SourceRepositoryEvidence(
                    SourceRepositoryEvidenceKind.MetadataText,
                    "test",
                    repository.ToString())
            },
            confidence);
    }

    #endregion

    #region RepoSearch: unsupported managers

    [Fact]
    public async Task ResolvePackageLibraryAsync_UnsupportedManager_ThrowsDescriptiveArgumentException()
    {
        // An empty override dictionary guarantees CreateProjectManager returns null for any purl type,
        // regardless of which managers ship by default.
        ProjectManagerFactory factory = new(new Dictionary<string, ProjectManagerFactory.ConstructProjectManager>());
        RepoSearch repoSearch = new(factory);
        PackageURL purl = new("pkg:someunsupportedtype/foo@1.0.0");

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => repoSearch.ResolvePackageLibraryAsync(purl));

        // Regression check: the previous implementation passed the type as the exception's `paramName`
        // argument instead of interpolating it into the message, leaving a literal "{0}" in the text.
        Assert.DoesNotContain("{0}", ex.Message);
        Assert.Contains("someunsupportedtype", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvePackageLibraryAsync_NullPurl_ReturnsEmptyDictionary_WithoutThrowing()
    {
        ProjectManagerFactory factory = new(new Dictionary<string, ProjectManagerFactory.ConstructProjectManager>());
        RepoSearch repoSearch = new(factory);

        Dictionary<PackageURL, double> result = await repoSearch.ResolvePackageLibraryAsync(null!);

        Assert.Empty(result);
    }

    #endregion

    #region NPM: malformed fields / partial results / cancellation

    [Fact]
    public async Task NPM_MalformedTopLevelJson_ReturnsEmpty_WithoutThrowing()
    {
        NPMProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:npm/malformed-json-pkg@1.0.0");

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, "{ this is not valid json ");

        Assert.Empty(result);
    }

    [Fact]
    public async Task NPM_MalformedRepositoryUrl_ReturnsEmpty_WithoutThrowing()
    {
        NPMProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:npm/malformed-repo-pkg@1.0.0");

        const string metadata = @"{
            ""dist-tags"": { ""latest"": ""1.0.0"" },
            ""versions"": {
                ""1.0.0"": {
                    ""name"": ""malformed-repo-pkg"",
                    ""version"": ""1.0.0"",
                    ""repository"": { ""type"": ""git"", ""url"": ""not a valid uri::::"" }
                }
            }
        }";

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, metadata);

        Assert.Empty(result);
    }

    [Fact]
    public async Task NPM_ValidRepositoryUrl_IsExtracted()
    {
        NPMProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:npm/good-repo-pkg@1.0.0");

        const string metadata = @"{
            ""dist-tags"": { ""latest"": ""1.0.0"" },
            ""versions"": {
                ""1.0.0"": {
                    ""name"": ""good-repo-pkg"",
                    ""version"": ""1.0.0"",
                    ""repository"": { ""type"": ""git"", ""url"": ""git+https://github.com/octo/good-repo-pkg.git"" }
                }
            }
        }";

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, metadata);

        Assert.Single(result);
    }

    [Fact]
    public async Task NPM_GetMetadataAsync_PropagatesCancellation()
    {
        MockHttpMessageHandler mockHttp = new();
        mockHttp.When(HttpMethod.Get, "https://registry.npmjs.org/*").Throw(new TaskCanceledException("The operation was canceled."));

        Mock<NPMProjectManager> manager = new(".", new NoOpPackageActions(), CreateHttpFactory(mockHttp), null) { CallBase = true };
        PackageURL purl = new("pkg:npm/cancel-me@1.0.0");

        await Assert.ThrowsAsync<TaskCanceledException>(() => manager.Object.IdentifySourceRepositoryAsync(purl, useCache: false));
    }

    #endregion

    #region PyPI: malformed fields / partial results / cancellation

    [Fact]
    public async Task PyPI_MalformedTopLevelJson_ReturnsEmpty_WithoutThrowing()
    {
        PyPIProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:pypi/malformed-json-pkg@1.0.0");

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, "not json at all");

        Assert.Empty(result);
    }

    [Fact]
    public async Task PyPI_NonObjectProjectUrls_ReturnsEmpty_WithoutThrowing()
    {
        PyPIProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:pypi/bad-project-urls-pkg@1.0.0");

        const string metadata = @"{
            ""info"": {
                ""home_page"": """",
                ""download_url"": """",
                ""project_urls"": [ ""not"", ""an"", ""object"" ]
            }
        }";

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, metadata);

        Assert.Empty(result);
    }

    [Fact]
    public async Task PyPI_MissingInfoField_ReturnsEmpty_WithoutThrowing()
    {
        PyPIProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:pypi/no-info-pkg@1.0.0");

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, @"{ ""not_info"": {} }");

        Assert.Empty(result);
    }

    [Fact]
    public async Task PyPI_PartialSuccess_SkipsUnusableEntry_ButFindsLaterValidRepository()
    {
        PyPIProjectManager manager = new(".", new NoOpPackageActions(), CreateHttpFactory(new MockHttpMessageHandler()));
        PackageURL purl = new("pkg:pypi/partial-success-pkg@1.0.0");

        // "home_page"/"download_url" are empty/unusable, and the first project_urls entry does not
        // resolve to a GitHub repository; the loop must continue and find the valid entry afterwards
        // instead of aborting on the first unusable candidate.
        const string metadata = @"{
            ""info"": {
                ""home_page"": """",
                ""download_url"": """",
                ""project_urls"": {
                    ""Not A Repo"": ""https://example.com/not-a-repo"",
                    ""Repository"": ""https://github.com/octo/partial-success-pkg""
                }
            }
        }";

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, metadata);

        Assert.Single(result);
    }

    [Fact]
    public async Task PyPI_GetMetadataAsync_PropagatesCancellation()
    {
        MockHttpMessageHandler mockHttp = new();
        mockHttp.When(HttpMethod.Get, "https://pypi.org/*").Throw(new TaskCanceledException("The operation was canceled."));

        Mock<PyPIProjectManager> manager = new(".", new NoOpPackageActions(), CreateHttpFactory(mockHttp), null) { CallBase = true };
        PackageURL purl = new("pkg:pypi/cancel-me@1.0.0");

        await Assert.ThrowsAsync<TaskCanceledException>(() => manager.Object.IdentifySourceRepositoryAsync(purl, useCache: false));
    }

    #endregion

    #region NuGet: malformed nuspec / bounded caching / cancellation

    private const string NuGetIndexUrl = "https://api.nuget.org/v3/index.json";

    private static NuGetProjectManager CreateNuGetManager(MockHttpMessageHandler mockHttp)
    {
        mockHttp.When(HttpMethod.Get, NuGetIndexUrl).Respond(HttpStatusCode.OK, "application/json", Resources.nuget_registration_json);
        return new NuGetProjectManager(".", NuGetPackageActions.CreateV3(), CreateHttpFactory(mockHttp));
    }

    [Fact]
    public async Task NuGet_MissingNuspec_ReturnsEmpty_WithoutThrowing()
    {
        MockHttpMessageHandler mockHttp = new();
        NuGetProjectManager manager = CreateNuGetManager(mockHttp);
        mockHttp.When(HttpMethod.Get, "https://api.nuget.org/v3-flatcontainer/missingnuspec/1.0.0/missingnuspec.nuspec").Respond(HttpStatusCode.NotFound);

        PackageURL purl = new("pkg:nuget/missingnuspec@1.0.0");

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public async Task NuGet_MalformedNuspecXml_ReturnsEmpty_WithoutThrowing()
    {
        MockHttpMessageHandler mockHttp = new();
        NuGetProjectManager manager = CreateNuGetManager(mockHttp);
        mockHttp.When(HttpMethod.Get, "https://api.nuget.org/v3-flatcontainer/malformednuspec/1.0.0/malformednuspec.nuspec")
            .Respond("application/xml", "this is not valid xml <<<");

        PackageURL purl = new("pkg:nuget/malformednuspec@1.0.0");

        Dictionary<PackageURL, double> result = await InvokeSearchRepoUrls(manager, purl, string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public async Task NuGet_GetNuspecAsync_CachesAcrossCalls_OnlyFetchesOnce()
    {
        MockHttpMessageHandler mockHttp = new();
        NuGetProjectManager manager = CreateNuGetManager(mockHttp);
        const string nuspecXml = @"<?xml version=""1.0""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
    <metadata>
        <id>cachedpkg</id>
        <version>1.0.0</version>
        <authors>test</authors>
        <description>test</description>
        <repository type=""git"" url=""https://github.com/octo/cachedpkg"" />
    </metadata>
</package>";
        MockedRequest request = mockHttp
            .When(HttpMethod.Get, "https://api.nuget.org/v3-flatcontainer/cachedpkg/1.0.0/cachedpkg.nuspec")
            .Respond("application/xml", nuspecXml);

        MethodInfo? getNuspecAsync = typeof(NuGetProjectManager).GetMethod(
            "GetNuspecAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(getNuspecAsync);

        Task firstTask = (Task)getNuspecAsync!.Invoke(manager, new object[] { "cachedpkg", "1.0.0", true })!;
        await firstTask;
        Task secondTask = (Task)getNuspecAsync.Invoke(manager, new object[] { "cachedpkg", "1.0.0", true })!;
        await secondTask;

        Assert.Equal(1, mockHttp.GetMatchCount(request));
    }

    [Fact]
    public async Task NuGet_GetNuspecAsync_PropagatesCancellation()
    {
        MockHttpMessageHandler mockHttp = new();
        NuGetProjectManager manager = CreateNuGetManager(mockHttp);
        mockHttp.When(HttpMethod.Get, "https://api.nuget.org/v3-flatcontainer/cancel-me/1.0.0/cancel-me.nuspec")
            .Throw(new TaskCanceledException("The operation was canceled."));

        PackageURL purl = new("pkg:nuget/cancel-me@1.0.0");

        await Assert.ThrowsAsync<TaskCanceledException>(() => InvokeSearchRepoUrls(manager, purl, string.Empty));
    }

    #endregion
}
