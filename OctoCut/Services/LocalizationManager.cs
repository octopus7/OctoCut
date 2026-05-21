using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OctoCut.Services;

public sealed class LocalizationManager
{
    private const string FallbackLanguageCode = "ko-KR";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, LanguagePack> _languagePacks = new(StringComparer.OrdinalIgnoreCase);

    private LanguagePack? _currentLanguage;
    private LanguagePack? _fallbackLanguage;

    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; private set; } = Array.Empty<LanguageInfo>();

    public string CurrentLanguageCode => _currentLanguage?.Code ?? _fallbackLanguage?.Code ?? FallbackLanguageCode;

    public string LanguageDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public void Reload()
    {
        _languagePacks.Clear();

        if (Directory.Exists(LanguageDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(LanguageDirectory, "*.json"))
            {
                TryLoadLanguagePack(path);
            }
        }

        AvailableLanguages = _languagePacks.Values
            .OrderBy(pack => pack.NativeName, StringComparer.CurrentCulture)
            .Select(pack => new LanguageInfo
            {
                Code = pack.Code,
                NativeName = pack.NativeName,
                FilePath = pack.FilePath
            })
            .ToList();

        _fallbackLanguage = ResolvePack(FallbackLanguageCode) ?? _languagePacks.Values.FirstOrDefault();
        _currentLanguage = ResolvePack(_currentLanguage?.Code) ?? _fallbackLanguage;
    }

    public string DetectPreferredLanguageCode()
    {
        var culture = CultureInfo.CurrentUICulture;
        var candidates = new List<string>();

        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(IsTraditionalChineseCulture(culture.Name) ? "zh-Hant" : "zh-Hans");
        }

        candidates.Add(culture.Name);
        candidates.Add(culture.TwoLetterISOLanguageName);

        foreach (var candidate in candidates)
        {
            var match = ResolvePack(candidate);
            if (match is not null)
            {
                return match.Code;
            }
        }

        return ResolvePack("en-US")?.Code ?? _fallbackLanguage?.Code ?? AvailableLanguages.FirstOrDefault()?.Code ?? FallbackLanguageCode;
    }

    public bool SetLanguage(string? languageCode)
    {
        var pack = ResolvePack(languageCode) ?? _fallbackLanguage;
        if (pack is null)
        {
            return false;
        }

        _currentLanguage = pack;
        return true;
    }

    public string Text(string key)
    {
        if (_currentLanguage?.Strings.TryGetValue(key, out var currentText) == true)
        {
            return currentText;
        }

        if (_fallbackLanguage?.Strings.TryGetValue(key, out var fallbackText) == true)
        {
            return fallbackText;
        }

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, Text(key), args);
        }
        catch (FormatException)
        {
            return Text(key);
        }
    }

    private void TryLoadLanguagePack(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var pack = JsonSerializer.Deserialize<LanguagePack>(json, JsonOptions);
            if (pack is null || string.IsNullOrWhiteSpace(pack.Code) || string.IsNullOrWhiteSpace(pack.NativeName))
            {
                return;
            }

            pack.FilePath = path;
            _languagePacks[pack.Code] = pack;
        }
        catch
        {
            // Invalid user language files are ignored so one bad file does not block app startup.
        }
    }

    private LanguagePack? ResolvePack(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        if (_languagePacks.TryGetValue(languageCode, out var exactPack))
        {
            return exactPack;
        }

        var twoLetterCode = languageCode.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(twoLetterCode))
        {
            return null;
        }

        return _languagePacks.Values.FirstOrDefault(pack =>
            pack.Code.Equals(twoLetterCode, StringComparison.OrdinalIgnoreCase) ||
            pack.Code.StartsWith(twoLetterCode + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTraditionalChineseCulture(string cultureName)
    {
        return cultureName.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
               cultureName.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
               cultureName.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
               cultureName.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LanguagePack
    {
        public string Code { get; set; } = string.Empty;

        public string NativeName { get; set; } = string.Empty;

        public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; set; } = string.Empty;
    }
}
