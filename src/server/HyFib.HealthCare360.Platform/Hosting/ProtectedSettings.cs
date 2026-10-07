using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>Protects one secret of the settings file, and opens it again.</summary>
public interface ISettingProtector
{
    string Protect(string plaintext);

    /// <exception cref="CryptographicException">The value was protected on another machine, or is damaged.</exception>
    string Unprotect(string protectedValue);
}

/// <summary>
/// Windows DPAPI for this machine (SRS SEC-07). A protected value opens only on the machine that protected it, so a copy
/// of the settings file is no use elsewhere. On the Server Node the file itself can be read only by SYSTEM,
/// Administrators and the service's own account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSettingProtector : ISettingProtector
{
    private static readonly byte[] Entropy = "HyFib.HealthCare360.Settings.v1"u8.ToArray();

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine));

    public string Unprotect(string protectedValue)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The protected value is not valid base64.", ex);
        }
    }
}

/// <summary>
/// Secrets in the Server Node's settings file (<c>config\hc360.settings.json</c>) are kept protected with DPAPI as
/// <c>dpapi:</c> followed by the protected bytes in base64 (SRS SEC-07, ASVS V13.3.1). The file is read through
/// <see cref="AddProtectedJsonFile"/>, which opens them as it loads, so the rest of the server sees the plain values.
/// <c>HyFib.HealthCare360.Server.exe settings protect</c>, which the installer runs, protects the secrets written in
/// plain text. Environment variables (<c>HC360_</c>, used in containers) are read as they are.
/// </summary>
public static class ProtectedSettings
{
    public const string FileName = "hc360.settings.json";
    public const string Prefix = "dpapi:";

    // Written for people to read: base64's "+" and connection strings' "&" stay as they are.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Config, FileName);

    /// <summary>DPAPI on Windows; null elsewhere (development machines and containers), where nothing is protected.</summary>
    public static ISettingProtector? ForThisMachine() => OperatingSystem.IsWindows() ? new DpapiSettingProtector() : null;

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Settings that hold a secret: every connection string (it carries a password), and every setting whose name ends in
    /// Password, Secret or ApiKey, such as <c>HealthCare360:Network:CertificatePassword</c>.
    /// </summary>
    public static bool IsSecret(string key)
    {
        var segments = key.Split(ConfigurationPath.KeyDelimiter);
        if (segments.Length > 1 && segments[0].Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = segments[^1];
        return name.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Secret", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The keys of the secrets the file holds in plain text (never their values). Empty when there is no file.</summary>
    public static IReadOnlyList<string> PlainSecrets(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var plain = new List<string>();
        Visit(Read(path), null, (key, value) =>
        {
            if (IsSecret(key) && !string.IsNullOrEmpty(value) && !IsProtected(value))
            {
                plain.Add(key);
            }

            return null;
        });
        return plain;
    }

    /// <summary>
    /// Protects every secret the file holds in plain text, and returns their keys. Protected values and other settings are
    /// left as they are. The file is rewritten only when something changed, through a temporary file, so a failure leaves
    /// it whole, and with a byte order mark when it had one (Windows PowerShell 5.1 reads UTF-8 without one as ANSI).
    /// Comments in the file are not kept.
    /// </summary>
    public static IReadOnlyList<string> ProtectFile(string path, ISettingProtector protector)
    {
        var hadByteOrderMark = File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var root = Read(path);
        var protectedKeys = new List<string>();
        Visit(root, null, (key, value) =>
        {
            if (!IsSecret(key) || string.IsNullOrEmpty(value) || IsProtected(value))
            {
                return null;
            }

            protectedKeys.Add(key);
            return Prefix + protector.Protect(value);
        });

        if (protectedKeys.Count > 0)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions) + Environment.NewLine, new UTF8Encoding(hadByteOrderMark));
            File.Move(temp, path, overwrite: true);
        }

        return protectedKeys;
    }

    /// <summary>A protected value opened, or a clear error that names the setting (never its value).</summary>
    internal static string Reveal(string key, string value, ISettingProtector? protector)
    {
        if (protector is null)
        {
            throw new InvalidOperationException(
                $"The setting {key} in {FileName} is protected with Windows DPAPI, and can be read only on the Windows machine that protected it.");
        }

        try
        {
            return protector.Unprotect(value[Prefix.Length..]);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"The setting {key} in {FileName} cannot be opened on this machine: it was protected on another machine, or it is damaged. " +
                "Replace it with its plain value and run \"HyFib.HealthCare360.Server.exe settings protect\".", ex);
        }
    }

    /// <summary>Reads a settings file as the configuration system does: comments and trailing commas are allowed.</summary>
    private static JsonNode Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? new JsonObject();

    /// <summary>
    /// Calls <paramref name="replace"/> for every string value with its configuration key (<c>Section:Name</c>, array items
    /// by index), and puts back what it returns when that is not null.
    /// </summary>
    private static void Visit(JsonNode node, string? key, Func<string, string, string?> replace)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    var childKey = key is null ? name : ConfigurationPath.Combine(key, name);
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (replace(childKey, text) is { } replaced)
                        {
                            obj[name] = replaced;
                        }
                    }
                    else
                    {
                        Visit(child, childKey, replace);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not { } child)
                    {
                        continue;
                    }

                    var childKey = ConfigurationPath.Combine(key ?? string.Empty, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (replace(childKey, text) is { } replaced)
                        {
                            array[i] = replaced;
                        }
                    }
                    else
                    {
                        Visit(child, childKey, replace);
                    }
                }

                break;
        }
    }
}

/// <summary>A JSON settings file whose <c>dpapi:</c> values are opened as it loads (<see cref="ProtectedSettings"/>).</summary>
public sealed class ProtectedJsonConfigurationSource : JsonConfigurationSource
{
    public ISettingProtector? Protector { get; init; }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ProtectedJsonConfigurationProvider(this);
    }
}

internal sealed class ProtectedJsonConfigurationProvider(ProtectedJsonConfigurationSource source) : JsonConfigurationProvider(source)
{
    public override void Load(Stream stream)
    {
        base.Load(stream);
        var protector = ((ProtectedJsonConfigurationSource)Source).Protector;
        foreach (var (key, value) in Data.ToList())
        {
            if (ProtectedSettings.IsProtected(value))
            {
                Data[key] = ProtectedSettings.Reveal(key, value!, protector);
            }
        }
    }
}

public static class ProtectedSettingsConfigurationExtensions
{
    /// <summary>
    /// Adds the site's settings file (optional, read once at start-up), opening its protected values with
    /// <paramref name="protector"/> (by default DPAPI on Windows).
    /// </summary>
    public static IConfigurationBuilder AddProtectedJsonFile(this IConfigurationBuilder builder, string path, ISettingProtector? protector = null)
    {
        var source = new ProtectedJsonConfigurationSource
        {
            Path = path,
            Optional = true,
            ReloadOnChange = false,
            Protector = protector ?? ProtectedSettings.ForThisMachine(),
        };
        source.ResolveFileProvider();
        return builder.Add(source);
    }
}
