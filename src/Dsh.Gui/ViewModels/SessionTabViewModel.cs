using CommunityToolkit.Mvvm.ComponentModel;

namespace Dsh.Gui.ViewModels;

/**
 * One tab of a session view: the session itself (its conversation or its terminal) or an extra shell in the project directory.
 * Only extra shells can be closed; closing one ends its shell.
 */
public sealed partial class SessionTabViewModel : ObservableObject
{
    private SessionTabViewModel(string title, SessionTranscriptViewModel? transcript, TerminalViewModel? terminal, bool isClosable)
    {
        Title = title;
        Transcript = transcript;
        Terminal = terminal;
        IsClosable = isClosable;
    }

    public string Title { get; }

    public SessionTranscriptViewModel? Transcript { get; }

    public TerminalViewModel? Terminal { get; }

    public bool IsTranscript => Transcript is not null;

    public bool IsTerminal => Terminal is not null;

    public bool IsClosable { get; }

    [ObservableProperty]
    private bool _isSelected;

    public static SessionTabViewModel ForTranscript(SessionTranscriptViewModel transcript) => new("会话", transcript, null, false);

    public static SessionTabViewModel ForSessionTerminal(TerminalViewModel terminal) => new("会话", null, terminal, false);

    public static SessionTabViewModel ForShell(string title, TerminalViewModel terminal) => new(title, null, terminal, true);

    public async ValueTask CloseAsync()
    {
        Transcript?.Dispose();
        if (Terminal is not null)
            await Terminal.DisposeAsync();
    }
}
