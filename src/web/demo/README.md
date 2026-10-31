# The HealthCare360 demo

A shareable, read-only demo of the staff app and the patient portal. A visitor picks a role (front office, doctor,
nurse, lab, pathologist, radiographer, radiologist, pharmacist, billing, hospital administrator, system administrator,
or a patient on the portal) and uses the real app, unchanged, as that role. Behind it there is no server: the app
replays what the real server answered when each role's screens were opened on a recorded morning at a sample hospital.
Anything that would change a record is refused with "This is a read-only demo".

It is made in five steps, each a script in this folder:

| Step             | Script        | What it does                                                                                                               |
| ---------------- | ------------- | -------------------------------------------------------------------------------------------------------------------------- |
| 1. The day       | `story.mjs`   | Plays a morning on a demo server through its API: patients registered, seen, tested, billed, admitted, discharged, triaged |
| 2. The recording | `capture.mjs` | Signs in as each role in Chromium (the patient on the portal), walks its screens (`lib/walk.mjs`) and keeps every answer   |
| 3. The build     | `build.mjs`   | Scrubs the answers, packs them into data files, and builds both apps around a replay (`app/`)                              |
| 4. The check     | `verify.mjs`  | Walks the built demo the same way and fails when a guided place has unrecorded data or errors                              |
| 5. Publishing    | `site/`       | The checked folder, committed as `site/`, is what https://hc360.hyfib.com serves (below)                                   |

The replay's rules (what it answers a search, another day, another role, a write, a patient's sign-in) have tests of
their own, run in CI: `node --test "demo/**/*.test.ts"`. CI also type-checks the demo against each app it starts:
`tsc -p demo` (the staff app) and `tsc -p demo/tsconfig.portal.json` (the patient portal, whose `@/` is its own).

## Making it

You need a demo server: the HC360 server from this repository with the demo seed. It must run on a test machine with
an empty database. **Never on a hospital's server**: the story writes patients, bills and admissions.

```bash
# The server (Development is what makes the demo accounts; the password must not contain the hospital's name)
ASPNETCORE_ENVIRONMENT=Development
HC360_HealthCare360__SeedDemoData=true
HC360_HealthCare360__Bootstrap__AdminPassword=<demo password>
HC360_HealthCare360__Bootstrap__RequirePasswordChange=false
HC360_HealthCare360__Security__EnforceMfa=false
HC360_HealthCare360__Security__AdministrationRequiresDesktop=false
HC360_HealthCare360__DefaultFacilityCode=DM
HC360_HealthCare360__DefaultFacilityName="HC360 Demo Hospital"
# The walk signs in often and opens many screens quickly:
HC360_HealthCare360__RateLimits__SignIn__PermitLimit=2000
HC360_HealthCare360__RateLimits__PerUser__TokenLimit=4000
HC360_HealthCare360__RateLimits__PerUser__TokensPerPeriod=2000
```

The story plays the morning at the hospital's times, from 9:00 to 11:10 am, so its screens show a believable
timeline (lab turnaround, waiting times, who was seen when). For that the server and its database take their clock
from one file, through libfaketime, and the story moves it on as it goes. At the end it stops the clock at 11:10 am,
the moment the demo shows, so every screen is recorded at that one moment. On Linux:

```bash
echo "+$(( $(TZ=Asia/Kolkata date -d 'today 09:00' +%s) - $(date +%s) ))" > /var/tmp/hc360-demo-clock.txt
export FAKETIME_TIMESTAMP_FILE=/var/tmp/hc360-demo-clock.txt FAKETIME_CACHE_DURATION=1 FAKETIME_DONT_FAKE_MONOTONIC=1
# Start PostgreSQL with LD_PRELOAD=…/faketime/libfaketime.so.1, then the server (on an empty database) with
# LD_PRELOAD=…/faketime/libfaketimeMT.so.1. Its demo seed is made at 9:00 am.
```

Then, from `src/web` (Node 22.18 or later, after `pnpm install` and `pnpm -r --filter './apps/**' build`: the server
serves both apps):

```bash
export HC360_DEMO_PASSWORD='<demo password>' NODE_EXTRA_CA_CERTS=<the server's CA certificate>
export HC360_DEMO_DB=postgresql://<user>:<password>@localhost:5432/<demo database>   # the patient's sign-in code
DEMO_CLOCK_FILE=/var/tmp/hc360-demo-clock.txt node demo/story.mjs demo/out/story.json   # about 3 min
DEMO_PARALLEL=4 node demo/capture.mjs demo/out/recordings                                # 40 to 60 min
node demo/build.mjs demo/out/recordings demo/out/story.json demo/out/demo
node demo/verify.mjs demo/out/demo demo/out/verify      # verify.md and a picture of every guided place
```

