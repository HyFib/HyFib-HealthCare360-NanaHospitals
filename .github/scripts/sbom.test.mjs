// Tests of what sbom.mjs does to the SBOMs of the CycloneDX .NET tool and cdxgen. Run with: node --test .github/scripts/
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, it } from 'node:test';
import { appBom, combine, finish, runtimePacks, withRuntimePacks } from './sbom.mjs';

// The parts of obj/project.assets.json that matter, from a restore of HC360 Desktop for win-x64, self-contained.
const desktopAssets = {
  targets: {
    'net10.0-windows10.0.19041': { 'Serilog.Sinks.File/7.0.0': { type: 'package' } },
    'net10.0-windows10.0.19041/win-x64': { 'Serilog.Sinks.File/7.0.0': { type: 'package' } },
  },
  project: {
    restore: { projectName: 'HyFib.HealthCare360.Desktop', packagesPath: '/nuget/packages/' },
    frameworks: {
      'net10.0-windows10.0.19041': {
        downloadDependencies: [
          { name: 'Microsoft.AspNetCore.App.Runtime.win-x64', version: '[10.0.12, 10.0.12]' },
          { name: 'Microsoft.NETCore.App.Runtime.win-x64', version: '[10.0.12, 10.0.12]' },
          { name: 'Microsoft.NETCore.App.Host.win-x64', version: '[10.0.12, 10.0.12]' },
          { name: 'Microsoft.Windows.SDK.NET.Ref', version: '[10.0.19041.57, 10.0.19041.57]' },
          { name: 'Microsoft.WindowsDesktop.App.Runtime.win-x64', version: '[10.0.12, 10.0.12]' },
        ],
        frameworkReferences: { 'Microsoft.NETCore.App': { privateAssets: 'all' }, 'Microsoft.Windows.SDK.NET.Ref.Windows': { privateAssets: 'all' }, 'Microsoft.WindowsDesktop.App': { privateAssets: 'none' } },
      },
    },
  },
};

const dotnetBom = (name, packages) => ({
  bomFormat: 'CycloneDX',
  specVersion: '1.6',
  serialNumber: 'urn:uuid:00000000-0000-0000-0000-000000000001',
  version: 1,
  metadata: { timestamp: '2026-10-23T00:00:00Z', tools: { components: [{ name: 'CycloneDX module for .NET', version: '6.2.0.0' }] }, component: { type: 'application', 'bom-ref': `${name}@1.0.0`, name, version: '1.0.0' } },
  components: packages.map((p) => ({ type: 'library', 'bom-ref': `pkg:nuget/${p}`, name: p.split('@')[0], version: p.split('@')[1], purl: `pkg:nuget/${p}` })),
  dependencies: [{ ref: `${name}@1.0.0`, dependsOn: packages.map((p) => `pkg:nuget/${p}`) }, ...packages.map((p) => ({ ref: `pkg:nuget/${p}`, dependsOn: [] }))],
});

// cdxgen's SBOM of a pnpm workspace like src/web: the workspace's packages are in the graph, not among the components.
const workspace = {
  bomFormat: 'CycloneDX',
  specVersion: '1.6',
  metadata: { timestamp: '2026-10-23T00:00:00Z', tools: { components: [{ name: 'cdxgen', version: '12.8.5' }] }, component: { 'bom-ref': 'pkg:npm/hc360-web', name: 'hc360-web' } },
  components: [
    { 'bom-ref': 'pkg:npm/react@19.3.0', name: 'react', version: '19.3.0', purl: 'pkg:npm/react@19.3.0' },
    { 'bom-ref': 'pkg:npm/echarts@6.1.0', name: 'echarts', version: '6.1.0', purl: 'pkg:npm/echarts@6.1.0' },
    { 'bom-ref': 'pkg:npm/tslib@2.8.1', name: 'tslib', version: '2.8.1', purl: 'pkg:npm/tslib@2.8.1' },
    { 'bom-ref': 'pkg:npm/vite@8.3.4', name: 'vite', version: '8.3.4', scope: 'optional', purl: 'pkg:npm/vite@8.3.4' },
    { 'bom-ref': 'pkg:npm/esbuild@0.27.0', name: 'esbuild', version: '0.27.0', scope: 'optional', purl: 'pkg:npm/esbuild@0.27.0' },
    { 'bom-ref': 'pkg:npm/fsevents@2.3.3', name: 'fsevents', version: '2.3.3', scope: 'excluded', purl: 'pkg:npm/fsevents@2.3.3' },
    { 'bom-ref': 'pkg:npm/zustand@5.0.15', name: 'zustand', version: '5.0.15', purl: 'pkg:npm/zustand@5.0.15' },
  ],
  dependencies: [
    { ref: 'pkg:npm/hc360-web', dependsOn: ['pkg:npm/@hc360/staff@1.0.0', 'pkg:npm/@hc360/portal@1.0.0', 'pkg:npm/@hc360/core@1.0.0'] },
    { ref: 'pkg:npm/@hc360/staff@1.0.0', dependsOn: ['pkg:npm/@hc360/core@1.0.0', 'pkg:npm/echarts@6.1.0', 'pkg:npm/react@19.3.0', 'pkg:npm/vite@8.3.4'] },
    { ref: 'pkg:npm/@hc360/portal@1.0.0', dependsOn: ['pkg:npm/@hc360/core@1.0.0', 'pkg:npm/react@19.3.0', 'pkg:npm/vite@8.3.4'] },
    { ref: 'pkg:npm/@hc360/core@1.0.0', dependsOn: ['pkg:npm/zustand@5.0.15'] },
    { ref: 'pkg:npm/echarts@6.1.0', dependsOn: ['pkg:npm/tslib@2.8.1'] },
    { ref: 'pkg:npm/vite@8.3.4', dependsOn: ['pkg:npm/esbuild@0.27.0', 'pkg:npm/fsevents@2.3.3'] },
    { ref: 'pkg:npm/react@19.3.0', dependsOn: [] },
    { ref: 'pkg:npm/tslib@2.8.1', dependsOn: [] },
    { ref: 'pkg:npm/esbuild@0.27.0', dependsOn: [] },
    { ref: 'pkg:npm/fsevents@2.3.3', dependsOn: [] },
    { ref: 'pkg:npm/zustand@5.0.15', dependsOn: [] },
  ],
};

