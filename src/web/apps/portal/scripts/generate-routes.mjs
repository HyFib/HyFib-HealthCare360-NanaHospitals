// Regenerates src/routeTree.gen.ts from the files in src/routes without starting Vite, so typecheck works right
// after adding a route file (the Vite plugin does the same during `pnpm dev` / `pnpm build`).
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
// The generator ships with the router plugin; resolve it from there (pnpm keeps it out of our node_modules).
const pluginEntry = require.resolve('@tanstack/router-plugin/vite');
const generatorEntry = createRequire(pluginEntry).resolve('@tanstack/router-generator');
const { Generator, getConfig } = await import(pathToFileURL(generatorEntry).href);

const config = getConfig({ target: 'react', autoCodeSplitting: true }, root);
await new Generator({ config, root }).run();
console.log('routeTree.gen.ts is up to date');
