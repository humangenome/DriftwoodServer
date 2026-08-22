using System.Text;
using System.Text.Json;

namespace DriftwoodServer;

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static void WriteText(string path, string content)
    {
        WriteBytes(path, Utf8WithoutBom.GetBytes(content));
    }

    public static void WriteJson<T>(string path, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        WriteBytes(path, bytes);
    }

    private static void WriteBytes(string path, ReadOnlySpan<byte> content)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase));
        return options;
    }
}
