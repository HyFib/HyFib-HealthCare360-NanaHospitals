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

Not measured here: PERF-03 (the screens' load time, which a browser measures) and PERF-04
(real-time updates within 2 s).

## Running it against a test Server Node

Use a server set up for testing, on the hospital's hardware: the figures mean something only there. Never the live
server — the load test registers patients.

1. Install HC360 with demonstration data: add `"SeedDemoData": true` under `HealthCare360` in
   `config\hc360.settings.json`, and give the demonstration staff accounts a password with
   `HealthCare360:Bootstrap:AdminPassword` (in the settings file, or the `HC360_HealthCare360__Bootstrap__AdminPassword`
   environment variable for the service) and `"RequirePasswordChange": false` under `Bootstrap`. Turn two-step
   verification off for the test (`HealthCare360:Security:EnforceMfa` false). Start the service once.
2. Add synthetic patients, from an elevated prompt on the server:
   ```powershell
   & 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe' loadtest patients --count 1000000
   ```
   It adds them after any added before, so it can be run in parts. Their UHIDs start with `LT`, their mobile numbers
   run from +91 90000 00001, and registering them sends nothing to anyone. It refuses on a server without demonstration
   data. A million take some minutes; the database grows by a few GB (the patients and their audit trail).
3. From a workstation on the hospital network, with the .NET SDK:
   ```powershell
   $env:HC360_LOADTEST_PASSWORD = Read-Host 'Demonstration accounts password'
   dotnet run --project tools/HyFib.HealthCare360.LoadTest -c Release -- run `
       --server https://HC360-SRV:7360/ --ca .\hc360-hospital-ca.crt `
       --accounts frontoffice,hospitaladmin --test-server `
       --users 75 --ramp 2m --duration 10m --report load-test.md
   ```
   `--ca` is the hospital CA certificate (from `https://HC360-SRV:7360/api/v1/system/ca-certificate`): the load test
   trusts the server only through it, as HC360 Desktop does. The accounts must be allowed to register patients; the
   virtual users share them. Each account may make 200 requests in 10 seconds, so give one account for every 40 or so
   users. Sign-in allows 10 a minute from one address, so the load test waits when it must.

The report lists each step's p50, p95, p99 and slowest time against its target, the lists' sizes, and the failed
requests by status. Exit code 0 means every target was met, 1 that one was missed or more than 1% of requests failed.
`--targets report` reports a missed target without failing.

Run the load test on a different machine from the server, or the load test's own work slows the server down. One
workstation is enough for 75 users. Remove the test server's data afterwards: synthetic patients are not meant to be
kept.

## In CI

`.github/workflows/load.yml` runs the same steps on a GitHub runner: it starts PostgreSQL and the server with
demonstration data, adds synthetic patients and runs the load test. It runs every Monday (100,000 patients, 75 users,
5 minutes), by hand (Actions › Load test › Run workflow, with the numbers to use), and on a pull request that changes
the load test (20,000 patients, 2 minutes). The runner is a shared machine that also runs the server and the database,
so its figures only compare runs with each other: the targets are reported in the run's summary, not enforced. The run
fails when requests fail.

`tests/HyFib.HealthCare360.IntegrationTests/LoadTestTests.cs` checks on every pull request that each step of the load
test works against the whole server, for the accounts the workflow uses, and that a short run has no failed request.
