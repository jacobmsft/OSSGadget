// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

using Microsoft.CST.OpenSource.Helpers;
using PackageUrl;
using System;

/// <summary>
/// Identifies a repository independently of evidence, confidence, versions, and verification.
/// </summary>
public sealed class SourceRepositoryIdentity : IEquatable<SourceRepositoryIdentity>
{
    private readonly string identityKey;

    public SourceRepositoryIdentity(
        PackageURL repositoryPurl,
        Uri? canonicalRepositoryUri = null,
        string? host = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryPurl);

        Subpath = NormalizeSubpath(repositoryPurl.Subpath);
        CanonicalRepositoryPurl = new PackageURL(
            repositoryPurl.Type,
            repositoryPurl.Namespace,
            repositoryPurl.Name,
            null,
            null,
            null);

        CanonicalRepositoryUri = SanitizeUri(canonicalRepositoryUri) ?? CreateCanonicalUri(CanonicalRepositoryPurl);
        Host = NormalizeHost(CanonicalRepositoryUri?.Host ?? host ?? GetDefaultHost(repositoryPurl.Type));
        identityKey = string.Join(
            "|",
            CanonicalRepositoryPurl.ToString(),
            Host,
            Subpath ?? string.Empty).ToUpperInvariant();
    }

    public PackageURL CanonicalRepositoryPurl { get; }

    public Uri? CanonicalRepositoryUri { get; }

    public string Host { get; }

    public string? Subpath { get; }

    public bool Equals(SourceRepositoryIdentity? other)
    {
        return other is not null &&
            StringComparer.Ordinal.Equals(identityKey, other.identityKey);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as SourceRepositoryIdentity);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(identityKey);
    }

    private static Uri? CreateCanonicalUri(PackageURL repositoryPurl)
    {
        return RepositoryUrlParser.GetRepositoryUri(repositoryPurl);
    }

    private static string? GetDefaultHost(string? repositoryType)
    {
        return repositoryType?.ToLowerInvariant() switch
        {
            "github" => "github.com",
            "gitlab" => "gitlab.com",
            "bitbucket" => "bitbucket.org",
            _ => null
        };
    }

    private static string NormalizeHost(string? host)
    {
        return (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string? NormalizeSubpath(string? subpath)
    {
        if (string.IsNullOrWhiteSpace(subpath))
        {
            return null;
        }

        string normalized = string.Join(
            "/",
            subpath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }

    private static Uri? SanitizeUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        UriBuilder builder = new(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }
}
