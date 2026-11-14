using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HyFib.HealthCare360.LoadTest;

/// <summary>Why the load test could not run: a sign-in that failed, a server that did not answer.</summary>
internal sealed class LoadTestException(string message) : Exception(message);

/// <summary>A staff account the virtual users share, signed in again before its access token runs out.</summary>
internal sealed class Account(string userName, string? fixedToken = null)
{
    /// <summary>Access tokens last 10 minutes unless the server is set otherwise; renewing earlier keeps every request signed in.</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(8);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private TimeSpan _signedInAt;

    public string UserName => userName;

    /// <summary>A token to send now, signing in first when there is none or it is old.</summary>
    public async Task<string> TokenAsync(Func<Account, Task<string>> signIn, Stopwatch clock)
    {
        // A portal session is made by a code sent to the patient's phone: the run is given its token, and cannot renew it.
        if (fixedToken is not null)
        {
            return fixedToken;
        }

        if (_token is { } current && clock.Elapsed - _signedInAt < RenewAfter)
        {
            return current;
        }

        await _lock.WaitAsync();
        try
        {
            if (_token is null || clock.Elapsed - _signedInAt >= RenewAfter)
            {
                _token = await signIn(this);
                _signedInAt = clock.Elapsed;
            }

            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The server refused the token: sign in again before the next request.</summary>
    public void Forget() => _signedInAt = TimeSpan.MinValue / 2;
}

/// <summary>
/// Runs the load test: signs the accounts in, notes the patients there are to open and how large the lists are, then
/// starts the virtual users evenly over the ramp and records every request they make once all have started.
/// </summary>
/// <param name="handler">Makes the connection to the server; <c>false</c> asks for one that does not decompress.</param>
internal sealed class LoadRunner(LoadTestOptions options, Func<bool, HttpMessageHandler> handler, TextWriter log)
{
    /// <summary>Sign-in allows 10 attempts a minute from one address; past that the server says when to try again.</summary>
    private const int SignInTries = 4;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public async Task<LoadTestResult> RunAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler(true), disposeHandler: true) { BaseAddress = options.Server, Timeout = TimeSpan.FromSeconds(30) };

        // The registration desk's accounts first (--accounts), then the other roles' (the peak mix, PeakMix).
        var accountsByRole = new Dictionary<Role, List<Account>> { [Role.FrontOffice] = [.. options.Accounts.Select(name => new Account(name))] };
        foreach (var (role, names) in options.RoleAccounts)
        {
            accountsByRole[role] = [.. names.Select(name => new Account(name))];
        }

        if (options.PortalTokens.Count > 0)
        {
            accountsByRole[Role.Portal] = [.. options.PortalTokens.Select((token, i) => new Account($"portal {i + 1}", token))];
        }

        var staff = accountsByRole.Where(p => p.Key != Role.Portal).SelectMany(p => p.Value).ToList();
        log.WriteLine($"Signing in {staff.Count} account(s) at {options.Server}");
        Task<string> SignIn(Account account) => SignInAsync(http, account.UserName, cancellationToken);
        foreach (var account in staff)
        {
            await account.TokenAsync(SignIn, _clock);
        }

        var frontOffice = accountsByRole[Role.FrontOffice][0];
        var patients = await PatientsAsync(http, await frontOffice.TokenAsync(SignIn, _clock), cancellationToken);
        log.WriteLine($"{patients.Count} patients to open; measuring the lists (PERF-05)");
        var lists = await ListSizesAsync(await frontOffice.TokenAsync(SignIn, _clock), cancellationToken);

        var tokens = new Dictionary<Role, string>();
        foreach (var (role, roleAccounts) in accountsByRole.Where(p => p.Key != Role.Portal))
        {
            tokens[role] = await roleAccounts[0].TokenAsync(SignIn, _clock);
        }

        foreach (var clerk in accountsByRole.GetValueOrDefault(Role.Billing) ?? [])
        {
            await FixtureDiscovery.EnsureShiftAsync(http, await clerk.TokenAsync(SignIn, _clock), log, cancellationToken);
        }

        var fixtures = await FixtureDiscovery.DiscoverAsync(http, tokens, options, log, cancellationToken);
        var mix = PeakMix.Build(accountsByRole.Keys.ToHashSet(), fixtures, options.Weights, out var notRun);
        var split = PeakMix.Split(mix, options.Users);
        foreach (var line in notRun)
        {
            log.WriteLine($"  not run: {line}");
        }

        if (split.Count == 0)
        {
            throw new LoadTestException("There is nothing to run: no step is left in the mix (see the lines above).");
        }

        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Starting {options.Users} users ({string.Join(", ", split.Select(p => $"{p.Value} {PeakMix.AreaOf(p.Key).ToLowerInvariant()}"))}) over {options.Ramp.TotalSeconds:0} s, then measuring for {options.Duration.TotalSeconds:0} s"));
        var samples = new ConcurrentQueue<Sample>();
        var start = _clock.Elapsed;
        var measureFrom = start + options.Ramp;
        var end = measureFrom + options.Duration;
        var numbers = Random.Shared.NextInt64(1, 500_000_000);
        var roleOf = split.SelectMany(p => Enumerable.Range(0, p.Value).Select(n => (Role: p.Key, N: n))).ToList();
        var users = roleOf.Select((user, i) =>
        {
            var roleAccounts = accountsByRole[user.Role];
            var account = roleAccounts[user.N % roleAccounts.Count];
            var operations = mix.Where(o => o.Role == user.Role).ToList();
            var startsAt = start + (options.Users == 1 ? TimeSpan.Zero : options.Ramp * i / options.Users);
            var context = new OperationContext(patients, new Random(unchecked(Environment.TickCount + (i * 7919))), () => Interlocked.Increment(ref numbers), fixtures);
            return UserAsync(http, account, SignIn, context, operations, startsAt, measureFrom, end, samples, cancellationToken);
        }).ToList();

        using var reporting = new CancellationTokenSource();
        var progress = ReportProgressAsync(samples, start, measureFrom, reporting.Token);
        await Task.WhenAll(users);
        await reporting.CancelAsync();
        await progress;

        var measured = samples.ToList();
        return new LoadTestResult(
            options.Server, options.Users, options.Duration, Results.Summarize(mix, measured), lists,
            measured.Count, measured.Count(s => s.Failed),
            measured.Where(s => s.Failed).GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()), notRun);
    }

    private async Task UserAsync(
        HttpClient http, Account account, Func<Account, Task<string>> signIn, OperationContext context, IReadOnlyList<Operation> operations,
        TimeSpan startsAt, TimeSpan measureFrom, TimeSpan end, ConcurrentQueue<Sample> samples, CancellationToken cancellationToken)
    {
        await DelayUntilAsync(startsAt, cancellationToken);
        while (_clock.Elapsed < end && !cancellationToken.IsCancellationRequested)
        {
            var operation = Operations.Choose(operations, context);
            var token = await account.TokenAsync(signIn, _clock);
            using var request = operation.Request(context);
            // The patient portal has a port of its own, away from the staff app's.
            if (operation.Role == Role.Portal && options.PortalServer is not null && request.RequestUri is { IsAbsoluteUri: false } relative)
            {
                request.RequestUri = new Uri(options.PortalServer, relative);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var sentAt = _clock.Elapsed;
            var status = 0;
            var elapsed = TimeSpan.Zero;
            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                elapsed = _clock.Elapsed - sentAt;
                if (response.IsSuccessStatusCode && operation.OnSuccess is { } keep)
                {
                    // What the next step needs from this one's answer; an empty or odd answer leaves the step with nothing to keep.
                    keep(context, await ReadBodyAsync(response, cancellationToken));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // No answer: status 0, a failure.
            }

            if (sentAt >= measureFrom)
            {
                samples.Enqueue(new Sample(operation.Name, operation.Kind, status == 0 ? _clock.Elapsed - sentAt : elapsed, status));
            }

            if (status == (int)HttpStatusCode.Unauthorized)
            {
                account.Forget();
            }

            // A person reads the screen before the next step: half to one and a half times the mean pause.
            var pause = options.Think * (0.5 + context.Random.NextDouble());
            await DelayUntilAsync(TimeSpan.FromTicks(Math.Min((_clock.Elapsed + pause).Ticks, end.Ticks)), cancellationToken);
        }
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private async Task<string> SignInAsync(HttpClient http, string userName, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.PostAsJsonAsync(
                "api/v1/auth/login", new { userName, password = options.Password }, Operations.Json, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < SignInTries)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
                log.WriteLine($"  sign-in is limited for now: waiting {wait.TotalSeconds:0} s");
                await Task.Delay(wait + TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LoadTestException($"{userName} could not sign in: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var result = await response.Content.ReadFromJsonAsync<SignInResult>(Operations.Json, cancellationToken)
                         ?? throw new LoadTestException($"{userName} could not sign in: the server's answer was empty.");
            return result is { AccessToken: { Length: > 0 } token, MfaRequired: false, Restriction: null }
                ? token
                : throw new LoadTestException(
                    $"{userName} could not sign in fully (two-step verification asked: {result.MfaRequired}, restriction: {result.Restriction ?? "none"}). " +
                    "Use accounts without two-step verification on the test server, and with their password changed from the first one.");
        }
    }

    /// <summary>Patients for the users to open: of the latest 200 registered (the synthetic ones when there are any), those not VIPs.</summary>
    private static async Task<IReadOnlyList<Guid>> PatientsAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/patients?pageSize=200");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LoadTestException($"The patient list could not be read: the server answered {(int)response.StatusCode}. Use accounts that may search patients.");
        }

        using var page = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        // A VIP's record opens only through break-glass, with a reason: staff do not open those in passing.
        var ids = page.RootElement.GetProperty("items").EnumerateArray()
            .Where(p => !p.TryGetProperty("isVip", out var vip) || !vip.GetBoolean())
            .Select(p => p.GetProperty("id").GetGuid())
            .ToList();
        return ids.Count > 0 ? ids : throw new LoadTestException("The server has no patients to open: add some with \"HyFib.HealthCare360.Server.exe loadtest patients --count N\".");
    }

    /// <summary>Each list's largest page as sent, compressed and not yet unpacked (PERF-05).</summary>
    private async Task<IReadOnlyList<ListSize>> ListSizesAsync(string token, CancellationToken cancellationToken)
    {
        using var raw = new HttpClient(handler(false), disposeHandler: true) { BaseAddress = options.Server, Timeout = TimeSpan.FromSeconds(30) };
        var sizes = new List<ListSize>();
        foreach (var (name, path) in Operations.Lists)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.AcceptEncoding.ParseAdd("br, gzip");
            using var response = await raw.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
            sizes.Add(new ListSize(name, body.LongLength, encoding, (int)response.StatusCode, response.IsSuccessStatusCode ? Rows(body, encoding) : 0));
        }

        return sizes;
    }

    /// <summary>How many rows a page held (its <c>items</c>), read from the page as sent.</summary>
    internal static int Rows(byte[] body, string? encoding)
    {
        using var sent = new MemoryStream(body);
        using Stream page = encoding switch
        {
            "br" => new BrotliStream(sent, CompressionMode.Decompress),
            "gzip" => new GZipStream(sent, CompressionMode.Decompress),
            _ => sent,
        };
        using var json = JsonDocument.Parse(page);
        return json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
    }

    private async Task ReportProgressAsync(ConcurrentQueue<Sample> samples, TimeSpan start, TimeSpan measureFrom, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var now = _clock.Elapsed;
                var phase = now < measureFrom ? "starting users" : "measuring";
                var failed = samples.Count(s => s.Failed);
                log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {now - start:mm\\:ss} {phase}: {samples.Count:N0} requests measured, {failed:N0} failed"));
            }
        }
        catch (OperationCanceledException)
        {
            // The run is over.
        }
    }

    private async Task DelayUntilAsync(TimeSpan at, CancellationToken cancellationToken)
    {
        var wait = at - _clock.Elapsed;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }

    private sealed record SignInResult(string? AccessToken, bool MfaRequired, string? Restriction);
}
