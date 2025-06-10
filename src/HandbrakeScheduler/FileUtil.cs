namespace HandbrakeScheduler;
public static class FileUtil
{

    public static string GetFileNameWithNewExtension(string filePath, string newExtension)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        if (string.IsNullOrWhiteSpace(newExtension))
            throw new ArgumentException("New extension must start with a dot.", nameof(newExtension));

        if (!newExtension.StartsWith("."))
        {
            newExtension = $".{newExtension}"; // Ensure the new extension starts with a dot
        }
        var fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(filePath);
        return $"{fileNameWithoutExtension}{newExtension}";
    }
}
