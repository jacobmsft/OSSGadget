// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Tests.ProjectManagerTests;

using Microsoft.CST.OpenSource.Helpers;
using Microsoft.CST.OpenSource.Model.SourceMapping;
using PackageUrl;
using System.Linq;

public class SourceRepositoryExtractorTests
{
    [Theory]
    [InlineData(
        "pkg:cargo/rand@0.8.5",
        "{\"crate\":{\"repository\":\"https://github.com/rust-random/rand.git\",\"homepage\":\"https://docs.rs/rand\"}}",
        "pkg:github/rust-random/rand")]
    [InlineData(
        "pkg:composer/acme/library@1.0.0",
        "{\"packages\":{\"acme/library\":{\"1.0.0\":{\"source\":{\"type\":\"git\",\"url\":\"https://gitlab.example.com/acme/library.git\"}}}}}",
        "pkg:generic/gitlab.example.com/acme/library")]
    [InlineData(
        "pkg:cocoapods/Example@1.0.0",
        "{\"source\":{\"git\":\"https://bitbucket.org/acme/example.git\",\"tag\":\"1.0.0\"},\"homepage\":\"https://example.com\"}",
        "pkg:bitbucket/acme/example@1.0.0")]
    [InlineData(
        "pkg:gem/example@1.0.0",
        "{\"source_code_uri\":\"https://git.example.com/acme/example.git\",\"homepage_uri\":\"https://example.com\"}",
        "pkg:generic/git.example.com/acme/example")]
    public void FromJsonExtractsOnlyAuthoritativeRepositoryFields(string package, string metadata, string expected)
    {
        Dictionary<PackageURL, double> repositories = SourceRepositoryExtractor.FromJson(new PackageURL(package), metadata);

        Assert.Equal(new[] { expected }, repositories.Keys.Select(repository => repository.ToString()));
    }

    [Fact]
    public void FromXmlExtractsMavenScmWithoutUsingProjectUrl()
    {
        const string metadata = @"
            <project>
              <url>https://example.com/project</url>
              <scm>
                <connection>scm:git:https://github.com/acme/project.git</connection>
                <url>https://github.com/acme/project</url>
              </scm>
            </project>";

        Dictionary<PackageURL, double> repositories = SourceRepositoryExtractor.FromXml(
            new PackageURL("pkg:maven/acme/project@1.0.0"),
            metadata);

        Assert.Equal(
            new[] { "pkg:github/acme/project" },
            repositories.Keys.Select(repository => repository.ToString()));
    }

    [Fact]
    public void FromExplicitTextFieldsExtractsVcsFields()
    {
        const string metadata = @"
            Package: example
            Homepage: https://example.com
            Vcs-Git: https://salsa.debian.org/acme/example.git -b main";

        Dictionary<PackageURL, double> repositories = SourceRepositoryExtractor.FromExplicitTextFields(
            new PackageURL("pkg:generic/example"),
            metadata);

        Assert.Equal(
            new[] { "pkg:generic/salsa.debian.org/acme/example@main" },
            repositories.Keys.Select(repository => repository.ToString()));
    }

    [Fact]
    public void CandidateExtractionRetainsFieldRawValueAndCanonicalHost()
    {
        const string metadata =
            "{\"source\":{\"url\":\"https://" +
            "user:secret@" +
            "git.example.com/acme/example.git?token=value\"}}";

        SourceRepositoryCandidate candidate = Assert.Single(
            SourceRepositoryExtractor.CandidatesFromJson(
                new PackageURL("pkg:composer/acme/example@1.0.0"),
                metadata));
        SourceRepositoryEvidence evidence = Assert.Single(candidate.Evidence);

        Assert.Equal(SourceRepositoryEvidenceKind.RegistrySource, evidence.Kind);
        Assert.Equal("source.url", evidence.SourceField);
        Assert.Equal("https://git.example.com/acme/example.git?token=<redacted>", evidence.RawValue);
        Assert.Equal("git.example.com", candidate.Identity.Host);
        Assert.Equal("pkg:generic/git.example.com/acme/example", candidate.ToPackageUrl().ToString());
    }

    [Fact]
    public void CandidateExtractionRetainsJsonSourceRefAndRepositorySubpath()
    {
        const string metadata =
            "{\"gitHead\":\"abc123\",\"repository\":{\"url\":\"https://github.com/acme/monorepo.git\",\"directory\":\"packages/example\"}}";

        SourceRepositoryCandidate candidate = Assert.Single(
            SourceRepositoryExtractor.CandidatesFromJson(
                new PackageURL("pkg:npm/example@1.0.0"),
                metadata));

        Assert.Equal("abc123", candidate.SourceVersion);
        Assert.Equal("packages/example", candidate.Identity.Subpath);
    }

    [Fact]
    public void CandidateExtractionRetainsNestedSourceReference()
    {
        const string metadata =
            "{\"source\":{\"type\":\"git\",\"url\":\"https://gitlab.com/acme/example.git\",\"reference\":\"deadbeef\"}}";

        SourceRepositoryCandidate candidate = Assert.Single(
            SourceRepositoryExtractor.CandidatesFromJson(
                new PackageURL("pkg:composer/acme/example@1.0.0"),
                metadata));

        Assert.Equal("deadbeef", candidate.SourceVersion);
    }

    [Fact]
    public void CandidateExtractionRetainsMavenScmTag()
    {
        const string metadata = @"
            <project>
              <scm>
                <connection>scm:git:https://github.com/acme/project.git</connection>
                <tag>release-1.0</tag>
              </scm>
            </project>";

        SourceRepositoryCandidate candidate = Assert.Single(
            SourceRepositoryExtractor.CandidatesFromXml(
                new PackageURL("pkg:maven/acme/project@1.0.0"),
                metadata));

        Assert.Equal("release-1.0", candidate.SourceVersion);
    }

    [Fact]
    public void CandidateExtractionRetainsExplicitVcsBranch()
    {
        const string metadata =
            "Vcs-Git: https://salsa.debian.org/acme/example.git -b debian/main";

        SourceRepositoryCandidate candidate = Assert.Single(
            SourceRepositoryExtractor.CandidatesFromExplicitTextFields(
                new PackageURL("pkg:generic/example"),
                metadata));

        Assert.Equal("debian/main", candidate.SourceVersion);
    }
}
