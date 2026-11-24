using System.IO;
using System.Windows;
using System.Windows.Controls;
using ViPlex.Core;

namespace ViPlex.Windows;

public partial class DriveWindow : Window
{
    public DriveWindow()
    {
        InitializeComponent();
        Loaded += Refresh_Click;
    }

    public string? SelectedRoot { get; private set; }
    public string? SelectedIdentity { get; private set; }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        Drives.ItemsSource = null;
        DriveStatus.Text = "Checking for removable drives…";
        DriveStatus.Visibility = Visibility.Visible;

        try
        {
            var choices = await Task.Run(GetRemovableDrives);
            Drives.ItemsSource = choices;
            DriveStatus.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DriveStatus.Text = "No removable drives found. Connect a USB drive and refresh.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DriveStatus.Text = "Windows could not enumerate the drives. Refresh to try again.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static List<DriveChoice> GetRemovableDrives()
    {
        var choices = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name))
        {
            try
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    var name = drive.VolumeLabel;
                    var identity = UsbVolume.Identity(drive.Name);
                    choices.Add(new DriveChoice(drive.Name, identity,
                        string.IsNullOrWhiteSpace(name) ? drive.Name : $"{drive.Name}  ·  {name}"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unplugged drive can disappear while Windows enumerates it.
            }
        }

        return choices;
    }

    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        UseButton.IsEnabled = Drives.SelectedItem is DriveChoice;
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (Drives.SelectedItem is not DriveChoice choice)
        {
            return;
        }

        RefreshButton.IsEnabled = UseButton.IsEnabled = Drives.IsEnabled = false;
        try
        {
            var identity = await Task.Run(() => UsbVolume.Identity(choice.Root));
            if (!string.Equals(identity, choice.Identity, StringComparison.Ordinal))
            {
                throw new IOException("The drive has changed since it was listed.");
            }

            if (!IsVisible)
            {
                return;
            }

            SelectedIdentity = identity;
            SelectedRoot = choice.Root;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Drives.ItemsSource = null;
            DriveStatus.Text = "The selected drive is unavailable. Connect it and refresh.";
            DriveStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshButton.IsEnabled = Drives.IsEnabled = true;
        }
    }

    private sealed record DriveChoice(string Root, string Identity, string Label);
}
