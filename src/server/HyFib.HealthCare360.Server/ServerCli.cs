using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Server;

/// <summary>Offline maintenance commands for administrators on the Server Node.</summary>
internal static class ServerCli
{
    private const string Usage = """
        HyFib HealthCare360 server maintenance

          backup decrypt --in <backup.hc360bak> --out <database.dump> [--key <recovery-key-base64>]
              Decrypts a backup produced by HC360 so it can be restored with:
              pg_restore --clean --if-exists --dbname=hc360 <database.dump>
              Without --key the key is read from the HC360_RECOVERY_KEY environment variable, from standard input
              when it is redirected, or from a hidden prompt, so it never appears in the process list.

        The recovery key is shown once in Administration → Backups → Recovery key. Keep it offline.
        """;

    public static bool IsCommand(string argument) => argument is "backup" or "help" or "--help" or "-h";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["backup", "decrypt", ..])
        {
            var options = ParseOptions(args[2..]);
            if (!options.TryGetValue("in", out var input) || !options.TryGetValue("out", out var output))
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            var key = options.GetValueOrDefault("key") ?? Environment.GetEnvironmentVariable("HC360_RECOVERY_KEY") ?? ReadSecret("Recovery key: ");
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("A recovery key is required.");
                return 2;
            }

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(key);
            }
            catch (FormatException)
            {
                Console.Error.WriteLine("The recovery key is not valid base64.");
                return 2;
            }

            await using var source = File.OpenRead(input);
            await using var destination = File.Create(output);
            try
            {
                await BackupEncryption.DecryptAsync(source, destination, keyBytes, CancellationToken.None);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                Console.Error.WriteLine($"Decryption failed: {ex.Message} (wrong key or damaged file).");
                return 1;
            }

            Console.WriteLine($"Decrypted to {output}. Restore it with pg_restore.");
            return 0;
        }

        Console.WriteLine(Usage);
        return args.Length == 0 || args[0] is "help" or "--help" or "-h" ? 0 : 2;
    }

    private static string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine()?.Trim();
        }

        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString().Trim();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i][2..]] = args[i + 1];
            }
        }

        return result;
    }
}
