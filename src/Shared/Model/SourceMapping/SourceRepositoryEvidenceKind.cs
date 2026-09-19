// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

/// <summary>
/// Describes why a source repository candidate was identified.
/// </summary>
public enum SourceRepositoryEvidenceKind
{
    Unknown,
    RegistryRepository,
    RegistrySource,
    RegistryProjectUrl,
    Homepage,
    DownloadUrl,
    MetadataText,
    RepositorySearch,
    NameInference,
    BuiltInMapping
}
