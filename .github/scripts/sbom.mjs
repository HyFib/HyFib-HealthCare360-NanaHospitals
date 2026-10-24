// CycloneDX SBOMs of what HC360 ships (SRS SEC-11; docs/security/SECURITY.md, "SBOMs"): one for each package of a
// release, and one for each web app.
//
//   HC360-Server-<version>-win-x64.cdx.json     the server package: the server, and the staff app and portal it serves
//   HC360-Desktop-<version>-win-x64.cdx.json    HC360 Desktop, with its updater
//   HC360-LabAgent-<version>-win-x64.cdx.json   the lab agent
//   HC360-Staff-<version>.cdx.json              the staff app on its own (it ships inside the server package)
//   HC360-Portal-<version>.cdx.json             the patient portal on its own (likewise)
//
// .NET: each project is restored for win-x64, self-contained, as Publish-HC360.ps1 publishes it, and the CycloneDX .NET
// tool (.config/dotnet-tools.json) lists the NuGet packages of that restore. The tool leaves out the .NET runtime that a
// self-contained package carries, so the runtime packs the restore downloaded for the shared frameworks the project uses
// are added. Web: cdxgen (.github/sbom/package-lock.json) reads src/web/pnpm-lock.yaml, and each app gets the packages
// it reaches that cdxgen does not mark as for development only. cdxgen's validator then checks each file: the CycloneDX
// 1.6 schema, and its references and purls.
//
//   dotnet tool restore
//   npm ci --prefix .github/sbom --ignore-scripts --omit=optional --no-audit --no-fund
//   node .github/scripts/sbom.mjs [--version <version>] [--output <folder>]
//
// The version is Directory.Build.props's unless given, as in the names of the packages; the folder is artifacts.
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const runtime = 'win-x64';
const specVersion = '1.6';
const cdxgenBin = join(repo, '.github', 'sbom', 'node_modules', '@cyclonedx', 'cdxgen', 'bin');
// Who makes HC360, and where to report a vulnerability in it (docs/security/SECURITY.md, "Reporting a vulnerability").
const hyfib = { name: 'HyFib', contact: [{ name: 'HyFib security', email: 'security@hyfib.com' }] };

const projects = {
  server: 'src/server/HyFib.HealthCare360.Server/HyFib.HealthCare360.Server.csproj',
  desktop: 'src/desktop/HyFib.HealthCare360.Desktop/HyFib.HealthCare360.Desktop.csproj',
  updater: 'src/desktop/HyFib.HealthCare360.DesktopUpdater/HyFib.HealthCare360.DesktopUpdater.csproj',
  labAgent: 'src/agent/HyFib.HealthCare360.LabAgent/HyFib.HealthCare360.LabAgent.csproj',
};

const compare = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

// ---------------------------------------------------------------- .NET

/**
 * The runtime packs a self-contained publish for `rid` copies into the package: one for each shared framework that the
 * project, or a package it uses, references (Microsoft.NETCore.App, Microsoft.AspNetCore.App, ...), in the version the
 * restore downloaded. Read from the project's obj/project.assets.json.
 */
export function runtimePacks(assets, rid) {
  const packs = new Map();
  for (const [key, framework] of Object.entries(assets.project?.frameworks ?? {})) {
    const downloaded = new Map((framework.downloadDependencies ?? []).map((d) => [d.name.toLowerCase(), /^\[?\s*([^,\]\s]+)/.exec(d.version)?.[1]]));
    const references = new Set(Object.keys(framework.frameworkReferences ?? {}));
    for (const library of Object.values(assets.targets?.[`${key}/${rid}`] ?? {})) {
      for (const name of library.frameworkReferences ?? []) references.add(name);
    }
    for (const reference of references) {
      // <framework>.Runtime.<rid>, as the SDK names them; the Windows SDK projection has one pack for both uses.
      const name = reference.startsWith('Microsoft.Windows.SDK.NET.Ref') ? 'Microsoft.Windows.SDK.NET.Ref' : `${reference}.Runtime.${rid}`;
      const version = downloaded.get(name.toLowerCase());
      if (!version) throw new Error(`The restore of ${assets.project?.restore?.projectName ?? 'a project'} has no ${name}: restore it for ${rid}, self-contained.`);
      packs.set(name, version);
    }
  }
  return [...packs].map(([name, version]) => ({ name, version }));
}

