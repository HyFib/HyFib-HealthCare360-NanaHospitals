// Packs a recorded day into the demo: the staff app built to replay it in the browser (demo/app), the recorded answers
// scrubbed and split into data files the page loads, and the page itself, ready to publish as one folder.
//
//   node demo/build.mjs <recordings-dir> <story.json> <out-dir> [--partial]
//
// --partial packs whatever roles were recorded (while trying the build out); without it every role of personas.ts
// must be there.
// It stops (exit 1) when a guided place of personas.ts was not recorded for its role, or when an answer still holds
// what must not travel: a token, a private key, a password (a connection string's, or the demo accounts' own, from
// HC360_DEMO_PASSWORD). Paths and addresses of the machine that recorded it are replaced, and what it replaced is
// listed in <out-dir>.report.json beside the folder.

import { createHash } from 'node:crypto';
import { mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { MORNING, PERSONAS } from './app/personas.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const STAFF = resolve(HERE, '../apps/staff');
const STAFF_SRC = join(STAFF, 'src');
const PORTAL_SRC = resolve(HERE, '../apps/portal/src');
const DEMO_APP = join(HERE, 'app');
const PARTIAL = process.argv.includes('--partial');
const [RECORDINGS, STORY, OUT] = process.argv
  .slice(2)
  .filter((a) => !a.startsWith('--'))
  .map((p) => resolve(p));
if (!RECORDINGS || !STORY || !OUT) {
  throw new Error('Usage: node demo/build.mjs <recordings-dir> <story.json> <out-dir>');
}

/** The demo accounts' password, which no answer may hold (it signs in to the recording server). */
const DEMO_PASSWORD = process.env.HC360_DEMO_PASSWORD;
/** Data files stay well under the 16 MB a published text file may be. */
const PART_BYTES = 6_000_000;
/** The fictional mobile numbers story.mjs gives patients (90000 0xxxx): any other is reported. */
const FICTIONAL_PHONE = /^90000/;

const report = { roles: {}, replaced: {}, phones: [], refused: [], files: [] };
const note = (what) => (report.replaced[what] = (report.replaced[what] ?? 0) + 1);

// ------------------------------------------------------------------------------------------------- the recordings

const story = JSON.parse(readFileSync(STORY, 'utf8'));
const failedScenes = story.scenes.filter((s) => !s.ok);
if (failedScenes.length) {
  throw new Error(`The story did not make every scene: ${failedScenes.map((s) => s.name).join('; ')}`);
}

const recordings = readdirSync(RECORDINGS)
  .filter((f) => f.endsWith('.json'))
  .map((f) => JSON.parse(readFileSync(join(RECORDINGS, f), 'utf8')));
if (recordings.length === 0) {
  throw new Error(`No recordings in ${RECORDINGS}: run capture.mjs first.`);
}

/**
 * Where the recording machine shows through: its data folder and user paths, its addresses, the name it gives
 * itself. Each is replaced with a stand-in a hospital server would show.
 */
const MACHINE_PATHS = ['/tmp', '/var/tmp', '/home', '/root'];
const PRIVATE_ADDRESS = /\b(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])|192\.168)\.\d{1,3}\.\d{1,3}\b/g;
/** The name the demo's server goes by instead. */
const SERVER_STAND_IN = 'HC360-SRV01';

/**
 * The names the recording machine gives itself, as the server reported them (System information and System health),
 * matched whole wherever they show: in those answers, in the certificate's names (vm.local) and in any message.
 */
function serverNames() {
  const names = new Set();
  for (const recording of recordings) {
    for (const [key, entry] of Object.entries(recording.entries)) {
      if (!entry.body || entry.encoding !== 'utf8' || entry.status !== 200) continue;
      if (key === 'GET /api/v1/system/info') names.add(JSON.parse(entry.body).serverName);
      if (key === 'GET /api/v1/admin/system/health') names.add(JSON.parse(entry.body).server?.machineName);
    }
  }
  return [...names]
    .filter((name) => typeof name === 'string' && name.trim() && name !== 'localhost')
    .map((name) => new RegExp(`(?<![\\w-])${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![\\w-])`, 'g'));
}
const SERVER_NAMES = serverNames();

