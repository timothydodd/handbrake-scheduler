using System.Runtime.InteropServices;

namespace HandbrakeScheduler;


public static class PathConverter
{
    public static string NormalizePath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return inputPath;

        // Normalize slashes
        string normalized = inputPath.Replace('\\', '/');

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Already in expected form, return as Windows-style
            return normalized.Replace('/', '\\');
        }
        else
        {
            return normalized.Replace('\\', '/');
        }
    }
}