function runtimePackComponent(name, version, packagesPath) {
  const id = name.toLowerCase();
  const folder = packagesPath ? join(packagesPath, id, version.toLowerCase()) : undefined;
  const read = (file) => (folder && existsSync(join(folder, file)) ? readFileSync(join(folder, file), 'utf8') : '');
  // As the CycloneDX tool does for the other packages: the SHA-512 NuGet recorded for the .nupkg, and its licence.
  const sha512 = read(`${id}.${version.toLowerCase()}.nupkg.sha512`).trim();
  const licence = /<license\s+type="expression"\s*>([^<]+)<\/license>/.exec(read(`${id}.nuspec`))?.[1]?.trim();
  const purl = `pkg:nuget/${name}@${version}`;
  return {
    type: 'framework',
    'bom-ref': purl,
    name,
    version,
    description: 'Part of the .NET runtime that the self-contained package carries (a runtime pack).',
    scope: 'required',
    ...(sha512 ? { hashes: [{ alg: 'SHA-512', content: Buffer.from(sha512, 'base64').toString('hex').toUpperCase() }] } : {}),
    ...(licence ? { licenses: [{ expression: licence }] } : {}),
    purl,
  };
}

/** Adds runtime packs to a BOM of the CycloneDX .NET tool, as components the application depends on. */
export function withRuntimePacks(bom, packs, packagesPath) {
  const root = bom.metadata.component['bom-ref'];
  let entry = bom.dependencies.find((d) => d.ref === root);
  if (!entry) bom.dependencies.push((entry = { ref: root, dependsOn: [] }));
  for (const { name, version } of packs) {
    const component = runtimePackComponent(name, version, packagesPath);
    if (!bom.components.some((c) => c['bom-ref'] === component['bom-ref'])) {
      bom.components.push(component);
      bom.dependencies.push({ ref: component['bom-ref'], dependsOn: [] });
    }
    entry.dependsOn = [...new Set([...(entry.dependsOn ?? []), component['bom-ref']])];
  }
  return bom;
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { cwd: repo, stdio: 'inherit', ...options });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${basename(command)} ${args.slice(0, 2).join(' ')} failed (exit code ${result.status}).`);
  return result;
}

function dotnetBom(project, name, version, work) {
  // The audit runs in CI and the release before this: the SBOM lists what there is, vulnerable or not.
  const windowsTargeting = process.platform === 'win32' ? [] : ['-p:EnableWindowsTargeting=true'];
  run('dotnet', ['restore', project, '--runtime', runtime, '-p:SelfContained=true', '-p:NuGetAudit=false', ...windowsTargeting]);
  const file = `${basename(project, '.csproj')}.cdx.json`;
  run('dotnet', [
    'CycloneDX', project, '--disable-package-restore', '--runtime', runtime, '--output', work, '--filename', file,
    '--output-format', 'Json', '--spec-version', specVersion, '--set-name', name, '--set-version', version, '--set-type', 'Application',
  ]);
  const assets = JSON.parse(readFileSync(join(repo, dirname(project), 'obj', 'project.assets.json'), 'utf8'));
  const bom = JSON.parse(readFileSync(join(work, file), 'utf8'));
  return withRuntimePacks(bom, runtimePacks(assets, runtime), assets.project?.restore?.packagesPath);
}

// ---------------------------------------------------------------- web

function firstParty(ref) {
  const at = ref.lastIndexOf('@');
  const fullName = ref.slice('pkg:npm/'.length, at);
  const slash = fullName.startsWith('@') ? fullName.indexOf('/') : -1;
  return {
    type: 'library',
    'bom-ref': ref,
    ...(slash > 0 ? { group: fullName.slice(0, slash), name: fullName.slice(slash + 1) } : { name: fullName }),
    version: ref.slice(at + 1),
    description: 'Part of HC360: a package of its web apps\' pnpm workspace.',
  };
}

/**
 * One app's SBOM, from cdxgen's SBOM of the whole pnpm workspace: the packages the app reaches in the dependency graph,
 * less those cdxgen marks as for development only (scope optional, or excluded). The workspace's own packages
 * (@hc360/ui, @hc360/core), which cdxgen has in the graph but not among its components, are listed as first party.
 * `links` are the workspace packages the app's package.json depends on: the graph must link the app to each of them.
 */
export function appBom(workspace, packageName, component, links = []) {
  const graph = new Map(workspace.dependencies.map((d) => [d.ref, d.dependsOn ?? []]));
  const components = new Map(workspace.components.map((c) => [c['bom-ref'], c]));
  const app = [...graph.keys()].find((ref) => ref.startsWith(`pkg:npm/${packageName}@`));
  if (!app) throw new Error(`${packageName} is not in the SBOM of the web apps' workspace.`);
  for (const link of links) {
    if (!graph.get(app).some((ref) => ref.startsWith(`pkg:npm/${link}@`))) {
      throw new Error(`The workspace's SBOM does not link ${packageName} to ${link}, which its package.json depends on: the SBOM would miss what ${link} uses.`);
    }
  }
  const reached = new Set();
  const stack = [...graph.get(app)];
  while (stack.length) {
    const ref = stack.pop();
    if (ref === app || reached.has(ref)) continue;
    reached.add(ref);
    stack.push(...(graph.get(ref) ?? []));
  }
  const kept = [...reached].filter((ref) => (components.has(ref) ? (components.get(ref).scope ?? 'required') === 'required' : graph.has(ref)));
  const keep = new Set(kept);
  return {
    bomFormat: 'CycloneDX',
    specVersion: workspace.specVersion,
    serialNumber: `urn:uuid:${randomUUID()}`,
    version: 1,
    metadata: { timestamp: workspace.metadata?.timestamp, tools: workspace.metadata?.tools, component: { type: 'application', 'bom-ref': app, ...component } },
    components: kept.map((ref) => components.get(ref) ?? firstParty(ref)),
    dependencies: [app, ...kept].map((ref) => ({ ref, dependsOn: (graph.get(ref) ?? []).filter((d) => keep.has(d)) })),
  };
}

