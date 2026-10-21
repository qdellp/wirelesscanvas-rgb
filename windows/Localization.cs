using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WirelessCanvas;

/// <summary>UI language only. Source strings are English; the catalog maps them to ru, zh-Hans, es and pt; numeric parsing and profile serialization retain their existing culture.</summary>
public static class L10n
{
    public static readonly string[] Codes = ["en", "ru", "zh-Hans", "es", "pt"];
    static readonly Lazy<Dictionary<string, string[]>> Catalog = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WirelessCanvas.Translations.json")
            ?? throw new InvalidDataException("Missing translation catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    });
    public static string Language { get; private set; } = "en";
    public static IReadOnlyDictionary<string, string[]> Entries => Catalog.Value;
    public static string Resolve(string? preference, string systemLanguage)
    {
        if (Codes.Contains(preference)) return preference!;
        string language = systemLanguage.Split('-')[0].ToLowerInvariant();
        return language switch { "ru" => "ru", "zh" => "zh-Hans", "es" => "es", "pt" => "pt", _ => "en" };
    }
    public static void Initialize(string preference = "system", string? systemLanguage = null) =>
        Language = Resolve(preference, systemLanguage ?? CultureInfo.CurrentUICulture.Name);
    public static string T(string source)
    {
        if (Language == "en") return source;
        return Catalog.Value.TryGetValue(source, out var translations) ? translations[Array.IndexOf(Codes, Language) - 1] : source;
    }
    public static string F(FormattableString value) => string.Format(CultureInfo.CurrentCulture, T(value.Format), value.GetArguments());
}

public sealed class AppPreferences
{
    public string Language { get; set; } = "system";
    public static string FilePath => Path.Combine(Log.DirectoryPath, "settings.json");
    public static AppPreferences Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return new();
            var value = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new();
            if (value.Language != "system" && !L10n.Codes.Contains(value.Language)) value.Language = "system";
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Profile.Json));
        File.Move(path + ".tmp", path, true);
    }
}
