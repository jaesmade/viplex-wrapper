using System.IO;
using System.Windows;
using Microsoft.Win32;
using ViPlex.Core;

namespace ViPlex.Windows;

public partial class PrepareWindow : Window
{
    public PrepareWindow(string programName, string programSummary, PlaybackMode mode,
        string? destinationRoot = null, string? destinationVolumeIdentity = null, bool writeToUsb = false)
    {
        InitializeComponent();
        ProgramLabel.Text = programName;
        ProgramSummary.Text = programSummary;
        PlugAndPlay.IsChecked = mode == PlaybackMode.PlugAndPlay;
        CopyAndReplace.IsChecked = mode == PlaybackMode.CopyAndReplace;
        if (destinationRoot is not null)
        {
            SetDestination(destinationRoot, destinationVolumeIdentity, writeToUsb);
        }
    }

    public string? DestinationRoot { get; private set; }
    public string? DestinationVolumeIdentity { get; private set; }

    public bool WriteToUsb { get; private set; }

    public PlaybackMode SelectedMode => PlugAndPlay.IsChecked == true
        ? PlaybackMode.PlugAndPlay
        : PlaybackMode.CopyAndReplace;

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder that will contain nova" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        ChooseFolderButton.IsEnabled = ChooseUsbButton.IsEnabled = PublishButton.IsEnabled = false;
        DestinationError.Visibility = Visibility.Collapsed;
        try
        {
            var identity = await Task.Run(() => IdentifyRemovableVolume(folder));
            SetDestination(folder, identity, writeToUsb: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DestinationError.Text = "Windows could not identify the selected destination. Choose the folder again.";
            DestinationError.Visibility = Visibility.Visible;
        }
        finally
        {
            ChooseFolderButton.IsEnabled = ChooseUsbButton.IsEnabled = true;
            PublishButton.IsEnabled = DestinationRoot is not null;
        }
    }

    private void ChooseUsb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DriveWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedRoot is { } root && dialog.SelectedIdentity is { } identity)
        {
            SetDestination(root, identity, writeToUsb: true);
        }
    }

    internal static string? IdentifyRemovableVolume(string folder)
    {
        var volumeRoot = Path.GetPathRoot(folder) ?? throw new IOException("The selected folder has no volume root.");
        return new DriveInfo(volumeRoot).DriveType switch
        {
            DriveType.Removable => UsbVolume.Identity(volumeRoot),
            DriveType.NoRootDirectory => throw new IOException("The selected volume is no longer available."),
            _ => null
        };
    }

    private void SetDestination(string root, string? volumeIdentity, bool writeToUsb)
    {
        DestinationRoot = root;
        DestinationVolumeIdentity = volumeIdentity;
        WriteToUsb = writeToUsb;
        DestinationError.Visibility = Visibility.Collapsed;
        DestinationLabel.Text = Path.Combine(root, "nova");
        PublishButton.Content = WriteToUsb ? "Write to USB" : "Export folder";
        PublishButton.IsEnabled = DestinationRoot is not null;
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationRoot is not null)
        {
            DialogResult = true;
        }
    }
}