describe('the .NET runtime of a self-contained package', () => {
  it('is the runtime pack of each shared framework the project uses, as restored', () => {
    assert.deepEqual(runtimePacks(desktopAssets, 'win-x64'), [
      { name: 'Microsoft.NETCore.App.Runtime.win-x64', version: '10.0.12' },
      { name: 'Microsoft.Windows.SDK.NET.Ref', version: '10.0.19041.57' },
      { name: 'Microsoft.WindowsDesktop.App.Runtime.win-x64', version: '10.0.12' },
    ]);
  });

  it('includes the shared frameworks the packages reference', () => {
    const assets = structuredClone(desktopAssets);
    assets.targets['net10.0-windows10.0.19041/win-x64']['Serilog.Sinks.File/7.0.0'].frameworkReferences = ['Microsoft.AspNetCore.App'];
    assert.ok(runtimePacks(assets, 'win-x64').some((p) => p.name === 'Microsoft.AspNetCore.App.Runtime.win-x64'));
  });

  it('refuses a restore that was not for the runtime, self-contained', () => {
    const assets = structuredClone(desktopAssets);
    assets.project.frameworks['net10.0-windows10.0.19041'].downloadDependencies = [];
    assert.throws(() => runtimePacks(assets, 'win-x64'), /has no Microsoft\.NETCore\.App\.Runtime\.win-x64: restore it for win-x64, self-contained/);
  });

  it('adds them as components the application depends on, with the hash and licence NuGet has for them', () => {
    const packages = mkdtempSync(join(tmpdir(), 'sbom-test-'));
    try {
      const folder = join(packages, 'microsoft.netcore.app.runtime.win-x64', '10.0.12');
      mkdirSync(folder, { recursive: true });
      writeFileSync(join(folder, 'microsoft.netcore.app.runtime.win-x64.10.0.12.nupkg.sha512'), Buffer.from([0xab, 0xcd]).toString('base64'));
      writeFileSync(join(folder, 'microsoft.netcore.app.runtime.win-x64.nuspec'), '<package><metadata><license type="expression">MIT</license></metadata></package>');
      const bom = withRuntimePacks(dotnetBom('HC360 Desktop', ['Serilog.Sinks.File@7.0.0']), runtimePacks(desktopAssets, 'win-x64'), packages);
      const runtime = bom.components.find((c) => c.name === 'Microsoft.NETCore.App.Runtime.win-x64');
      assert.equal(runtime.purl, 'pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@10.0.12');
      assert.equal(runtime.type, 'framework');
      assert.deepEqual(runtime.hashes, [{ alg: 'SHA-512', content: 'ABCD' }]);
      assert.deepEqual(runtime.licenses, [{ expression: 'MIT' }]);
      assert.equal(bom.components.find((c) => c.name === 'Microsoft.Windows.SDK.NET.Ref').hashes, undefined);
      assert.deepEqual(bom.dependencies.find((d) => d.ref === 'HC360 Desktop@1.0.0').dependsOn, [
        'pkg:nuget/Serilog.Sinks.File@7.0.0',
        'pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@10.0.12',
        'pkg:nuget/Microsoft.Windows.SDK.NET.Ref@10.0.19041.57',
        'pkg:nuget/Microsoft.WindowsDesktop.App.Runtime.win-x64@10.0.12',
      ]);
      assert.equal(bom.components.length, 4);
    } finally {
      rmSync(packages, { recursive: true, force: true });
    }
  });
});

