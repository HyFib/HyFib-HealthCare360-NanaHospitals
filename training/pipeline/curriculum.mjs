// Writes training/curriculum/README.md from the code and the storyboards: each role's learning path (in go-live wave
// order), which videos teach each module, how much of each role's menu its videos teach (and which links they leave
// to the role to find), and each video's checks.
//
//   node training/pipeline/curriculum.mjs
//
// Inputs: training/curriculum/role-menus.json (pipeline/role-menus.mjs: each role's menu, from the code), every
// training/storyboards/*.mjs, and training/curriculum/qa.json (each video's gates, wherever it was made: make.sh writes
// training/out/<id>/qa.json; `--collect` copies those in).

import { readdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const CURRICULUM = join(ROOT, 'training/curriculum');
const { modules, roles } = JSON.parse(readFileSync(join(CURRICULUM, 'role-menus.json'), 'utf8'));

const boards = [];
for (const file of readdirSync(join(ROOT, 'training/storyboards')).filter((f) => f.endsWith('.mjs')).sort()) {
  try {
    boards.push((await import(pathToFileURL(join(ROOT, 'training/storyboards', file)).href)).default);
  } catch (error) {
    console.warn(`skipped ${file}: ${String(error.message ?? error).split('\n')[0]}`);
  }
}

// Each video's checks: copied from training/out (videos made here) into qa.json, beside those reported from elsewhere.
const qaFile = join(CURRICULUM, 'qa.json');
const qa = existsSync(qaFile) ? JSON.parse(readFileSync(qaFile, 'utf8')) : {};
if (process.argv.includes('--collect')) {
  for (const s of boards) {
    const f = join(ROOT, 'training/out', s.id, 'qa.json');
    if (existsSync(f)) {
      const r = JSON.parse(readFileSync(f, 'utf8'));
      qa[s.id] = {
        passed: r.passed,
        gates: r.gates,
        steps: r.run.steps,
        wer: r.speech.wer,
        wpm: r.pace.wordsPerMinute,
        maxCps: r.captions.maxCharsPerSecond,
        lufs: r.loudness.integratedLufs,
        seconds: r.format.seconds,
        megabytes: r.format.megabytes,
      };
    }
  }
  writeFileSync(qaFile, `${JSON.stringify(qa, null, 1)}\n`);
}

// Which videos a role watches: the shared ones meant for everyone, 02 for the roles whose menu has a patient list,
// and the videos of its own role.
const hasPatientLists = (r) => r.menu.some((m) => m.links.some((l) => l.to === '/patients' || l.to === '/front-office/visits'));
const shared = boards.filter((s) => s.role === '*');
const pathFor = (r) => [
  ...shared.filter((s) => s.id !== '02-find-patient' || hasPatientLists(r)),
  ...boards.filter((s) => s.role === r.role),
];

const covered = (link, videos) => videos.some((s) => (s.covers ?? []).some((c) => c === link.to || (c.endsWith('/*') && link.to.startsWith(c.slice(0, -1)))));
const fmt = (sec) => {
  if (!sec) return '';
  const s = Math.round(sec); // 359.6 s is 6:00, not 5:60
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
};

const out = [];
out.push('# Curriculum: who watches what');
out.push('');
out.push('Written by `training/pipeline/curriculum.mjs` from the code (each role\'s menu: `role-menus.json`) and the');
out.push('storyboards. Do not edit by hand: change a storyboard, then run the script again.');
out.push('');
out.push(`${boards.length} videos: ${shared.length} shared, ${boards.length - shared.length} for a role. Every member of staff watches the shared`);
out.push('videos first, then their role\'s, just before their department\'s go-live wave');
out.push('([MODULE-ROLLOUT-PLAN.md](../../docs/deployment/MODULE-ROLLOUT-PLAN.md), section 5).');
out.push('');

out.push('## Learning paths, by go-live wave');
out.push('');
const waveName = { 0: 'Before go-live (everyone)', 1: 'Wave 1: pharmacy and stores', 2: 'Wave 2: front office and OP billing', 3: 'Wave 3: doctors', 4: 'Wave 4: diagnostics', 5: 'Wave 5: in-patient', 6: 'Wave 6: back office', 7: 'Wave 7: patients and insights' };
const roleWave = (r) => Math.min(...boards.filter((s) => s.role === r.role).map((s) => s.wave ?? 9), 9);
out.push('| Wave | Role | Responsibility (from the code) | Videos, in order | Menu links taught |');
out.push('|---|---|---|---|---:|');
for (const r of [...roles].sort((a, b) => roleWave(a) - roleWave(b) || a.role.localeCompare(b.role))) {
  if (r.role === 'Patient') {
    const own = boards.filter((s) => s.role === 'Patient');
    out.push(`| ${waveName[roleWave(r)] ?? ''} | Patient (portal) | ${r.responsibility} | ${own.map((s) => `\`${s.id}\``).join(', ') || '—'} | portal |`);
    continue;
  }
  const videos = pathFor(r);
  const links = r.menu.flatMap((m) => m.links);
  const taught = links.filter((l) => covered(l, videos)).length;
  out.push(`| ${waveName[roleWave(r)] ?? 'Not yet made'} | ${r.role} | ${r.responsibility} | ${videos.map((s) => `\`${s.id}\``).join(', ')} | ${taught} of ${links.length} |`);
}
out.push('');

out.push('## The videos');
out.push('');
out.push('| Video | For | Teaches | Length | Checks |');
out.push('|---|---|---|---:|---|');
for (const s of boards) {
  const q = qa[s.id];
  const checks = q ? (q.passed ? `all 6 pass (WER ${q.wer}%, ${q.wpm} wpm)` : `fails: ${Object.entries(q.gates).filter(([, v]) => !v).map(([k]) => k).join(', ')}`) : 'not made yet';
  out.push(`| \`${s.id}\` ${s.title} | ${s.roleLabel} | ${s.objectives.join(' ')} | ${fmt(q?.seconds)} | ${checks} |`);
}
out.push('');

out.push('## Modules and the videos that teach them');
out.push('');
out.push('| Module | Area | Taught in | Links taught |');
out.push('|---|---|---|---:|');
for (const m of modules) {
  const teaching = boards.filter((s) => (s.modules ?? []).includes(m.id) || m.links.some((l) => covered(l, [s])));
  const taught = m.links.filter((l) => covered(l, boards)).length;
  out.push(`| ${m.title} | ${m.section} | ${teaching.map((s) => `\`${s.id}\``).join(', ') || '—'} | ${taught} of ${m.links.length} |`);
}
out.push('');

out.push('## What each role\'s videos leave for the role to find');
out.push('');
out.push('A role\'s menu holds more screens than a few short videos can teach. These are in the role\'s menu and in no video');
out.push('it watches: trainers show them on the training server, or the department decides they are not used yet.');
out.push('');
for (const r of roles.filter((x) => x.role !== 'Patient' && x.role !== 'SuperAdmin')) {
  const videos = pathFor(r);
  const left = r.menu.flatMap((m) => m.links.filter((l) => !covered(l, videos)).map((l) => `${m.title} › ${l.label}`));
  if (left.length) out.push(`- **${r.role}** (${left.length}): ${left.join('; ')}`);
}
out.push('');

writeFileSync(join(CURRICULUM, 'README.md'), `${out.join('\n')}\n`);
console.log(`${boards.length} storyboards, ${Object.keys(qa).length} with checks -> training/curriculum/README.md`);
