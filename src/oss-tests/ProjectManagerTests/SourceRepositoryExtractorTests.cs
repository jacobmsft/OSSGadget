// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests.ProjectManagerTests;

using Microsoft.CST.OpenSource.Helpers;
using PackageUrl;
using System.Linq;

public class SourceRepositoryExtractorTests
{
    [Theory]
    [InlineData(
        "pkg:cargo/rand@0.8.5",
        """{"crate":{"repository":"https://github.com/rust-random/rand.git","homepage":"https://docs.rs/rand"}}""",
        "pkg:github/rust-random/rand")]
    [InlineData(
        "pkg:composer/acme/library@1.0.0",
        """{"packages":{"acme/library":{"1.0.0":{"source":{"type":"git","url":"https://gitlab.example.com/acme/library.git"}}}}}""",
        "pkg:generic/gitlab.example.com/acme/library")]
    [InlineData(
        "pkg:cocoapods/Example@1.0.0",
        """{"source":{"git":"https://bitbucket.org/acme/example.git","tag":"1.0.0"},"homepage":"https://example.com"}""",
        "pkg:bitbucket/acme/example")]
    [InlineData(
        "pkg:gem/example@1.0.0",
        """{"source_code_uri":"https://git.example.com/acme/example.git","homepage_uri":"https://example.com"}""",
        "pkg:generic/git.example.com/acme/example")]
    public void FromJsonExtractsOnlyAuthoritativeRepositoryFields(string package, string metadata, string expected)
    {
        Dictionary<PackageURL, double> repositories = SourceRepositoryExtractor.FromJson(new PackageURL(package), metadata);

        repositories.Keys.Select(repository => repository.ToString()).Should().ContainSingle(expected);
    }

    [Fact]
    public void FromXmlExtractsMavenScmWithoutUsingProjectUrl()
    {
        const string metadata = """
            <project>
              <url>https://example.com/project</url>
              <scm>
                <connection>scm:git:https://github.com/acme/project.git</connection>
                <url>https://github.com/acme/project</url>
              </scm>
            </project>
            """;

        Dictionary<PackageURL, double> repositories = SourceRepositoryExtractor.FromXml(
            new PackageURL("pkg:maven/acme/project@1.0.0"),
            metadata);

        repositories.Keys.Select(repository => repository.ToString()).Should().ContainSingle("pkg:github/acme/project");
    }

    [Fact]
    public void FromExplicitTextFieldsExtractsVcsFields()
    {
        const string metadata = """
            Package: example
            Homepage: https://example.com
            Vcs-Git: https://salsa.debian.org/acme/example.git -b main
            """;

        Dictionary<PackageURL, double> repositories = SourceRepositoryExtractor.FromExplicitTextFields(
            new PackageURL("pkg:generic/example"),
            metadata);

        repositories.Keys.Select(repository => repository.ToString()).Should().ContainSingle("pkg:generic/salsa.debian.org/acme/example");
    }
}
