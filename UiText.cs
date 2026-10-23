using System.Globalization;
using System.IO;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Markup;
using Microsoft.Win32;

namespace ArkCopyBuffer;

internal static class UiText
{
    internal static readonly ResourceManager Resources = new("ArkCopyBuffer.Strings", typeof(UiText).Assembly);
    static string _language = ReadLanguage();
    internal static string Language
    {
        get => _language;
        set
        {
            _language = value is "en" or "ru" ? value : throw new ArgumentOutOfRangeException(nameof(value));
            ApplyCulture();
        }
    }
    internal static bool IsEnglish => Language != "ru";

    static UiText() => ApplyCulture();

    static void ApplyCulture()
    {
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    static string ReadLanguage()
    {
        var args = Environment.GetCommandLineArgs();
        if (args is [_, "--localization-selftest" or "--selftest", _, var language] && language is "en" or "ru") return language;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\ArkCopyBuffer");
            return key?.GetValue("Language") is "ru" ? "ru" : "en";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "en"; }
    }

    internal static string Key(string source) => "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ReplaceLineEndings("\n"))))[..16];
    internal static string Get(string key) => Resources.GetString(key, CultureInfo.GetCultureInfo(Language))
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");
    internal static string T(string source) => Get(Key(source));
    internal static string F(FormattableString text) => string.Format(CultureInfo.GetCultureInfo(Language), T(text.Format), text.GetArguments());
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(Key);
}
