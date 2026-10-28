# Load tests (SRS §7.3)

The load test signs staff in, has them search, open and register patients and list visits and appointments as a busy
registration and OPD counter does, and compares the server's answers with the targets in SRS §7.3. It is
`tools/HyFib.HealthCare360.LoadTest`, a console program that needs nothing but .NET.

| Target | What the load test measures |
|---|---|
| PERF-01: p95 within 300 ms for reads and 600 ms for writes, at 75 concurrent users | Opening a patient, the patient banner, a patient's visits, today's appointments, the visit list, who am I (reads); registering a patient (write) |
| PERF-02: patient search within 500 ms over a million patients | Search by name (the trigram index), by mobile number and by UHID, over the synthetic patients below |
| PERF-05: lists paginated and compressed, under 100 KB | The largest page (200) of the patient, search, visit and appointment lists, as sent: compressed (br or gzip) and under 100 KB |

Each virtual user pauses between steps for half to one and a half times `--think` (3 s), as a person reads the screen;
about a third of the steps are searches and one in ten registers a patient. The users start evenly over `--ramp`, and
only their requests after the last one has started are measured, for `--duration`. A request that fails (HTTP 400 or
above, or no answer) counts as failed, never as fast. More than 1% failed requests fail the run whatever the timings.

Not measured by the load test: PERF-03 (the staff app's load time, which a browser measures; see "The staff app's load
time" below) and PERF-04 (real-time updates within 2 s). PERF-04's server share is measured by `RealtimeLatencyTests` in the integration tests, which time a queue call, a
bed change and a critical lab value from the request to the event arriving at a screen subscribed to the hub; on site,
time the same three changes from one workstation to a second one on the hospital's WiFi (a stopwatch is enough at 2 s).

## Running it against a test Server Node

Use a server set up for testing, on the hospital's hardware: the figures mean something only there. Never the live
server — the load test registers patients.

1. Install HC360 as for a hospital (DEPLOYMENT.md), then add `"SeedDemoData": true` under `HealthCare360` in
   `config\hc360.settings.json` and restart the service: the test server then has demonstration data, which the
   commands below require. Do it before anyone registers a patient there: the database is then marked as made for
   testing, and stays so. HC360 does not start with demonstration data on a database that holds patients and is not
   (a hospital's records). Turn two-step verification off for the test (`HealthCare360:Security:EnforceMfa` false).
2. From an elevated prompt on the server, add synthetic patients and the load test's accounts:
   ```powershell
   $server = 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe'
   & $server loadtest patients --count 1000000
   $env:HC360_LOADTEST_PASSWORD = Read-Host 'A password for the load test accounts (12 characters or more)'
   & $server loadtest accounts --count 8
   ```
   - `loadtest patients` adds patients after any added before, so it can run in parts. Their UHIDs start with `LT`,
     their mobile numbers run from +91 90000 00001, and registering them sends nothing to anyone. About 700 are added a
     second on a CI runner, so a million take some 25 minutes; the database grows with the patients and their audit
     trail.
   - `loadtest accounts` makes `loadtest01` to `loadtest08`, Front Office accounts with that password, and prints their
     names. Run again, it gives the same accounts the new password. Each account may make 200 requests in 10 seconds,
     so make one for every ten users or so.
   - Both refuse on a server without demonstration data.
3. From a workstation on the hospital network, with the .NET SDK:
   ```powershell
   $env:HC360_LOADTEST_PASSWORD = Read-Host 'The load test accounts password'
   dotnet run --project tools/HyFib.HealthCare360.LoadTest -c Release -- run `
       --server https://HC360-SRV:7360/ --ca .\hc360-hospital-ca.crt `
       --accounts loadtest01,loadtest02,loadtest03,loadtest04,loadtest05,loadtest06,loadtest07,loadtest08 --test-server `
       --users 75 --ramp 2m --duration 10m --report load-test.md
   ```
   `--ca` is the hospital CA certificate (from `https://HC360-SRV:7360/api/v1/system/ca-certificate`): the load test
   trusts the server only through it, as HC360 Desktop does. Sign-in allows 10 a minute from one address, so the load
   test waits when it must.

The report lists each step's p50, p95, p99 and slowest time against its target, the lists' sizes, and the failed
requests by status. Exit code 0 means every target was met, 1 that one was missed or more than 1% of requests failed.
`--targets report` reports a missed target without failing. `--targets payloads` fails a run whose lists are over 100 KB
(PERF-05) and only reports the timings: a page's size is the same on any machine.

Run the load test on a different machine from the server, or the load test's own work slows the server down. One
workstation is enough for 75 users. Remove the test server's data afterwards: synthetic patients are not meant to be
kept.

## In CI

`.github/workflows/load.yml` runs the same steps on a GitHub runner: it starts PostgreSQL and the server with
demonstration data, as in production, adds synthetic patients and eight load test accounts, and runs the load test. It runs every Monday (100,000 patients, 75 users,
5 minutes), by hand (Actions › Load test › Run workflow, with the numbers to use), and on a pull request that changes
the load test (20,000 patients, 2 minutes). The runner is a shared machine that also runs the server and the database,
so its timings only compare runs with each other: they are reported in the run's summary, not enforced. The lists'
sizes are enforced (`--targets payloads`): a list whose largest page is over 100 KB as sent fails the run, as do failed
requests.

`tests/HyFib.HealthCare360.IntegrationTests/LoadTestTests.cs` checks on every pull request that each step of the load
test works against the whole server, for a load test account and the front office, and that a short run has no failed
request. With 200 synthetic patients the patient list's largest page is full, and it must be sent compressed under
100 KB; the other lists are held to the size their rows would take on a full page.

`tests/HyFib.HealthCare360.IntegrationTests/PatientSearchPlanTests.cs` holds PERF-02 on every pull request without a
million patients: it asks PostgreSQL for the plan of each kind of search the front desk types (a UHID or its start, a
mobile number, the last digits of either, words of the name) and fails when a column the search looks in is not
matched by an index. Without one, every search of that kind reads the whole register.

## A run over a million patients (October 2026)

What one run showed on a development machine, before the hospital's own: a 4-core, 16 GB Linux virtual machine running
the server, PostgreSQL 17 and the load test together, so the figures are the server's worst case rather than a Server
Node's. The server ran as in production with demonstration data, from this repository's build, with the steps above
(`loadtest patients --count 1000000`, `loadtest accounts --count 4`, then `run --targets report`).

The first run, 20 users for 3 minutes, missed PERF-02: searching by mobile number took 897 ms at p95 and by UHID 948 ms
(by name, 272 ms). Each read the whole register: there was no index for the alternate mobile number, and the UHID index
could not serve a search by its start, nor either index one by its last digits. On their own, with the machine idle,
the two searches took 151 ms and 166 ms; under load they queued behind each other. (Other builds were running on the
machine during that run, which made it slower still.)

With the indexes added (migration `PatientSearchIndexes`) the same searches take 0.5 ms and 0.6 ms on their own. The
runs after it, with the machine otherwise idle:

| Run | Search by name | by mobile | by UHID | Slowest other read (p95) | Register (p95) | Failed |
|---|--:|--:|--:|--:|--:|--:|
| 20 users, 3 minutes | 156 ms | 9 ms | 78 ms | 11 ms | 15 ms | 0 |
| 75 users, 5 minutes (7,435 requests) | 171 ms | 9 ms | 93 ms | 13 ms | 15 ms | 0 |

Every PERF-01, PERF-02 and PERF-05 target was met in both, the largest page of every list at most 14 KB as sent. This
says the code meets the targets with room to spare on modest hardware; the run that decides go-live is still the one
on the hospital's Server Node, over its WiFi, with the load test on a workstation.

## The staff app's load time (PERF-03)

PERF-03 asks for the staff app within 3 s the first time and within 1 s from the browser's cache. One run in October
2026 timed it with Playwright's Chromium against the HC360 server (as installed: HTTPS, compression, the cache headers
of Program.cs), on the same development machine: from navigating to the address to the sign-in form taking typing,
seven times each, in a fresh browser profile (first run) and then again in it (cached). The second profile is a busy
WiFi link to a modest workstation, set through the Chrome DevTools protocol: 20 Mbit/s down, 40 ms round trip, the CPU
four times slower.

| Profile | First run (median, max) | Cached (median, max) | Transferred, first run |
|---|--:|--:|--:|
| Unthrottled | 414 ms, 984 ms | 195 ms, 222 ms | 595 KB |
| WiFi 20 Mbit/s, 40 ms, CPU × 4 | 1,621 ms, 1,817 ms | 817 ms, 898 ms | 595 KB |

Most of the time is the browser parsing and running the code, not the transfer, and the cached second is the tighter
target. `apps/staff/a11y/load-budget.spec.ts`, in the browser checks CI runs on every pull request, holds the code the
sign-in page fetches to 620 KB as the server compresses it (516 KB in that run), about where the throttled cached load
would reach a second. On site, open the staff app on a ward workstation over the hospital's WiFi, once after clearing
the browser's cache and once again, and time each to the sign-in form.
