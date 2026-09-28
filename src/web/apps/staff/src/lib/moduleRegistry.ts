import type { ModuleManifest } from './modules';

// Every feature module registers itself with a manifest next to its code; the shell discovers them at build time.
const discovered = import.meta.glob<{ default: ModuleManifest }>('../modules/*/manifest.{ts,tsx}', { eager: true });

export const moduleManifests: ModuleManifest[] = Object.values(discovered)
  .map((entry) => entry.default)
  .sort((a, b) => (a.order ?? 100) - (b.order ?? 100) || a.title.localeCompare(b.title));