describe('an SBOM per web app', () => {
  it('lists what the app uses, less the development tools, with the workspace packages as first party', () => {
    const staff = appBom(workspace, '@hc360/staff', { name: 'HC360 staff app', version: '1.0.0' }, ['@hc360/core']);
    assert.deepEqual(staff.metadata.component, { type: 'application', 'bom-ref': 'pkg:npm/@hc360/staff@1.0.0', name: 'HC360 staff app', version: '1.0.0' });
    assert.deepEqual(staff.components.map((c) => c['bom-ref']).sort(), ['pkg:npm/@hc360/core@1.0.0', 'pkg:npm/echarts@6.1.0', 'pkg:npm/react@19.3.0', 'pkg:npm/tslib@2.8.1', 'pkg:npm/zustand@5.0.15']);
    assert.deepEqual(staff.components.find((c) => c.name === 'core'), { type: 'library', 'bom-ref': 'pkg:npm/@hc360/core@1.0.0', group: '@hc360', name: 'core', version: '1.0.0', description: "Part of HC360: a package of its web apps' pnpm workspace." });
    assert.deepEqual(staff.dependencies.find((d) => d.ref === 'pkg:npm/@hc360/staff@1.0.0').dependsOn, ['pkg:npm/@hc360/core@1.0.0', 'pkg:npm/echarts@6.1.0', 'pkg:npm/react@19.3.0']);
    const portal = appBom(workspace, '@hc360/portal', { name: 'HC360 Patient Portal', version: '1.0.0' });
    assert.deepEqual(portal.components.map((c) => c.name).sort(), ['core', 'react', 'zustand']);
    assert.notEqual(staff.serialNumber, portal.serialNumber);
  });

  it('refuses a graph that does not link the app to the workspace packages it depends on', () => {
    const unlinked = structuredClone(workspace);
    unlinked.dependencies[1].dependsOn = unlinked.dependencies[1].dependsOn.filter((ref) => !ref.includes('@hc360/'));
    assert.throws(() => appBom(unlinked, '@hc360/staff', { name: 'HC360 staff app', version: '1.0.0' }, ['@hc360/core']), /does not link @hc360\/staff to @hc360\/core/);
    assert.throws(() => appBom(workspace, '@hc360/kiosk', { name: 'x', version: '1' }), /not in the SBOM/);
  });
});

describe('an SBOM per package', () => {
  it('puts the applications that ship inside a package into its SBOM, once each', () => {
    const server = withRuntimePacks(dotnetBom('HC360 Server', ['Npgsql@10.0.3']), [{ name: 'Microsoft.NETCore.App.Runtime.win-x64', version: '10.0.12' }]);
    const staff = appBom(workspace, '@hc360/staff', { name: 'HC360 staff app', version: '1.0.0' });
    const portal = appBom(workspace, '@hc360/portal', { name: 'HC360 Patient Portal', version: '1.0.0' });
    const bom = finish(combine(server, [staff, portal]));
    const refs = bom.components.map((c) => c['bom-ref']);
    assert.equal(new Set(refs).size, refs.length);
    assert.deepEqual(refs, [...refs].sort());
    assert.deepEqual(bom.dependencies.find((d) => d.ref === 'HC360 Server@1.0.0').dependsOn, [
      'pkg:npm/@hc360/portal@1.0.0',
      'pkg:npm/@hc360/staff@1.0.0',
      'pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@10.0.12',
      'pkg:nuget/Npgsql@10.0.3',
    ]);
    // react: used by both apps, listed once; zustand and core: reached by both.
    assert.equal(refs.filter((r) => r.startsWith('pkg:npm/react@')).length, 1);
    assert.equal(bom.components.length, 2 + 2 + 5);
    const known = new Set([bom.metadata.component['bom-ref'], ...refs]);
    for (const { ref, dependsOn } of bom.dependencies) for (const r of [ref, ...dependsOn]) assert.ok(known.has(r), r);
    assert.deepEqual(bom.metadata.tools.components.map((t) => t.name), ['CycloneDX module for .NET', 'cdxgen']);
    assert.equal(bom.metadata.manufacturer.contact[0].email, 'security@hyfib.com');
    assert.deepEqual(bom.metadata.supplier, { name: 'HyFib' });
    assert.equal(bom.serialNumber, server.serialNumber);
  });
});
