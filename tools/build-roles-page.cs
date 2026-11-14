// Builds the shareable roles page from docs/security/roles.json, which RolesInventoryTests writes from the code, and the
// template tools/roles-page.html: one HTML file with the data inside it, to open in a browser or to publish.
//
// The page names the commit that last changed roles.json, with that commit's date, never the time of the build, so the
// same commit always builds the same bytes. The build refuses:
//   - a roles.json with uncommitted changes, or whose last change a shallow clone cannot see: the page would name a commit
//     its data did not come from;
//   - a roles.json version the template does not read (docs/security/ROLES.md, "The same data as JSON").
//
// Run from the repository root:
//   dotnet run tools/build-roles-page.cs                             (writes artifacts/roles.html, to open in a browser)
//   dotnet run tools/build-roles-page.cs -- <file.html>
//   dotnet run tools/build-roles-page.cs -- <file.html> --fragment  (page content only, for a publisher that wraps it)

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string DataPath = "docs/security/roles.json";
const string TemplatePath = "tools/roles-page.html";
const int ReadsVersion = 1;

var fragment = args.Contains("--fragment");
var output = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Path.Combine("artifacts", "roles.html");

if (!File.Exists(DataPath) || !File.Exists(TemplatePath))
{
    Console.Error.WriteLine($"Run this from the repository root: {DataPath} and {TemplatePath} were not found here.");
    return 1;
}

using var data = JsonDocument.Parse(File.ReadAllText(DataPath));
var version = data.RootElement.GetProperty("version").GetInt32();
if (version != ReadsVersion)
{
    Console.Error.WriteLine(
        $"{DataPath} is version {version} and {TemplatePath} reads version {ReadsVersion}. Update the template for the new fields, then ReadsVersion here.");
    return 1;
}

if (Git("status", "--porcelain", "--", DataPath).Length > 0)
{
    Console.Error.WriteLine($"{DataPath} has uncommitted changes. Commit it first: the page names the commit its data comes from.");
    return 1;
}

var last = Git("log", "-1", "--format=%H %cs", "--", DataPath).Split(' ');
if (last.Length != 2)
{
    Console.Error.WriteLine($"{DataPath} has never been committed.");
    return 1;
}

// A shallow clone's history stops at its boundary commits, where git counts every file as added: when the commit found
// is one of them, the file may have last changed long before it.
var shallow = Git("rev-parse", "--git-path", "shallow");
if (File.Exists(shallow) && File.ReadAllLines(shallow).Contains(last[0], StringComparer.Ordinal))
{
    Console.Error.WriteLine(
        $"This clone's history stops at {last[0][..8]}, so it cannot tell which commit last changed {DataPath}. Fetch more history (git fetch --deepen=200) and build again.");
    return 1;
}

var commit = last[0][..8];
var date = DateOnly.ParseExact(last[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None)
    .ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
var totals = data.RootElement.GetProperty("totals");

var page = File.ReadAllText(TemplatePath)
    .Replace("__DATA__", Inline(data.RootElement), StringComparison.Ordinal)
    .Replace("__COMMIT__", commit, StringComparison.Ordinal)
    .Replace("__DATE__", date, StringComparison.Ordinal)
    .Replace("__ROLES__", Total("roles"), StringComparison.Ordinal)
    .Replace("__PERMISSIONS__", Total("permissions"), StringComparison.Ordinal)
    .Replace("__ENDPOINTS__", Total("guardedEndpoints"), StringComparison.Ordinal)
    .Replace("__APPROVALS__", Total("approvalKinds"), StringComparison.Ordinal);

var unfilled = Regex.Match(page, "__[A-Z]+__");
if (unfilled.Success)
{
    Console.Error.WriteLine($"{TemplatePath} has a placeholder this script does not fill: {unfilled.Value}.");
    return 1;
}

if (!fragment)
{
    // What a publisher's wrapper adds: a document in standards mode with the same small reset.
    page = "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">\n" +
        "<style>:root{color-scheme:light;padding-top:env(safe-area-inset-top,0px);padding-bottom:env(safe-area-inset-bottom,0px)}" +
        "body{margin:0;font:14px system-ui,sans-serif}img{max-width:100%}[hidden]{display:none!important}</style>\n" +
        "</head>\n<body>\n" + page + "</body>\n</html>\n";
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, page);
Console.WriteLine($"Wrote {output} from {DataPath} as of commit {commit} ({date}){(fragment ? ", page content only" : string.Empty)}.");
return 0;

string Total(string name) => totals.GetProperty(name).GetInt32().ToString("N0", CultureInfo.InvariantCulture);

// The data, compact, for inside a <script> element: the writer's default encoder writes <, >, &, quotes and non-ASCII
// letters in strings as \u escapes, so nothing in it can end the element early.
static string Inline(JsonElement element)
{
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        element.WriteTo(writer);
    }

    return Encoding.UTF8.GetString(buffer.ToArray());
}

static string Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var git = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
    var text = git.StandardOutput.ReadToEnd();
    git.WaitForExit();
    return git.ExitCode == 0 ? text.Trim() : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed.");
}
