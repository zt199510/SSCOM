using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sscom.Services;

public sealed class QuickSendEntryData
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public bool IsHex { get; set; }
}

public sealed class QuickSendGroupData
{
    public string Name { get; set; } = "";
    public List<QuickSendEntryData> Entries { get; set; } = new();
}

public sealed class QuickSendFile
{
    public int Version { get; set; } = QuickSendStore.CurrentVersion;
    public List<QuickSendGroupData> Groups { get; set; } = new();
}

// The same format is used for the auto-saved file and for import/export.
public static class QuickSendStore
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sscom", "quick-send.json");

    // Throws for unreadable or malformed files so callers never silently replace user data.
    public static List<QuickSendGroupData> Load(string path)
    {
        var file = JsonSerializer.Deserialize<QuickSendFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("文件内容为空。");
        if (file.Version != CurrentVersion) throw new InvalidDataException($"不支持的文件版本 {file.Version}。");
        if (file.Groups == null) throw new InvalidDataException("缺少 groups。");
        foreach (var group in file.Groups)
        {
            if (group == null || group.Entries == null || group.Entries.Any(e => e == null))
                throw new InvalidDataException("分组或条目格式无效。");
            group.Name ??= "";
            foreach (var entry in group.Entries)
            {
                entry.Name ??= "";
                entry.Content ??= "";
            }
        }
        return file.Groups;
    }

    public static void Save(string path, List<QuickSendGroupData> groups)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new QuickSendFile { Groups = groups }, Options);
        // Write beside the target first so a crash mid-write cannot truncate the saved groups.
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
    }
}
