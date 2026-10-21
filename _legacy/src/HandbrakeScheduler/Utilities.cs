using System.Runtime.InteropServices;
using System.Text;

namespace HandbrakeScheduler;


public static class PathConverter
{
    public static string NormalizePath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return inputPath;

        try
        {
            // Normalize Unicode (important for macOS)
            var normalized = inputPath.Normalize(NormalizationForm.FormC);

            // Handle platform-specific path separators
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
            }
            else
            {
                normalized = normalized.Replace('\\', Path.DirectorySeparatorChar);
            }

            return normalized;
        }
        catch (Exception)
        {
            return inputPath;
        }
    }
}
