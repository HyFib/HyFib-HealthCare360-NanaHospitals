#:project ../src/server/HyFib.HealthCare360.Platform/HyFib.HealthCare360.Platform.csproj

// Builds the breached-password set HC360 checks every new password against (SRS SEC-01, ASVS V6.2.12):
//   src/server/HyFib.HealthCare360.Platform/Security/Resources/breached-passwords.gcs
//
// Source: SecLists (MIT licence, https://github.com/danielmiessler/SecLists), Passwords/Common-Credentials/
// Pwdb_top-10000000.txt, the 10 million passwords seen most often in public breach compilations, most common first. The
// commit is pinned so the set can be rebuilt byte for byte.
//
// Kept: printable ASCII, at most 128 characters, at least 5 different characters (the policy refuses fewer anyway), and
//   - every password of 12 characters or more (the default minimum length), from all 10 million;
//   - passwords of 8 to 11 characters from the 250,000 most common, for sites that lower the minimum.
//
// Run from the repository root:
//   dotnet run tools/build-breached-passwords.cs            (downloads the list)
//   dotnet run tools/build-breached-passwords.cs -- <file>  (a copy already downloaded)

using HyFib.HealthCare360.Platform.Security;

const string Commit = "dd50d1561e3cbad84f016a592f7f9371bef7ada9";
const string SourceUrl = $"https://raw.githubusercontent.com/danielmiessler/SecLists/{Commit}/Passwords/Common-Credentials/Pwdb_top-10000000.txt";
const string Output = "src/server/HyFib.HealthCare360.Platform/Security/Resources/breached-passwords.gcs";
const int ShortFromTop = 250_000;

Stream source;
if (args.Length > 0)
{
    source = File.OpenRead(args[0]);
}
else
{
    Console.WriteLine($"Downloading {SourceUrl}");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    source = await http.GetStreamAsync(new Uri(SourceUrl));
}

var kept = new List<string>();
var lines = 0;
using (var reader = new StreamReader(source))
{
    while (reader.ReadLine() is { } line)
    {
        lines++;
        var minimum = lines <= ShortFromTop ? 8 : 12;
        if (line.Length >= minimum && line.Length <= 128 && line.All(c => c is >= ' ' and <= '~') && line.Distinct().Count() >= 5)
        {
            kept.Add(line);
        }
    }
}

using (var output = File.Create(Output))
{
    BreachedPasswords.Write(output, kept);
}

using var check = File.OpenRead(Output);
var set = BreachedPasswords.Read(check);
Console.WriteLine($"Read {lines:N0} passwords, kept {kept.Count:N0}; wrote {set.Count:N0} to {Output} ({new FileInfo(Output).Length:N0} bytes).");