function scrub(text, where) {
  let out = text;
  for (const path of MACHINE_PATHS) {
    // The folder itself or anything below it, up to the end of the JSON string or the sentence it is in.
    const pattern = new RegExp(`(?<=["\\s(])${path}(?:/[^"\\s,)]*)?(?=["\\s,)])`, 'g');
    out = out.replace(pattern, () => {
      note('machine path');
      return 'D:\\\\HC360\\\\Data';
    });
  }
  // The fingerprint of the recording server's own certificate authority, which no viewer can check against anything.
  out = out.replace(/"caFingerprint":"[0-9A-F]+"/g, () => {
    note('certificate fingerprint');
    return `"caFingerprint":"${'0'.repeat(64)}"`;
  });
  out = out.replace(PRIVATE_ADDRESS, () => {
    note('private address');
    return '192.0.2.10';
  });
  for (const name of SERVER_NAMES) {
    out = out.replace(name, () => {
      note('server name');
      return SERVER_STAND_IN;
    });
  }
  if (/eyJ[\w-]{10,}\.[\w-]{10,}\.[\w-]{10,}/.test(out)) {
    report.refused.push(`${where}: a token`);
  }
  if (/-----BEGIN [A-Z ]*PRIVATE KEY-----/.test(out)) {
    report.refused.push(`${where}: a private key`);
  }
  if (/\b(?:Password|Pwd)\s*=/i.test(out) || (DEMO_PASSWORD && out.includes(DEMO_PASSWORD))) {
    report.refused.push(`${where}: a password`);
  }
  for (const match of out.matchAll(/(?<![\w*])(?:\+91[\s-]?)?([6-9]\d{4})[\s-]?(\d{5})(?!\w)/g)) {
    const number = match[1] + match[2];
    if (!FICTIONAL_PHONE.test(number)) report.phones.push(`${where}: ${number}`);
  }
  return out;
}

const bodies = [];
const bodyIds = new Map();
function bodyId(text) {
  const hash = createHash('sha256').update(text).digest('base64url');
  let id = bodyIds.get(hash);
  if (id === undefined) {
    id = bodies.length;
    bodies.push(text);
    bodyIds.set(hash, id);
  }
  return id;
}

const roles = {};
/** Which app each role uses: the staff app, or the patient portal. */
const apps = {};
let lastRecorded = 0;
for (const recording of recordings) {
  const { role, user } = recording;
  // Staff sign in with a password; the patient with the code sent to their phone.
  const login = recording.entries['POST /api/v1/auth/login'] ?? recording.entries['POST /api/v1/auth/otp/verify'];
  if (!login?.body) throw new Error(`${role}: the recording has no sign-in answer.`);
  const signIn = JSON.parse(login.body);
  const answers = {};
  for (const [key, entry] of Object.entries(recording.entries)) {
    // Signing in and out, the token's refresh and the health check are the replay's own (replay.ts).
    if (key.startsWith('POST /api/v1/auth/') || key.startsWith('GET /health/')) continue;
    // A read the server refused for a lost race (a 401 before the refresh) would replay as a failure.
    if (entry.status === 401) continue;
    const text =
      entry.body === null ? null : entry.encoding === 'base64' ? entry.body : scrub(entry.body, `${role} ${key}`);
    answers[key] = [entry.status, entry.type, entry.disposition, entry.encoding, text === null ? null : bodyId(text)];
  }
  roles[role] = {
    role,
    userName: user.userName,
    displayName: user.displayName,
    roleName: user.roles.map((r) => r.replace(/([a-z])([A-Z])/g, '$1 $2')).join(', '),
    login: { ...signIn, accessToken: 'demo-access-token', expiresAt: null },
    desk: recording.desk ?? {},
    ...(recording.phone ? { phone: recording.phone } : {}),
    answers,
  };
  apps[role] = user.type === 'patient' ? 'portal' : 'staff';
  report.roles[role] = {
    menu: recording.menu.length,
    notOpened: recording.menu
      .filter((m) => m.error || m.skipped)
      .map((m) => `${m.category ?? ''} › ${m.label}: ${m.error ?? m.skipped}`),
    screens: recording.visited.length,
    answers: Object.keys(answers).length,
    writes: recording.writes.length,
    minutes: recording.minutes,
  };
  lastRecorded = Math.max(lastRecorded, Date.parse(recording.recordedAt));

  // Every guided place of the role was opened while recording.
  const opened = new Set([...recording.visited, ...recording.menu.map((m) => m.path)].map((p) => p?.split('?')[0]));
  for (const place of PERSONAS.find((p) => p.role === role)?.places ?? []) {
    if (!opened.has(place.path))
      report.refused.push(`${role}: the guided place ${place.label} (${place.path}) was not recorded`);
  }
}
for (const persona of PERSONAS) {
  if (!roles[persona.role] && !PARTIAL)
    report.refused.push(`${persona.role}: no recording for a role personas.ts guides`);
}
if (MORNING.length === 0) report.refused.push('personas.ts: the morning is empty');

