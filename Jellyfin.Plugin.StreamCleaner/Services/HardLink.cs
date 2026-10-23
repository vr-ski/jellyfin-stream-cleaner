using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>
/// Cross-platform hard link creation. <c>File.CreateHardLink</c> does not exist
/// until .NET 11, so this wraps the OS primitive directly.
/// </summary>
internal static partial class HardLink
{
    /// <summary>Creates a hard link at <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>.</summary>
    /// <exception cref="IOException">The OS refused to create the link.</exception>
    public static void Create(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero))
            {
                throw new IOException(
                    $"CreateHardLinkW failed for '{linkPath}' -> '{targetPath}'.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            return;
        }

        // link(2): oldpath = existing target, newpath = the new directory entry.
        if (LinkUnix(targetPath, linkPath) != 0)
        {
            throw new IOException(
                $"link(2) failed for '{linkPath}' -> '{targetPath}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkUnix(string oldpath, string newpath);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkWindows(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
