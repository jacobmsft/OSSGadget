// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

using System;

/// <summary>
/// Describes repository relationships discovered without making them part of candidate identity.
/// </summary>
[Flags]
public enum SourceRepositoryRelationship
{
    None = 0,
    Fork = 1,
    Upstream = 2
}
