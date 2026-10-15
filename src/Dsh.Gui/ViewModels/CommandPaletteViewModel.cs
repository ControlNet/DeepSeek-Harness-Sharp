using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

/**
 * Quick switcher (Ctrl+K): fuzzy search over every session of every machine, Enter jumps to the selected one.
 * A query matches a session when its characters appear in order in the session's title, agent, project, machine or id.
 * Results are ordered by how directly they match (title substring, then any substring, then scattered characters), then sessions that need attention, then recent activity.
 */
public sealed partial class CommandPaletteViewModel(Func<IReadOnlyList<SessionCardViewModel>> cards) : ObservableObject
{
    public const int MaxResults = 50;

    public ObservableCollection<SessionCardViewModel> Results { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private SessionCardViewModel? _selected;

    public bool HasNoResults => IsOpen && Results.Count == 0;

    public event Action<SessionCardViewModel>? OpenRequested;

    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }
        Query = "";
        IsOpen = true;
        Search();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Move(int delta)
    {
        if (Results.Count == 0)
            return;
        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[(index + delta + Results.Count) % Results.Count];
    }

    [RelayCommand]
    private void Confirm(SessionCardViewModel? card)
    {
        var target = card ?? Selected;
        if (target is null)
            return;
        IsOpen = false;
        OpenRequested?.Invoke(target);
    }

    /** Recomputes the results from the current cards (also called when sessions change while the palette is open). */
    public void Search()
    {
        var query = Query.Trim();
        var matches = cards()
            .Select(card => (Card: card, Match: Classify(card, query)))
            .Where(item => item.Match != MatchKind.None)
            .OrderBy(item => item.Match)
            .ThenByDescending(item => item.Card.NeedsAttention)
            .ThenByDescending(item => item.Card.LastActivityAt)
            .Take(MaxResults)
            .Select(item => item.Card)
            .ToList();
        CollectionSync.Sync(Results, matches);
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    partial void OnQueryChanged(string value)
    {
        if (IsOpen)
            Search();
    }

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    private enum MatchKind
    {
        TitleSubstring,
        Substring,
        Scattered,
        None,
    }

    private static MatchKind Classify(SessionCardViewModel card, string query)
    {
        if (query.Length == 0 || card.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return MatchKind.TitleSubstring;
        if (card.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            return MatchKind.Substring;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => IsSubsequence(term, card.SearchText)) ? MatchKind.Scattered : MatchKind.None;
    }

    /** Whether the characters of a query term occur in the text in the same order (case-insensitive); terms match independently, in any order. */
    private static bool IsSubsequence(ReadOnlySpan<char> term, ReadOnlySpan<char> text)
    {
        var position = 0;
        foreach (var character in term)
        {
            var found = text[position..].IndexOfAny(char.ToLowerInvariant(character), char.ToUpperInvariant(character));
            if (found < 0)
                return false;
            position += found + 1;
        }
        return true;
    }
}
