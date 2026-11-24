using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ViPlex.Windows;

internal sealed class NewPackageDraft(string programName, string playerUsername, string playerPassword, IReadOnlyList<string> mediaPaths)
{
    public string ProgramName { get; } = programName;
    public string PlayerUsername { get; } = playerUsername;
    public string PlayerPassword { get; } = playerPassword;
    public IReadOnlyList<string> MediaPaths { get; } = mediaPaths;
}

internal partial class NewPackageWindow : Window
{
    private readonly ObservableCollection<MediaChoice> media = [];
    private readonly Func<NewPackageDraft, Task> createPackage;
    private readonly string? setName;
    private bool creating;

    public NewPackageWindow(Func<NewPackageDraft, Task> createPackage, string? setName = null)
    {
        this.createPackage = createPackage;
        this.setName = setName;
        InitializeComponent();
        if (setName is not null)
        {
            Title = DialogHeading.Text = $"Create {setName}";
            ProgramInput.Text = setName;
            CredentialsInputs.Visibility = Visibility.Collapsed;
            PlaylistHeading.Text = "Set pages";
            AddPagesButton.Content = "Add pages…";
            CreateButton.Content = "Create set";
        }

        Playlist.ItemsSource = media;
        Loaded += (_, _) => ProgramInput.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = setName is null ? "Add pages to the USB program" : $"Add pages to {setName}",
            Filter = "Supported media|*.jpg;*.jpeg;*.png;*.mp4",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var firstAdded = media.Count;
        foreach (var path in dialog.FileNames)
        {
            media.Add(new MediaChoice(path));
        }

        Playlist.SelectedIndex = firstAdded;
        Playlist.ScrollIntoView(Playlist.SelectedItem);
        UpdatePlaylistControls();
    }

    private void Playlist_Changed(object sender, SelectionChangedEventArgs e) => UpdatePlaylistControls();

    private void UpdatePlaylistControls()
    {
        var index = Playlist.SelectedIndex;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < media.Count - 1;
        RemoveButton.IsEnabled = index >= 0;
        EmptyPlaylist.Visibility = media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MovePage(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MovePage(1);

    private void MovePage(int distance)
    {
        var current = Playlist.SelectedIndex;
        var next = current + distance;
        if (current >= 0 && next >= 0 && next < media.Count)
        {
            media.Move(current, next);
            Playlist.SelectedIndex = next;
            Playlist.ScrollIntoView(Playlist.SelectedItem);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var index = Playlist.SelectedIndex;
        if (index >= 0)
        {
            media.RemoveAt(index);
            Playlist.SelectedIndex = Math.Min(index, media.Count - 1);
            UpdatePlaylistControls();
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var draft = new NewPackageDraft(ProgramInput.Text.Trim(), UserInput.Text.Trim(), PasswordInput.Password,
            media.Select(choice => choice.Path).ToArray());
        var missingCredentials = setName is null && (string.IsNullOrWhiteSpace(draft.PlayerUsername) ||
            string.IsNullOrWhiteSpace(draft.PlayerPassword));
        if (string.IsNullOrWhiteSpace(draft.ProgramName) || missingCredentials || draft.MediaPaths.Count == 0)
        {
            SetFeedback(setName is null
                ? "Enter a program name and the player credentials, and add at least one media file."
                : "Enter a program name and add at least one page.", error: true);
            return;
        }

        creating = true;
        Inputs.IsEnabled = CreateButton.IsEnabled = CancelButton.IsEnabled = false;
        WorkingProgress.Visibility = Visibility.Visible;
        SetFeedback(setName is null ? "Preparing the program locally…" : $"Preparing {setName} locally…");
        try
        {
            await createPackage(draft);
            PasswordInput.Clear();
            creating = false;
            DialogResult = true;
        }
        catch (InvalidDataException ex)
        {
            SetFeedback(ex.Message, error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetFeedback("The program could not be created. Check the program name and selected files.", error: true);
        }
        finally
        {
            creating = false;
            Inputs.IsEnabled = CreateButton.IsEnabled = CancelButton.IsEnabled = true;
            WorkingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void SetFeedback(string text, bool error = false)
    {
        Feedback.Text = text;
        Feedback.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        e.Cancel |= creating;
    }

    protected override void OnClosed(EventArgs e)
    {
        PasswordInput.Clear();
        base.OnClosed(e);
    }

    private sealed record MediaChoice(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }
}
