using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ViPlex.Core;

public static class UsbVolume
{
    public static string Identity(string driveRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USB volume identification requires Windows.");

        var root = Path.GetPathRoot(Path.GetFullPath(driveRoot))!;
        var volume = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, volume, (uint)volume.Capacity))
            throw new IOException($"Windows could not identify the volume at {root}.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return volume.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint bufferLength);
}