const web = join(repo, 'src', 'web');

/** The workspace packages an app's package.json depends on (workspace:*). */
function workspaceLinks(app) {
  const { dependencies = {} } = JSON.parse(readFileSync(join(web, 'apps', app, 'package.json'), 'utf8'));
  return Object.entries(dependencies).filter(([, range]) => String(range).startsWith('workspace:')).map(([name]) => name);
}

function workspaceBom(work) {
  const file = join(work, 'web.cdx.json');
  // Only the lockfile: nothing is installed, and no usage analysis of the code. cdxgen finds the workspace's links
  // (workspace:*) from the folder it runs in, so it runs in src/web.
  const args = ['--type', 'js', '--no-install-deps', '--no-babel', '--spec-version', specVersion, '--output', file, '.'];
  const result = spawnSync(process.execPath, [join(cdxgenBin, 'cdxgen.js'), ...args], { cwd: web, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  if (result.error) throw result.error;
  if (result.status !== 0 || !existsSync(file)) throw new Error(`cdxgen failed (exit code ${result.status}):\n${result.stdout}\n${result.stderr}`);
  const bom = JSON.parse(readFileSync(file, 'utf8'));
  const sources = bom.metadata?.properties?.find((p) => p.name === 'cdx:bom:componentSrcFiles')?.value ?? '';
  if (!sources.split('\n').includes('pnpm-lock.yaml') || !bom.components?.length) {
    throw new Error(`cdxgen did not read src/web/pnpm-lock.yaml (it read: ${sources || 'nothing'}).`);
  }
  return bom;
}

// ---------------------------------------------------------------- one SBOM per package

/** The SBOM of `main`, with the applications that ship inside it (`parts`) and what they use. */
export function combine(main, parts) {
  const bom = structuredClone(main);
  const root = bom.metadata.component['bom-ref'];
  const components = new Map(bom.components.map((c) => [c['bom-ref'], c]));
  const graph = new Map(bom.dependencies.map((d) => [d.ref, new Set(d.dependsOn ?? [])]));
  if (!graph.has(root)) graph.set(root, new Set());
  const tools = [...(bom.metadata.tools?.components ?? [])];
  for (const part of parts) {
    const app = part.metadata.component;
    for (const component of [app, ...part.components]) if (!components.has(component['bom-ref'])) components.set(component['bom-ref'], component);
    for (const { ref, dependsOn = [] } of part.dependencies) {
      if (!graph.has(ref)) graph.set(ref, new Set());
      for (const dependency of dependsOn) graph.get(ref).add(dependency);
    }
    graph.get(root).add(app['bom-ref']);
    for (const tool of part.metadata.tools?.components ?? []) if (!tools.some((t) => t.name === tool.name && t.version === tool.version)) tools.push(tool);
  }
  bom.metadata.tools = { components: tools };
  bom.components = [...components.values()];
  bom.dependencies = [...graph].map(([ref, dependsOn]) => ({ ref, dependsOn: [...dependsOn] }));
  return bom;
}

/** HyFib as manufacturer and supplier, and a stable order, so two SBOMs of the same code differ only in their dates. */
export function finish(bom) {
  const { timestamp, tools, component } = bom.metadata;
  return {
    ...bom,
    metadata: { timestamp, lifecycles: [{ phase: 'build' }], tools, manufacturer: hyfib, supplier: { name: hyfib.name }, component },
    components: [...bom.components].sort((a, b) => compare(a['bom-ref'], b['bom-ref'])),
    dependencies: bom.dependencies
      .map(({ ref, dependsOn = [] }) => ({ ref, dependsOn: [...new Set(dependsOn)].sort(compare) }))
      .sort((a, b) => compare(a.ref, b.ref)),
  };
}

/** Checks a file with cdxgen's validator: the schema, and the references and purls. Its compliance scores are not checked. */
function validate(file, work) {
  const report = join(work, `${basename(file)}.validation.json`);
  const result = spawnSync(process.execPath, [join(cdxgenBin, 'validate.js'), '--input', file, '--report', 'json', '--report-file', report], { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  if (!existsSync(report)) throw new Error(`cdx-validate could not check ${basename(file)}:\n${result.stdout}\n${result.stderr}`);
  const { schemaValid, deepValid, findings = [] } = JSON.parse(readFileSync(report, 'utf8'));
  if (!schemaValid || !deepValid) {
    const errors = findings.filter((f) => f.status === 'fail' && /schema|deep|valid/i.test(`${f.category} ${f.ruleId}`)).map((f) => `  ${f.ruleId}: ${f.message}`);
    throw new Error(`${basename(file)} is not a valid CycloneDX ${specVersion} SBOM (schema ${schemaValid ? 'valid' : 'invalid'}, references and purls ${deepValid ? 'valid' : 'invalid'}):\n${errors.join('\n')}`);
  }
}

function count(bom) {
  const purls = bom.components.map((c) => c.purl ?? '');
  return {
    components: bom.components.length,
    nuget: purls.filter((p) => p.startsWith('pkg:nuget/')).length,
    npm: purls.filter((p) => p.startsWith('pkg:npm/')).length,
    runtime: bom.components.filter((c) => c.type === 'framework' && c.purl?.startsWith('pkg:nuget/')).map((c) => `${c.name} ${c.version}`),
  };
}

function main() {
  const { values } = parseArgs({ options: { version: { type: 'string' }, output: { type: 'string' } } });
  const version = values.version ?? /<Version>([^<]+)<\/Version>/.exec(readFileSync(join(repo, 'Directory.Build.props'), 'utf8'))?.[1];
  if (!version) throw new Error('No --version, and no <Version> in Directory.Build.props.');
  const output = resolve(values.output ?? join(repo, 'artifacts'));
  if (!existsSync(join(cdxgenBin, 'cdxgen.js'))) throw new Error('cdxgen is not installed: npm ci --prefix .github/sbom --ignore-scripts --omit=optional');
  mkdirSync(output, { recursive: true });
  const work = mkdtempSync(join(tmpdir(), 'hc360-sbom-'));
  try {
    const workspace = workspaceBom(work);
    const staff = appBom(workspace, '@hc360/staff', { name: 'HC360 staff app', version, description: 'The staff app, served by the HC360 server and shown in HC360 Desktop.' }, workspaceLinks('staff'));
    const portal = appBom(workspace, '@hc360/portal', { name: 'HC360 Patient Portal', version, description: 'The patient portal, served by the HC360 server at /portal.' }, workspaceLinks('portal'));
    const server = dotnetBom(projects.server, 'HC360 Server', version, work);
    const desktop = dotnetBom(projects.desktop, 'HC360 Desktop', version, work);
    const updater = dotnetBom(projects.updater, 'HC360 Desktop updater', version, work);
    const labAgent = dotnetBom(projects.labAgent, 'HC360 lab agent', version, work);
    const sboms = {
      [`HC360-Server-${version}-${runtime}.cdx.json`]: combine(server, [staff, portal]),
      [`HC360-Desktop-${version}-${runtime}.cdx.json`]: combine(desktop, [updater]),
      [`HC360-LabAgent-${version}-${runtime}.cdx.json`]: labAgent,
      [`HC360-Staff-${version}.cdx.json`]: staff,
      [`HC360-Portal-${version}.cdx.json`]: portal,
    };
    const rows = [];
    for (const [name, bom] of Object.entries(sboms)) {
      const file = join(output, name);
      const finished = finish(bom);
      writeFileSync(file, `${JSON.stringify(finished, null, 2)}\n`);
      validate(file, work);
      const counts = count(finished);
      console.log(`${name}: ${counts.components} components (${counts.nuget} NuGet, ${counts.npm} npm)${counts.runtime.length ? `, .NET runtime: ${counts.runtime.join(', ')}` : ''}; valid CycloneDX ${specVersion}.`);
      rows.push(`| \`${name}\` | ${counts.components} | ${counts.nuget} | ${counts.npm} | ${counts.runtime.join('<br>') || '–'} |`);
    }
    if (process.env.GITHUB_STEP_SUMMARY) {
      appendFileSync(process.env.GITHUB_STEP_SUMMARY, [
        `### SBOMs (CycloneDX ${specVersion})`,
        '',
        'Each is valid against the CycloneDX schema, with its references and purls checked by cdxgen\'s validator.',
        '',
        '| SBOM | Components | NuGet | npm | .NET runtime packs |',
        '|---|---|---|---|---|',
        ...rows,
        '',
      ].join('\n') + '\n');
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(process.env.GITHUB_ACTIONS === 'true' ? `::error title=SBOMs not made::${String(error.message).replaceAll('%', '%25').replaceAll('\n', '%0A')}` : error.message);
    process.exitCode = 1;
  }
}
