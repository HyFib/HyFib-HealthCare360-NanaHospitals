// What each built-in role finds in its menu on a new installation, read from the code: the staff app's module manifests
// (each link with the permission it needs) evaluated by Vite exactly as the app is built, and the roles' default
// permissions from docs/security/roles.json (written from the server's code by RolesInventoryTests). The curriculum
// is built on this file: every link a role sees must be taught in one of that role's videos, or say why not.
//
//   node training/pipeline/role-menus.mjs [out.json]
//
// A link is shown when its permission is held (one of a list is enough) and its feature switch is on; this file counts
// every switch on, as a new installation has them. Rule: src/web/apps/staff/src/lib/modules.ts, hasPermission.

import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const STAFF = join(ROOT, 'src/web/apps/staff');
const OUT = process.argv[2] ?? join(ROOT, 'training/curriculum/role-menus.json');

const { createServer } = await import(createRequire(join(STAFF, 'package.json')).resolve('vite'));
const vite = await createServer({
  root: STAFF,
  configFile: join(STAFF, 'vite.config.ts'),
  server: { middlewareMode: true, hmr: false, watch: null },
  appType: 'custom',
  logLevel: 'error',
});

let manifests;
try {
  ({ moduleManifests: manifests } = await vite.ssrLoadModule('/src/lib/moduleRegistry.ts'));
} finally {
  await vite.close();
}

const { roles, permissions } = JSON.parse(readFileSync(join(ROOT, 'docs/security/roles.json'), 'utf8'));
const describe = new Map(permissions.map((p) => [p.name, p.description]));

const asList = (p) => (!p ? [] : typeof p === 'string' ? [p] : [...p]);
const holds = (granted, needed) => needed.length === 0 || granted.has('*') || needed.some((p) => granted.has(p));

const modules = manifests.map((m) => ({
  id: m.id,
  title: m.title,
  section: m.section,
  links: m.links.map((l) => ({
    label: l.label,
    to: l.to,
    permission: asList(l.permission),
    ...(l.feature ? { feature: l.feature } : {}),
  })),
}));

const roleMenus = roles.map((role) => {
  const granted = new Set(role.defaultPermissions);
  const menu = modules
    .map((m) => ({ module: m.id, title: m.title, section: m.section, links: m.links.filter((l) => holds(granted, l.permission)) }))
    .filter((m) => m.links.length > 0);
  return {
    role: role.name,
    responsibility: role.description,
    mfa: role.mfa,
    permissions: role.defaultPermissions.map((p) => ({ name: p, description: describe.get(p) ?? null })),
    menu,
  };
});

mkdirSync(dirname(OUT), { recursive: true });
writeFileSync(OUT, `${JSON.stringify({ generatedFrom: ['src/web/apps/staff/src/modules/*/manifest.ts', 'docs/security/roles.json'], modules, roles: roleMenus }, null, 1)}\n`);

for (const r of roleMenus) {
  const links = r.menu.reduce((n, m) => n + m.links.length, 0);
  console.log(`${r.role.padEnd(22)} ${String(r.menu.length).padStart(2)} modules ${String(links).padStart(3)} links`);
}
console.log(`${modules.length} modules, ${modules.reduce((n, m) => n + m.links.length, 0)} links -> ${OUT}`);