if (report.refused.length) {
  writeFileSync(`${OUT}.report.json`, JSON.stringify(report, null, 2));
  console.error(`Refused:\n  ${report.refused.join('\n  ')}`);
  process.exit(1);
}

// ------------------------------------------------------------------------------------------------- the app

const fromStaff = createRequire(join(STAFF, 'package.json'));
const { build } = await import(pathToFileURL(fromStaff.resolve('vite')).href);
const { default: react } = await import(pathToFileURL(fromStaff.resolve('@vitejs/plugin-react')).href);

/**
 * Each app's modules resolve as they do in that app's own build: `@/` is the app's own source, and the demo's modules
 * import packages as the app they start does, so there is one copy of each. The portal's are its source and the demo's
 * portal starter (app/portal.tsx); everything else is the staff app's.
 */
function resolveLikeTheApps() {
  const source = { staff: STAFF_SRC, portal: PORTAL_SRC };
  const entry = { staff: join(STAFF_SRC, 'main.tsx'), portal: join(PORTAL_SRC, 'main.tsx') };
  const appOf = (importer) =>
    importer.startsWith(`${PORTAL_SRC}/`) || importer === join(DEMO_APP, 'portal.tsx') ? 'portal' : 'staff';
  return {
    name: 'hc360-demo:resolve-like-the-apps',
    enforce: 'pre',
    async resolveId(id, importer, options) {
      if (!importer) return null;
      const app = appOf(importer);
      if (id.startsWith('@/')) {
        return this.resolve(join(source[app], id.slice(2)), importer, { ...options, skipSelf: true });
      }
      if (!importer.startsWith(DEMO_APP) || /^[./\0]/.test(id)) return null;
      return this.resolve(id, entry[app], { ...options, skipSelf: true });
    },
  };
}

rmSync(OUT, { recursive: true, force: true });
await build({
  configFile: false,
  root: DEMO_APP,
  base: './',
  logLevel: 'warn',
  plugins: [resolveLikeTheApps(), react()],
  resolve: {
    alias: [
      // A recorded day sends no live updates: the realtime hubs connect at once and stay quiet.
      { find: /^@microsoft\/signalr$/, replacement: join(DEMO_APP, 'signalr.ts') },
    ],
  },
  build: {
    outDir: OUT,
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 20_000,
    // Each app's stylesheet comes with it (both set the page's own height and margins); the page's own is written into
    // the page by build.mjs.
    cssCodeSplit: true,
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name]-[hash].js',
        chunkFileNames: 'assets/[name]-[hash].js',
        assetFileNames: 'assets/[name]-[hash][extname]',
      },
    },
  },
});

// ------------------------------------------------------------------------------------------------- the data

mkdirSync(join(OUT, 'data'), { recursive: true });
const files = [];
const writePart = (part) => {
  const name = `data/part-${files.length}.js`;
  writeFileSync(join(OUT, name), `(self.HC360_DEMO_PARTS ||= []).push(${JSON.stringify(part)});\n`);
  files.push(name);
};
writePart({ roles });
for (let start = 0; start < bodies.length;) {
  const list = [];
  let size = 0;
  while (
    start + list.length < bodies.length &&
    (size === 0 || size + bodies[start + list.length].length < PART_BYTES)
  ) {
    size += bodies[start + list.length].length;
    list.push(bodies[start + list.length]);
  }
  writePart({ bodies: [start, list] });
  start += list.length;
}

