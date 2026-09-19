// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Helpers;

using PackageUrl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Parses repository URLs into canonical PackageURLs.
/// </summary>
public static class RepositoryUrlParser
{
    private static readonly Regex CandidateRegex = new(
        @"(?<![\w.@/-])(?:(?:git\+)?(?:https?|ssh|git)://[^\s<>\u0022'`]+|git@[\w.-]+:[^\s<>\u0022'`]+|(?:www\.)?(?:github\.com|gitlab\.com|bitbucket\.org)(?:[:]\d+)?/[^\s<>\u0022'`]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> GitHubNonRepositoryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "actions", "archive", "blob", "code", "commit", "commits", "compare", "discussions",
        "issues", "pull", "pulls", "raw", "releases", "security", "tags", "wiki"
    };

    private static readonly HashSet<string> GitLabNonRepositoryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "archive", "blob", "issues", "merge_requests", "raw", "snippets", "uploads"
    };

    private static readonly HashSet<string> BitbucketNonRepositoryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "branches", "commits", "downloads", "issues", "pull-requests", "raw", "wiki"
    };

    /// <summary>
    /// Attempts to parse a repository URL in a supported public forge into a canonical PackageURL.
    /// </summary>
    public static bool TryParse(string? value, out PackageURL? packageUrl)
    {
        packageUrl = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = TrimCandidate(value);
        if (candidate.Length == 0)
        {
            return false;
        }

        string normalized = candidate.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            ? candidate[4..]
            : candidate;

        string uriValue = normalized;
        if (TryParseScpLike(normalized, out string? scpHost, out string? scpPath))
        {
            uriValue = $"https://{scpHost}/{scpPath}";
        }
        else if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            uriValue = $"https://{normalized}";
        }

        if (!Uri.TryCreate(uriValue, UriKind.Absolute, out Uri? uri) ||
            !IsSupportedHost(uri.Host, out string? type))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length < 2)
        {
            return false;
        }

        int repositoryIndex = type is "github" or "bitbucket" ? 1 : segments.Length - 1;
        if (segments.Length <= repositoryIndex)
        {
            return false;
        }

        string repositoryName = segments[repositoryIndex];
        if (repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repositoryName = repositoryName[..^4];
        }

        if (string.IsNullOrWhiteSpace(repositoryName) ||
            repositoryName is "." or ".." ||
            repositoryName.Contains(':') ||
            IsNonRepositoryPath(type, segments))
        {
            return false;
        }

        string? namespaceValue = type == "github" || type == "bitbucket"
            ? segments[0]
            : string.Join('/', segments[..repositoryIndex]);
        if (string.IsNullOrWhiteSpace(namespaceValue) || namespaceValue is "." or "..")
        {
            return false;
        }

        string[] subpathSegments = segments[(repositoryIndex + 1)..];
        packageUrl = new PackageURL(type, namespaceValue, repositoryName, null, null,
            subpathSegments.Length == 0 ? null : string.Join('/', subpathSegments));
        return true;
    }

    /// <summary>
    /// Extracts supported repository URLs from prose or serialized metadata.
    /// </summary>
    public static IEnumerable<PackageURL> Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<PackageURL>();
        }

        return CandidateRegex.Matches(content)
            .Select(match => TryParse(match.Value, out PackageURL? packageUrl) ? packageUrl : null)
            .Where(packageUrl => packageUrl is not null)
            .Cast<PackageURL>()
            .GroupBy(packageUrl => $"{packageUrl.Type}|{packageUrl.Namespace}|{packageUrl.Name}|{packageUrl.Subpath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsSupportedHost(string host, out string type)
    {
        string normalizedHost = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..].ToLowerInvariant()
            : host.ToLowerInvariant();
        type = normalizedHost switch
        {
            "github.com" => "github",
            "gitlab.com" => "gitlab",
            "bitbucket.org" => "bitbucket",
            _ => string.Empty
        };
        return type.Length != 0;
    }

    private static bool IsNonRepositoryPath(string type, IReadOnlyList<string> segments)
    {
        if (segments.Count <= 2)
        {
            return false;
        }

        string firstExtraSegment = segments[2];
        if (type == "github")
        {
            if (segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return GitHubNonRepositoryPaths.Contains(firstExtraSegment) ||
                firstExtraSegment.Equals("badge.svg", StringComparison.OrdinalIgnoreCase);
        }

        if (type == "gitlab")
        {
            return segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
                GitLabNonRepositoryPaths.Contains(firstExtraSegment) ||
                firstExtraSegment.StartsWith("-", StringComparison.Ordinal);
        }

        return segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            BitbucketNonRepositoryPaths.Contains(firstExtraSegment);
    }

    private static bool TryParseScpLike(string value, out string? host, out string? path)
    {
        host = null;
        path = null;
        int separator = value.IndexOf(':');
        int at = value.IndexOf('@');
        if (separator <= 0 || at < 0 || at > separator || value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        host = value[(at + 1)..separator];
        path = value[(separator + 1)..].TrimStart('/');
        return host.Length > 0 && path.Length > 0;
    }

    private static string TrimCandidate(string value)
    {
        return value.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'');
    }
}
