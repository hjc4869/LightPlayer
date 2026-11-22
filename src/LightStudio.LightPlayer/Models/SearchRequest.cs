using System;
using System.Collections.Generic;
using System.Linq;

namespace LightStudio.LightPlayer.Models;

/// <summary>The kind of result a search is narrowed to.</summary>
public enum SearchScope
{
    All,
    Songs,
    Albums,
    Artists,
    Videos,
    Photos,
}

/// <summary>
/// What the search page is opened with: the query, the kind of result shown, and the library
/// sources searched. A null <see cref="Sources"/> searches every source.
/// </summary>
public sealed record SearchRequest(
    string Query,
    SearchScope Scope = SearchScope.All,
    IReadOnlyList<LibrarySourceId>? Sources = null)
{
    public bool Equals(SearchRequest? other) =>
        other is not null && Query == other.Query && Scope == other.Scope &&
        (Sources is null ? other.Sources is null : other.Sources is not null && Sources.SequenceEqual(other.Sources));

    public override int GetHashCode() => HashCode.Combine(Query, Scope, Sources?.Count ?? -1);
}