// The demo's clock starts where the story stopped the server's (the moment every screen was recorded at); for a story
// run on a free-running clock, a few minutes after it finished, before any role's recording began.
const clockStart =
  story.demoClock ?? new Date(Math.min(Date.parse(story.finishedAt) + 5 * 60_000, lastRecorded)).toISOString();
const facility = Object.values(roles)[0].login.user.facilityName ?? 'the demo hospital';
const meta = {
  clockStart,
  facility,
  recordedOn: new Date(clockStart).toLocaleDateString('en-IN', {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: 'Asia/Kolkata',
  }),
  roles: PERSONAS.filter((p) => roles[p.role]).map((p) => {
    const r = roles[p.role];
    return {
      role: r.role,
      userName: r.userName,
      displayName: r.displayName,
      roleName: r.roleName,
      app: apps[p.role],
      ...(r.phone ? { phone: r.phone } : {}),
    };
  }),
  // Every patient's id, for verify.mjs to open each one's record and chart as capture.mjs did.
  patients: [
    ...new Set(
      recordings.flatMap((r) => r.patients ?? r.visited.map((p) => /^\/patients\/([0-9a-f-]{36})$/.exec(p)?.[1])),
    ),
  ].filter(Boolean),
  files,
};
writeFileSync(join(OUT, 'data/meta.js'), `self.HC360_DEMO = ${JSON.stringify(meta)};\n`);

// ------------------------------------------------------------------------------------------------- the page

// The Artifact wraps the page in its own document, so the page is what goes inside it: no html, head or body tags.
const built = readFileSync(join(OUT, 'index.html'), 'utf8');
const scripts = [...built.matchAll(/<script type="module"[^>]*src="([^"]+)"[^>]*><\/script>/g)].map((m) => m[1]);
const styles = [...built.matchAll(/<link rel="stylesheet"[^>]*href="([^"]+)"[^>]*>/g)].map((m) => m[1]);
const css = styles.map((href) => readFileSync(join(OUT, href), 'utf8')).join('\n');
for (const href of styles) rmSync(join(OUT, href));
const page = `<title>HealthCare360 Demo</title>
<meta name="description" content="The HealthCare360 staff app and patient portal replaying a recorded morning at ${facility}: choose a role and look around.">
<style>
${css}
/* The demo badge sits at the foot of the navigation: its last items can scroll up above it. */
.fui-NavDrawerBody { padding-bottom: 112px !important; }
#root:empty::before { content: "Loading the HealthCare360 demo…"; display: block; padding: 32px; font: 15px/1.5 "Segoe UI", system-ui, sans-serif; color: #42474a; }
</style>
<div id="root"></div>
<script src="data/meta.js"></script>
${scripts.map((src) => `<script type="module" src="${src}"></script>`).join('\n')}
`;
writeFileSync(join(OUT, 'index.html'), page);

// ------------------------------------------------------------------------------------------------- the report

const walk = (dir) =>
  readdirSync(dir, { withFileTypes: true }).flatMap((d) =>
    d.isDirectory() ? walk(join(dir, d.name)) : [join(dir, d.name)],
  );
for (const file of walk(OUT)) {
  report.files.push({ file: file.slice(OUT.length + 1), bytes: statSync(file).size });
}
const total = report.files.reduce((sum, f) => sum + f.bytes, 0);
report.summary = {
  files: report.files.length,
  megabytes: Math.round(total / 10_000) / 100,
  bodies: bodies.length,
  clockStart,
};
writeFileSync(`${OUT}.report.json`, JSON.stringify(report, null, 2));
console.log(
  `Demo built in ${OUT}: ${report.files.length} files, ${report.summary.megabytes} MB, ` +
    `${Object.keys(roles).length} roles, ${bodies.length} distinct answers; clock starts ${clockStart}.`,
);
if (report.phones.length) {
  console.log(`Phone numbers outside the fictional series (see the report): ${report.phones.length}`);
}
