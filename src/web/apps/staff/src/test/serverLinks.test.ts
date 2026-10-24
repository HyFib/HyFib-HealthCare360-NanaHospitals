import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

// The server links its alerts to staff screens ("/assets/tickets?id=…", "/feedback/complaints/{id}"): a click on the
// alert opens the link in the app. A link to a screen the app does not have opens "page not found", so every app path
// written in the server's C# must be a route of the staff app.

const ROOT = join(import.meta.dirname, '..', '..', '..', '..', '..', '..');
const SERVER = join(ROOT, 'src', 'server');

/** The staff app's route paths ("/assets/tickets", "/lab/orders/$orderId"), from the generated route tree. */
function staffRoutes(): string[] {
  const tree = readFileSync(join(import.meta.dirname, '..', 'routeTree.gen.ts'), 'utf8');
  const byPath = /interface FileRoutesByFullPath \{([\s\S]*?)\}/.exec(tree)?.[1] ?? '';
  return [...byPath.matchAll(/'([^']+)':/g)].map((m) => m[1]!.replace(/\/$/, '') || '/');
}

function csharpFiles(folder: string): string[] {
  return readdirSync(folder, { withFileTypes: true, recursive: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith('.cs'))
    .map((entry) => join(entry.parentPath, entry.name))
    .filter((file) => !/[\\/](bin|obj)[\\/]/.test(file));
}

const routes = staffRoutes();
const firstSegments = new Set(routes.map((route) => route.split('/')[1]));

/** "/assets/tickets/{ticket.Id}" matches "/assets/tickets/$ticketId"; a query string or fragment is left out. */
function isRoute(path: string): boolean {
  const wanted = path.split(/[?#]/)[0]!.replace(/\/$/, '').split('/');
  return routes.some((route) => {
    const segments = route.split('/');
    return (
      segments.length === wanted.length &&
      segments.every((s, i) => s === wanted[i] || (s.startsWith('$') && /^\{[^}]+\}$/.test(wanted[i]!)))
    );
  });
}

/** App paths in the server's strings: under a top-level staff screen, at least two segments deep. */
function serverLinks(): { path: string; where: string }[] {
  const links: { path: string; where: string }[] = [];
  for (const file of csharpFiles(SERVER)) {
    readFileSync(file, 'utf8')
      .split('\n')
      .forEach((line, index) => {
        // Endpoint route templates ("/patients/{id:guid}") are API paths, not links.
        if (/\.Map(Get|Post|Put|Patch|Delete|Group|Methods)\(/.test(line)) {
          return;
        }
        for (const match of line.matchAll(/\$?"(\/[a-z][^" ]*)"/g)) {
          const path = match[1]!;
          const segments = path.split(/[?#]/)[0]!.split('/');
          if (segments.length >= 3 && firstSegments.has(segments[1])) {
            links.push({ path, where: `${relative(ROOT, file)}:${index + 1}` });
          }
        }
      });
  }
  return links;
}

describe('links in server alerts open a staff screen', () => {
  const links = serverLinks();

  it('finds the links', () => {
    expect(links.map((l) => l.path)).toContain('/assets/tickets?id={ticket.Id}');
    expect(links.length).toBeGreaterThan(5);
  });

  it('has a route for every link', () => {
    const broken = links.filter((l) => !isRoute(l.path)).map((l) => `${l.path} (${l.where})`);
    expect(broken, `Server links to staff screens that do not exist:\n${broken.join('\n')}`).toEqual([]);
  });
});
