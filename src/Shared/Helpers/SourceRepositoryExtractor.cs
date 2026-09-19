// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Helpers;

using Microsoft.CST.OpenSource.Model.SourceMapping;
using PackageUrl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Extracts source repositories from explicit registry metadata fields.
/// </summary>
public static class SourceRepositoryExtractor
{
    private static readonly HashSet<string> RepositoryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "repository",
        "repository_url",
        "source",
        "source_repository",
        "source_repository_url",
        "source_code_uri",
        "code_repository",
        "scm",
        "vcs",
        "vcs_git",
        "vcs_browser",
        "git",
    };

    private static readonly string[] SourceVersionKeys =
    {
        "gitHead",
        "reference",
        "commit",
        "revision",
        "tag",
        "branch",
    };

    private static readonly string[] RootSourceVersionKeys =
    {
        "gitHead",
    };

    private static readonly string[] RepositorySubpathKeys =
    {
        "directory",
        "subpath",
    };

    private static readonly Regex ExplicitTextFieldRegex = new(
        @"(?im)^\s*(?<field>Vcs-(?:Git|Browser)|Source-(?:Repository|URL)|Repository)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled);

    private static readonly Regex ExplicitJsonUrlRegex = new(
        "(?i)\"(?<field>git|repository_url|source_repository_url)\"\\s*:\\s*\"(?<url>[^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts repository URLs from JSON values under authoritative repository/VCS fields.
    /// Homepage and arbitrary URL fields are intentionally ignored.
    /// </summary>
    public static Dictionary<PackageURL, double> FromJson(PackageURL package, string metadata)
    {
        return new SourceRepositoryCandidateSet(CandidatesFromJson(package, metadata))
            .ToCompatibilityDictionary();
    }

    /// <summary>
    /// Extracts evidence-rich candidates from authoritative JSON repository/VCS fields.
    /// </summary>
    public static IReadOnlyList<SourceRepositoryCandidate> CandidatesFromJson(
        PackageURL package,
        string metadata)
    {
        List<SourceRepositoryCandidate> result = new();
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(metadata);
            VisitJson(
                package,
                document.RootElement,
                result,
                null,
                SourceRepositoryEvidenceKind.Unknown,
                null,
                null);
        }
        catch (JsonException)
        {
            // Some registry endpoints concatenate multiple JSON documents or embed a podspec
            // JSON document in HTML. Parse line-delimited documents, then retain only explicit
            // repository/VCS URL properties as a conservative fallback.
            foreach (string line in metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line.Trim());
                    VisitJson(
                        package,
                        document.RootElement,
                        result,
                        null,
                        SourceRepositoryEvidenceKind.Unknown,
                        null,
                        null);
                }
                catch (JsonException)
                {
                    // Continue with the explicit-field fallback below.
                }
            }

            foreach (Match match in ExplicitJsonUrlRegex.Matches(metadata))
            {
                string field = match.Groups["field"].Value;
                AddUrl(package, match.Groups["url"].Value, field, GetEvidenceKind(field), result);
            }
        }
        return new SourceRepositoryCandidateSet(result).Candidates;
    }

    /// <summary>
    /// Extracts repositories from Maven-style SCM XML.
    /// </summary>
    public static Dictionary<PackageURL, double> FromXml(PackageURL package, string metadata)
    {
        return new SourceRepositoryCandidateSet(CandidatesFromXml(package, metadata))
            .ToCompatibilityDictionary();
    }

    /// <summary>
    /// Extracts evidence-rich candidates from Maven-style SCM XML.
    /// </summary>
    public static IReadOnlyList<SourceRepositoryCandidate> CandidatesFromXml(
        PackageURL package,
        string metadata)
    {
        List<SourceRepositoryCandidate> result = new();
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return result;
        }

        XDocument document = XDocument.Parse(metadata);
        foreach (XElement scm in document.Descendants().Where(element => element.Name.LocalName.Equals("scm", StringComparison.OrdinalIgnoreCase)))
        {
            string? sourceVersion = scm
                .Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("tag", StringComparison.OrdinalIgnoreCase))
                ?.Value
                .Trim();
            foreach (XElement child in scm.Elements())
            {
                if (child.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.LocalName.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.LocalName.Equals("developerConnection", StringComparison.OrdinalIgnoreCase))
                {
                    AddUrl(
                        package,
                        child.Value,
                        $"scm.{child.Name.LocalName}",
                        SourceRepositoryEvidenceKind.RegistryRepository,
                        result,
                        sourceVersion);
                }
            }
        }
        return new SourceRepositoryCandidateSet(result).Candidates;
    }

    /// <summary>
    /// Extracts repositories from explicit Debian source control fields.
    /// </summary>
    public static Dictionary<PackageURL, double> FromExplicitTextFields(PackageURL package, string metadata)
    {
        return new SourceRepositoryCandidateSet(CandidatesFromExplicitTextFields(package, metadata))
            .ToCompatibilityDictionary();
    }

    /// <summary>
    /// Extracts evidence-rich candidates from explicit source-control text fields.
    /// </summary>
    public static IReadOnlyList<SourceRepositoryCandidate> CandidatesFromExplicitTextFields(
        PackageURL package,
        string metadata)
    {
        List<SourceRepositoryCandidate> result = new();
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return result;
        }

        foreach (Match match in ExplicitTextFieldRegex.Matches(metadata))
        {
            string field = match.Groups["field"].Value;
            string fieldValue = match.Groups["value"].Value.Trim().TrimEnd(';', ',');
            string[] parts = fieldValue.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            string? sourceVersion = null;
            for (int index = 1; index + 1 < parts.Length; index++)
            {
                if (parts[index].Equals("-b", StringComparison.Ordinal))
                {
                    sourceVersion = parts[index + 1];
                    break;
                }
            }

            AddUrl(
                package,
                parts[0],
                field,
                GetEvidenceKind(field),
                result,
                sourceVersion);
        }
        return new SourceRepositoryCandidateSet(result).Candidates;
    }

    private static void VisitJson(
        PackageURL package,
        JsonElement element,
        List<SourceRepositoryCandidate> result,
        string? authoritativeField,
        SourceRepositoryEvidenceKind evidenceKind,
        string? inheritedSourceVersion,
        string? inheritedSubpath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                string? sourceVersion =
                    GetFirstStringProperty(
                        element,
                        authoritativeField is null ? RootSourceVersionKeys : SourceVersionKeys) ??
                    inheritedSourceVersion;
                string? subpath = authoritativeField is null
                    ? inheritedSubpath
                    : GetFirstStringProperty(element, RepositorySubpathKeys) ?? inheritedSubpath;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    bool startsAuthoritativeContext =
                        authoritativeField is null &&
                        RepositoryKeys.Contains(property.Name);
                    string? sourceField = startsAuthoritativeContext
                        ? property.Name
                        : authoritativeField is null
                            ? null
                            : $"{authoritativeField}.{property.Name}";
                    SourceRepositoryEvidenceKind propertyEvidenceKind = startsAuthoritativeContext
                        ? GetEvidenceKind(property.Name)
                        : evidenceKind;

                    if (sourceField is not null && property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddUrl(
                            package,
                            property.Value.GetString(),
                            sourceField,
                            propertyEvidenceKind,
                            result,
                            sourceVersion,
                            subpath);
                    }
                    else
                    {
                        VisitJson(
                            package,
                            property.Value,
                            result,
                            sourceField,
                            propertyEvidenceKind,
                            sourceVersion,
                            subpath);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    VisitJson(
                        package,
                        child,
                        result,
                        authoritativeField,
                        evidenceKind,
                        inheritedSourceVersion,
                        inheritedSubpath);
                }
                break;
            case JsonValueKind.String when authoritativeField is not null:
                AddUrl(
                    package,
                    element.GetString(),
                    authoritativeField,
                    evidenceKind,
                    result,
                    inheritedSourceVersion,
                    inheritedSubpath);
                break;
        }
    }

    private static void AddUrl(
        PackageURL package,
        string? rawUrl,
        string sourceField,
        SourceRepositoryEvidenceKind evidenceKind,
        List<SourceRepositoryCandidate> result,
        string? sourceVersion = null,
        string? repositorySubpath = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }

        if (!RepositoryUrlParser.TryParseRepository(
                rawUrl.Trim().Trim('"', '\''),
                allowGenericHosts: true,
                out RepositoryUrlParseResult? parsed) ||
            parsed is null)
        {
            return;
        }

        string? normalizedSubpath = NormalizeSubpath(repositorySubpath);
        PackageURL repositoryPurl = new(
            parsed.PackageUrl.Type,
            parsed.PackageUrl.Namespace,
            parsed.PackageUrl.Name,
            null,
            null,
            parsed.PackageUrl.Subpath ?? normalizedSubpath);
        result.Add(new SourceRepositoryCandidate(
            new SourceRepositoryIdentity(repositoryPurl, parsed.CanonicalUri),
            new[]
            {
                new SourceRepositoryEvidence(
                    evidenceKind,
                    sourceField,
                    rawUrl,
                    package.Version)
            },
            1.0,
            package.Version,
            parsed.PackageUrl.Version ?? NormalizeSourceVersion(sourceVersion),
            parsed.PackageUrl.Qualifiers));
    }

    private static string? GetFirstStringProperty(JsonElement element, IEnumerable<string> propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }

    private static string? NormalizeSourceVersion(string? sourceVersion)
    {
        return string.IsNullOrWhiteSpace(sourceVersion)
            ? null
            : sourceVersion.Trim().Trim('"', '\'');
    }

    private static string? NormalizeSubpath(string? subpath)
    {
        if (string.IsNullOrWhiteSpace(subpath))
        {
            return null;
        }

        string[] segments = subpath
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ||
            segments.Any(segment => segment is "." or "..")
            ? null
            : string.Join('/', segments);
    }

    private static SourceRepositoryEvidenceKind GetEvidenceKind(string sourceField)
    {
        return sourceField.Contains("source", StringComparison.OrdinalIgnoreCase)
            ? SourceRepositoryEvidenceKind.RegistrySource
            : SourceRepositoryEvidenceKind.RegistryRepository;
    }
}
