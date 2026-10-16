using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Gui.ViewModels;

/** One presented file; Exists is read when the list is rebuilt so a deleted file shows as missing. */
public sealed class DeliverableItemViewModel(PresentedFile file)
{
    public string Path { get; } = file.Path;

    public string? Description { get; } = file.Description;

    public string FileName { get; } = System.IO.Path.GetFileName(file.Path);

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool Exists { get; } = File.Exists(file.Path);
}

/**
 * Header list of the shown session's deliverables.
 * It keeps the observed deliverables/presented events and folds them with SessionDeliverables, so live appends, truncation and remote replay share one rule.
 */
public sealed partial class DeliverablesViewModel : ObservableObject
{
    private readonly List<SessionEvent> _events = [];

    public ObservableCollection<DeliverableItemViewModel> Items { get; } = [];

    /** False for remote sessions: their files live on the remote machine and cannot be opened locally. */
    [ObservableProperty]
    private bool _canOpen = true;

    public bool HasItems => Items.Count > 0;

    public string Badge => Items.Count.ToString();

    public void Reset(bool canOpen)
    {
        _events.Clear();
        CanOpen = canOpen;
        Rebuild();
    }

    public void Observe(SessionEvent sessionEvent)
    {
        if (sessionEvent.Data is not DeliverablesPresentedPayload)
            return;
        _events.Add(sessionEvent);
        Rebuild();
    }

    public void TruncateTo(long eventCount)
    {
        if (_events.RemoveAll(sessionEvent => sessionEvent.Seq >= eventCount) > 0)
            Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var file in SessionDeliverables.Collect(_events))
            Items.Add(new DeliverableItemViewModel(file));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Badge));
    }
}
