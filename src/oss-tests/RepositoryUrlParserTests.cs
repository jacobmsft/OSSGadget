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

        Assert.Equal("pkg:github/microsoft/ossgadget#tree/main/src", packageUrl!.ToString());
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
}
