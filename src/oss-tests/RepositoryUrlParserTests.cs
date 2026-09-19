// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests;

using Microsoft.CST.OpenSource.Helpers;
using PackageUrl;

public class RepositoryUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/Microsoft/OSSGadget", "pkg:github/microsoft/ossgadget")]
    [InlineData("https://www.github.com/Microsoft/OSSGadget/", "pkg:github/microsoft/ossgadget")]
    [InlineData("git+https://user:secret@github.com/Microsoft/OSSGadget.git", "pkg:github/microsoft/ossgadget")]
    [InlineData("ssh://git@github.com/Microsoft/OSSGadget.git", "pkg:github/microsoft/ossgadget")]
    [InlineData("git@github.com:Microsoft/OSSGadget.git", "pkg:github/microsoft/ossgadget")]
    [InlineData("https://gitlab.com/group/subgroup/project", "pkg:gitlab/group/subgroup/project")]
    [InlineData("git@bitbucket.org:workspace/project.git", "pkg:bitbucket/workspace/project")]
    public void ParsesCanonicalRepositoryVariants(string input, string expected)
    {
        Assert.True(RepositoryUrlParser.TryParse(input, out PackageURL? packageUrl));
        Assert.Equal(expected, packageUrl!.ToString());
    }

    [Fact]
    public void PreservesMeaningfulRepositorySubpath()
    {
        Assert.True(RepositoryUrlParser.TryParse(
            "https://github.com/Microsoft/OSSGadget/tree/main/src", out PackageURL? packageUrl));

        Assert.Equal("pkg:github/microsoft/ossgadget@main#src", packageUrl!.ToString());
    }

    [Fact]
    public void PreservesSourceControlFragmentAsSourceVersion()
    {
        Assert.True(RepositoryUrlParser.TryParse(
            "git+https://github.com/acme/project.git#release-1.0",
            out PackageURL? packageUrl));

        Assert.Equal("release-1.0", packageUrl!.Version);
    }

    [Fact]
    public void ParsesAuthoritativeGenericRepositoryAndRetainsHost()
    {
        Assert.True(RepositoryUrlParser.TryParseRepository(
            "ssh://user:secret@git.example.com/acme/project.git",
            allowGenericHosts: true,
            out RepositoryUrlParseResult? repository));

        Assert.Equal("pkg:generic/git.example.com/acme/project", repository!.PackageUrl.ToString());
        Assert.Equal("https://git.example.com/acme/project", repository.CanonicalUri.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://github.com/Microsoft/OSSGadget/issues/42")]
    [InlineData("https://github.com/Microsoft/OSSGadget/pull/42")]
    [InlineData("https://github.com/Microsoft/OSSGadget/blob/main/README.md")]
    [InlineData("https://github.com/Microsoft/OSSGadget/actions")]
    [InlineData("https://github.com/repos/Microsoft/OSSGadget")]
    [InlineData("https://api.github.com/repos/Microsoft/OSSGadget")]
    [InlineData("https://github.com/Microsoft/OSSGadget/archive/main.zip")]
    [InlineData("https://gitlab.com/group/project/-/issues/1")]
    [InlineData("https://gitlab.com/api/v4/projects/1")]
    [InlineData("https://bitbucket.org/workspace/project/pull-requests/1")]
    [InlineData("https://bitbucket.org/api/2.0/repositories/workspace/project")]
    [InlineData("https://github.com/owner/repository/tree")]
    [InlineData("https://gitlab.com/group/project/-/tree")]
    [InlineData("https://bitbucket.org/workspace/project/src")]
    public void RejectsNonRepositoryUrls(string input)
    {
        Assert.False(RepositoryUrlParser.TryParse(input, out _));
    }

    [Fact]
    public void ExtractsBoundedUrlsAndDeduplicatesCanonicalForms()
    {
        const string content =
            "github.com/Microsoft/OSSGadget, https://www.github.com/Microsoft/OSSGadget.git; " +
            "notgithub.com/Microsoft/OSSGadget and https://github.com/Microsoft/OSSGadget/issues/1";

        PackageURL[] repositories = RepositoryUrlParser.Extract(content).ToArray();

        var repository = Assert.Single(repositories);
        Assert.Equal("pkg:github/microsoft/ossgadget", repository.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("github.com")]
    [InlineData("https://github.com/only-owner")]
    [InlineData("https://github.com/owner/")]
    [InlineData("https://example.com/owner/repository")]
    [InlineData("https://github.com/owner/repo?tab=readme#readme")]
    public void HandlesMalformedOrUnsupportedInput(string input)
    {
        bool parsed = RepositoryUrlParser.TryParse(input, out PackageURL? packageUrl);
        Assert.Equal(input.Contains("tab=readme", StringComparison.Ordinal), parsed);
        if (parsed)
        {
            Assert.Equal("pkg:github/owner/repo", packageUrl!.ToString());
        }
    }

    [Theory]
    [InlineData(
        "pkg:github/acme/project@main#src/component",
        "https://github.com/acme/project/tree/main/src/component")]
    [InlineData(
        "pkg:gitlab/acme/project@release#packages/example",
        "https://gitlab.com/acme/project/-/tree/release/packages/example")]
    [InlineData(
        "pkg:bitbucket/acme/project@default#src",
        "https://bitbucket.org/acme/project/src/default/src")]
    [InlineData(
        "pkg:generic/git.example.com/acme/project",
        "https://git.example.com/acme/project")]
    public void BuildsProviderAwareRepositoryUris(string purl, string expected)
    {
        Uri? repositoryUri = RepositoryUrlParser.GetRepositoryUri(
            new PackageURL(purl),
            includeSourceLocation: true);

        Assert.Equal(expected, repositoryUri!.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task NonGitHubRepositoryMetadataRetainsCanonicalFields()
    {
        PackageURL purl = new("pkg:gitlab/acme/project");
        Microsoft.CST.OpenSource.Model.Repository repository = new();

        await repository.ExtractRepositoryMetadata(purl);

        Assert.Equal("gitlab", repository.Type);
        Assert.Equal("project", repository.Name);
        Assert.Equal(purl.ToString(), repository.Purl);
        Assert.Equal("https://gitlab.com/acme/project", repository.Uri);
    }
}
