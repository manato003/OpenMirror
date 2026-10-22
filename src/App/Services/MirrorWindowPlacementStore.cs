using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IPhoneMirror.App.Services;

/// <summary>Where a device's mirror window was last left, in physical pixels.</summary>
internal sealed record MirrorWindowPlacement(int Left, int Top, int Right, int Bottom, bool TopMost);

/// <summary>
/// Reopens each phone's window where the user put it last time. Kept apart
/// from settings.json: it is per-device convenience state that may be
/// discarded at any time without changing behaviour.
/// </summary>
internal static class MirrorWindowPlacementStore
{
    private const int MaximumEntries = 32;
    private static readonly object Gate = new();
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "mirror-windows.json");

    internal static MirrorWindowPlacement? Load(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (Gate)
        {
            if (!ReadAll().TryGetValue(key, out var placement)) return null;
            // A monitor may have been unplugged or rearranged since.
            var rect = new NativeRect(placement.Left, placement.Top, placement.Right, placement.Bottom);
            return placement.Right - placement.Left >= 64 && placement.Bottom - placement.Top >= 64 &&
                MonitorFromRect(ref rect, 0) != 0 ? placement : null;
        }
    }

    internal static void Save(string? key, MirrorWindowPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (Gate)
        {
            try
            {
                var all = ReadAll();
                all.Remove(key);
                all[key] = placement;
                while (all.Count > MaximumEntries) all.Remove(all.Keys.First());
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                var temporary = StorePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all));
                File.Move(temporary, StorePath, overwrite: true);
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("mirror_window", "placement_save_failed", error);
            }
        }
    }

    private static Dictionary<string, MirrorWindowPlacement> ReadAll()
    {
        try
        {
            if (File.Exists(StorePath) &&
                JsonSerializer.Deserialize<Dictionary<string, MirrorWindowPlacement>>(
                    File.ReadAllText(StorePath)) is { } stored)
                return new(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("mirror_window", "placement_load_failed", error);
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
}
