using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Backs the shell search box: query text, suggestion population, submit command,
/// and suggestion selection.
/// </summary>
public partial class SearchBoxViewModel : ObservableObject
{
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<SearchResultModel>>> suggestionProvider;

    [ObservableProperty]
    private string text = string.Empty;

    /// <summary>
    /// Creates a search box that asks <paramref name="suggestionProvider"/> for matches each time
    /// the query changes (real library). The provider runs the query and maps results to
    /// suggestions; this view model feeds them to the drop-down and handles cancellation.
    /// </summary>
    public SearchBoxViewModel(Func<string?, CancellationToken, Task<IReadOnlyList<SearchResultModel>>> suggestionProvider)
    {
        this.suggestionProvider = suggestionProvider;
        SubmitCommand = new RelayCommand(Submit);
    }

    /// <summary>Raised when the user submits a free-text query.</summary>
    public event Action<string>? QuerySubmitted;

    /// <summary>Raised when the user picks a specific suggestion.</summary>
    public event Action<SearchResultModel>? SuggestionChosen;

    /// <summary>
    /// Feeds <c>AutoCompleteBox.AsyncPopulator</c>. The control's own filter is disabled
    /// (FilterMode=None), so the returned list is shown exactly as produced by the provider.
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> Populator => PopulateAsync;

    public IRelayCommand SubmitCommand { get; }

    /// <summary>
    /// Raises <see cref="SuggestionChosen"/> for a suggestion the user explicitly committed from the
    /// drop-down. Called by the view; routing through here keeps text-completion selections (which
    /// merely match the typed text) from triggering navigation.
    /// </summary>
    public void ChooseSuggestion(SearchResultModel suggestion) => SuggestionChosen?.Invoke(suggestion);

    private async Task<IEnumerable<object>> PopulateAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<object>();
        }

        return await suggestionProvider(query, cancellationToken);
    }

    private void Submit()
    {
        if (!string.IsNullOrWhiteSpace(Text))
        {
            QuerySubmitted?.Invoke(Text.Trim());
        }
    }
}
