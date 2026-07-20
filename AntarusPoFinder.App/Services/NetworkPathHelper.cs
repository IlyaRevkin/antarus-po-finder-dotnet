using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AntarusPoFinder.App.Services;

/// <summary>Resolves the real \\server\share UNC path behind a mapped drive letter (e.g. Z:\...) —
/// used only to make the "connect the inspection folder from a phone" instructions concrete (see
/// InspectionView's PhoneNetworkInstructionsDialog) instead of telling the operator to go ask IT for
/// an address the app already knows. Falls back to returning the path unchanged for anything that
/// isn't a mapped drive (already-UNC paths, or a purely local folder — the instructions dialog
/// handles that case by saying so instead of showing a bogus address).</summary>
public static class NetworkPathHelper
{
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

    /// <summary>Returns the UNC form of <paramref name="path"/> if it lives under a mapped network
    /// drive, or null if it's already a UNC path, a local (non-mapped) path, or resolution fails for
    /// any reason (no network, drive not actually mapped, etc.) — callers must treat null as "don't
    /// know", not as an error.</summary>
    public static string? TryResolveUnc(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return path; // already UNC

            var full = System.IO.Path.GetFullPath(path);
            if (full.Length < 2 || full[1] != ':') return null; // not a drive-letter path
            var driveRoot = full[..2]; // "Z:"

            var sb = new StringBuilder(512);
            var len = sb.Capacity;
            var result = WNetGetConnection(driveRoot, sb, ref len);
            if (result != 0) return null; // NO_ERROR = 0 — anything else means "not a network drive"/error

            var uncRoot = sb.ToString();
            var remainder = full.Length > 2 ? full[2..] : "";
            return uncRoot.TrimEnd('\\') + remainder;
        }
        catch
        {
            return null;
        }
    }
}