The demo's clock starts where the story stopped the server's, and runs on from there, so the recorded morning is
"today" in the demo.

The patient (`PORTAL_PATIENT` in `app/personas.ts`) signs in to the portal on the server's patients' port
(`HC360_PORTAL`, `https://localhost:7361` by default) with a code sent by SMS. A demo server has no SMS gateway, so
`capture.mjs` reads the code the server queued for one from its database, with `psql` and `HC360_DEMO_DB`.

## Publishing at hc360.hyfib.com

https://hc360.hyfib.com serves `site/`: a built folder that `verify.mjs` checked, committed as it was built. Cloudflare
serves it, as the Worker `hc360-demo` with static assets only (no Worker script runs). The _Demo site_ workflow
(`.github/workflows/demo-site.yml`) deploys it with Wrangler when it changes on `main`, and can be run by hand.

- **What it deploys.** `site/` as committed, with the headers in `.github/demo-site/_headers`: HTTPS only, no type
  sniffing, no referrer, and no framing by other sites.
- **What it checks.** After a deploy it fetches each file from the live site and fails if one is not the file
  committed. It also fails if a header is missing or Cloudflare is not in front.
- **Check only.** Run by hand with _check only_, it deploys nothing and checks the live site.

`site/` holds the demo built from the head of #138 (6a7b6e5): 12 roles, 1,336 screens walked, no unrecorded calls, no
browser errors, and 30 of 30 guided places. Building that commit again from the same recordings gives the same 16 files,
byte for byte. To publish a new recording, make it and check it as above, then replace the folder in a pull request:

```bash
rm -rf demo/site && cp -r demo/out/demo demo/site
```

It is set up once, in the Cloudflare account that holds the `hyfib.com` zone, by its administrator. Until then, a run of
the workflow says what is missing and deploys nothing. The deploy token can deploy this one Worker and nothing else.
Creating a Worker and giving it a Custom Domain need more than that, so they are done once, by hand:

1. **The Worker.** In **Workers & Pages › Create**, start from _Hello World_, name it `hc360-demo`, and deploy it. The
   first run of the workflow replaces it with the demo.
2. **Its address.** In the Worker's **Settings › Domains & Routes › Add › Custom domain**, enter `hc360.hyfib.com`.
   Cloudflare makes the DNS record and the certificate. It refuses a name that already has a `CNAME` record, so delete
   any `hc360` record first.
3. **The deploy token.** In **Manage Account › Account API Tokens**, create a token with one permission: _Workers_,
   scope _Specified Workers_ › `hc360-demo`, role _Editor_.
4. **In this repository's Settings**:
   - In **Environments**, create `demo-site`, and set its deployment branches to `main` only.
   - Add the environment secret `CLOUDFLARE_API_TOKEN`, set to the token.
   - In **Secrets and variables › Actions › Variables**, add `CLOUDFLARE_ACCOUNT_ID`. It is on the **Workers & Pages**
     overview, on the right.

Then run the workflow. Its summary lists each file, each header, and the certificate the site answers with.

## What it replays, and what it does not

- **Reads** answer from the recording. A search the walk did not make is answered by filtering what was recorded. A
  list for another day or another page is answered with an empty list.
- **Signing in and out** works for the demo's user names, with any password, and for the demo patient's mobile
  number on the portal, with any code.
- **Writes are refused** (`demo.read_only`), and the app shows the refusal as it shows any other.
- **Live updates** (SignalR) connect and stay quiet. Video consultations do not connect.
- **The idle screen lock is off**, since there is no PIN to unlock it with.
- **Inside the Artifact viewer**, printing and file downloads are blocked by the viewer itself.
- **The apps are the same builds** as `apps/staff` and `apps/portal`, with one difference (`app/staff.tsx`,
  `app/portal.tsx`): each keeps the screen it is on in memory, not in the address bar. The page that holds the demo
  serves only itself, and its address belongs to the page around it.
- **A screen the walk did not record** gets the app's own "not found"; the demo badge says why, and points to the
  role's tour.

## What the build refuses to ship

`build.mjs` stops if a guided place in `app/personas.ts` was not recorded for its role. It also stops if an answer
still holds a token or a private key. It replaces these before packing:

- the recording machine's folders, with `D:\HC360\Data`
- its private addresses, with `192.0.2.10`
- the name it gives itself (the server name on System information and System health), with `HC360-SRV01`
- its CA certificate's fingerprint, with zeros

It also reports any phone number outside the story's fictional series (90000 0xxxx). The sign-in answers keep their
shape, but the token is a stand-in. Its report sits beside the built folder (`<out-dir>.report.json`).
