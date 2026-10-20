import AxeBuilder from '@axe-core/playwright';
import type { Page, TestInfo } from '@playwright/test';
import type { NodeResult, Result } from 'axe-core';
import { NOT_CHECKED, WCAG_TAGS } from './axe';

/**
 * Accessibility checks in a real browser (SRS UX-10), for Playwright tests of the built apps: what jsdom cannot judge.
 * axe-core's colour contrast and 24 px target size, with every other WCAG 2.0, 2.1 and 2.2 A/AA rule, and a walk
 * through the screen with Tab that wants a visible focus indicator at every stop (docs/architecture/FRONTEND-GUIDE.md,
 * "Accessibility").
 *
 * The page talks to no server: `serveFakeApi` answers its API, health checks and realtime hub in the browser
 * (page.route), with the canned data the jsdom tests use.
 */

/** The themes the apps offer (UX-01). The staff app stores the choice; the portal follows the device. */
export type BrowserTheme = 'light' | 'dark' | 'high-contrast';

export const BROWSER_THEMES: readonly BrowserTheme[] = ['light', 'dark', 'high-contrast'];

/** A fetch the fake server answers with: `createFakeFetch` (./fakeApi) over the fixtures the jsdom tests use. */
export type FakeFetch = (input: string, init?: RequestInit) => Promise<Response>;

const RECORD_SEPARATOR = '\u001e';

/**
 * Answers every request the page makes to the HC360 Server (/api, /health, the SignalR hub) from `fakeFetch`, run in
 * the test, never on the network. The realtime hub accepts the connection and each subscription, and pings, as the
 * server does, so the frame shows a connected app rather than one trying to reconnect.
 */
export async function serveFakeApi(page: Page, fakeFetch: FakeFetch): Promise<void> {
  await page.route(
    (url) => url.pathname.startsWith('/api/') || url.pathname.startsWith('/health/'),
    async (route) => {
      const request = route.request();
      const body = request.postData();
      const response = await fakeFetch(request.url(), {
        method: request.method(),
        ...(body === null ? {} : { body }),
      });
      // The canned answers are JSON (or nothing), so the body goes as text.
      await route.fulfill({
        status: response.status,
        headers: Object.fromEntries(response.headers.entries()),
        body: await response.text(),
      });
    },
  );
  await page.route(
    (url) => url.pathname.startsWith('/hubs/') && url.pathname.endsWith('/negotiate'),
    (route) =>
      route.fulfill({
        json: {
          negotiateVersion: 1,
          connectionId: 'a11y-connection',
          connectionToken: 'a11y-connection-token',
          availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
        },
      }),
  );
  await page.routeWebSocket(
    (url) => url.pathname.startsWith('/hubs/'),
    (socket) => {
      let ping: ReturnType<typeof setInterval> | undefined;
      const send = (message: unknown) => socket.send(JSON.stringify(message) + RECORD_SEPARATOR);
      socket.onMessage((data) => {
        const text = typeof data === 'string' ? data : data.toString('utf8');
        for (const frame of text.split(RECORD_SEPARATOR).filter(Boolean)) {
          const message = JSON.parse(frame) as { protocol?: string; type?: number; invocationId?: string };
          if (message.protocol !== undefined) {
            send({});
            ping = setInterval(() => send({ type: 6 }), 10_000);
          } else if (message.type === 1 && message.invocationId !== undefined) {
            send({ type: 3, invocationId: message.invocationId, result: null });
          }
        }
      });
      socket.onClose(() => clearInterval(ping));
    },
  );
}

/**
 * Before the page loads: the theme (stored, as the staff app's account menu does, or the device's setting, which is
 * what the portal follows), and no transitions or animations, so axe measures the colours of the state on screen and
 * not one half-way through a change.
 */
export async function prepareTheme(page: Page, theme: BrowserTheme, how: 'stored' | 'device'): Promise<void> {
  if (how === 'stored') {
    await page.addInitScript((mode) => localStorage.setItem('hc360:theme', mode), theme);
    await page.emulateMedia({ reducedMotion: 'reduce' });
  } else {
    await page.emulateMedia({
      reducedMotion: 'reduce',
      colorScheme: theme === 'dark' ? 'dark' : 'light',
      forcedColors: theme === 'high-contrast' ? 'active' : 'none',
    });
  }
  await page.addInitScript(() => {
    const style = document.createElement('style');
    style.textContent = '*, *::before, *::after { transition: none !important; animation: none !important; }';
    document.addEventListener('DOMContentLoaded', () => document.head.append(style), { once: true });
  });
}

/**
 * What axe asks a person to review in a browser, and the reason it is right as it is, by the axe check that could not
 * decide. The jsdom check's own list (REVIEWED in ./axe) is for what jsdom reports; these are what only painting
 * shows. Anything else axe asks to review fails the check, as a violation does.
 */
export const REVIEWED_IN_BROWSER: Record<string, string> = {
  'focusable-modal-open':
    'The page behind an open modal dialog: Fluent hides it from screen readers and its focus trap keeps Tab inside the ' +
    'dialog, which axe cannot see (aria-hidden-focus).',
};

/** An axe result reduced to what a report needs, with the nodes that fail the check. */
export interface BrowserViolation {
  id: string;
  impact: string | null;
  help: string;
  helpUrl: string;
  nodes: { target: string; html: string; summary: string }[];
}

const isReviewed = (node: NodeResult) => {
  const checks = [...node.any, ...node.all, ...node.none];
  return checks.length > 0 && checks.every((check) => REVIEWED_IN_BROWSER[check.id] !== undefined);
};

const reduce = (result: Result, nodes: NodeResult[], review: boolean): BrowserViolation => ({
  id: result.id,
  impact: result.impact ?? null,
  help: review ? `${result.help} (axe asks for review)` : result.help,
  helpUrl: result.helpUrl,
  nodes: nodes.map((node) => ({
    target: node.target.join(' '),
    html: node.html.replace(/\sclass="[^"]*"/g, '').slice(0, 240),
    summary: node.failureSummary ?? '',
  })),
});

/** The lowest contrast a second look measured for some text, and the ratio it needs (4.5:1, or 3:1 when large). */
interface Measured {
  lowest: number;
  needed: number;
}

/**
 * Runs in the page: measures the contrast of `element`'s text where axe could not tell, or returns null when this
 * cannot tell either. 'gradient': against each colour stop of the gradient behind it (the sign-in pages' brand panel).
 * 'layers': against the colours painted under each of its lines, from the text down to the first opaque one, provided
 * nothing is painted over the text (text on a drawer's or dialog's opaque surface, whose lines lie over different parts
 * of the page beneath it, which is all axe compares). Self-contained, as Playwright sends its source to the page.
 */
function measureContrast(element: Element, how: 'gradient' | 'layers'): Measured | null {
  // Limits: a background image is not measured (null), and elementsFromPoint leaves out what has pointer-events: none,
  // so a see-through overlay of that kind over the text would go unseen.
  type Rgba = { r: number; g: number; b: number; a: number };
  const parse = (color: string): Rgba | null => {
    const parts = /rgba?\(([^)]+)\)/
      .exec(color)?.[1]
      ?.split(/[\s,/]+/)
      .filter(Boolean)
      .map(Number);
    return parts && parts.length >= 3 ? { r: parts[0]!, g: parts[1]!, b: parts[2]!, a: parts[3] ?? 1 } : null;
  };
  const over = (top: Rgba, under: Rgba): Rgba => ({
    r: top.a * top.r + (1 - top.a) * under.r,
    g: top.a * top.g + (1 - top.a) * under.g,
    b: top.a * top.b + (1 - top.a) * under.b,
    a: 1,
  });
  const luminance = ({ r, g, b }: Rgba) => {
    const channel = (value: number) => {
      const c = value / 255;
      return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
  };
  const ratio = (text: Rgba, background: Rgba) => {
    const [light, dark] = [luminance(over(text, background)), luminance(background)].sort((a, b) => b - a);
    return (light! + 0.05) / (dark! + 0.05);
  };

  const style = getComputedStyle(element);
  const text = parse(style.color);
  if (!text) {
    return null;
  }
  const size = parseFloat(style.fontSize);
  const needed = size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700) ? 3 : 4.5;
  const measured = (ratios: number[]) =>
    ratios.length === 0 ? null : { lowest: Math.round(Math.min(...ratios) * 100) / 100, needed };

  if (how === 'gradient') {
    let gradient: string | null = null;
    for (let node: Element | null = element; node && gradient === null; node = node.parentElement) {
      const nodeStyle = getComputedStyle(node);
      if (parseFloat(nodeStyle.opacity) < 1) {
        return null;
      }
      if (nodeStyle.backgroundImage.includes('gradient')) {
        gradient = nodeStyle.backgroundImage;
      } else if ((parse(nodeStyle.backgroundColor)?.a ?? 0) > 0) {
        // A colour between the text and the gradient: not what this measures.
        return null;
      }
    }
    const stops = (gradient?.match(/rgba?\([^)]+\)/g) ?? []).map(parse);
    if (stops.some((stop) => !stop || stop.a < 1)) {
      return null;
    }
    return measured(stops.map((stop) => ratio(text, stop!)));
  }

  const range = document.createRange();
  range.selectNodeContents(element);
  const ratios: number[] = [];
  for (const line of [...range.getClientRects()].filter((rect) => rect.width > 0 && rect.height > 0)) {
    const x = line.left + line.width / 2;
    const y = line.top + line.height / 2;
    if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) {
      return null;
    }
    const stack = document.elementsFromPoint(x, y);
    if (!stack[0] || !element.contains(stack[0])) {
      return null;
    }
    const layers: Rgba[] = [];
    for (const painted of stack) {
      const paintedStyle = getComputedStyle(painted);
      if (paintedStyle.backgroundImage !== 'none' || parseFloat(paintedStyle.opacity) < 1) {
        return null;
      }
      const background = parse(paintedStyle.backgroundColor);
      if (background && background.a > 0) {
        layers.push(background);
        if (background.a === 1) {
          break;
        }
      }
    }
    if (layers.length === 0 || layers[layers.length - 1]!.a < 1) {
      return null;
    }
    const background = layers.reduceRight((under, top) => over(top, under));
    ratios.push(ratio(text, background));
  }
  return measured(ratios);
}

const messageKey = (node: NodeResult) =>
  node.any.map((check) => (check.data as { messageKey?: string } | null)?.messageKey).find(Boolean);

/**
 * A second look at text whose contrast axe could not tell ("needs review"), where a browser can tell after all. Returns
 * 'passes', what fails (the node's report), or null when it still needs a person:
 * - on a gradient: measured against each of the gradient's colour stops;
 * - over different things beneath an opaque surface (a drawer's or dialog's text): measured against what is painted
 *   under each line;
 * - partly scrolled out of view or hidden under something (a tab in a list that scrolls sideways): scrolled into view,
 *   and checked by axe again.
 */
async function secondLook(page: Page, result: Result, node: NodeResult): Promise<'passes' | BrowserViolation | null> {
  const selector = node.target.length === 1 && typeof node.target[0] === 'string' ? node.target[0] : null;
  const key = messageKey(node);
  if (result.id !== 'color-contrast' || selector === null || key === undefined) {
    return null;
  }
  if (key === 'bgGradient' || key === 'elmPartiallyObscuring') {
    const how: 'gradient' | 'layers' = key === 'bgGradient' ? 'gradient' : 'layers';
    const measured = await page
      .locator(selector)
      .evaluate(measureContrast, how)
      .catch(() => null);
    if (measured === null) {
      return null;
    }
    if (measured.lowest >= measured.needed) {
      return 'passes';
    }
    const violation = reduce(result, [node], false);
    violation.impact = 'serious';
    const where = how === 'gradient' ? 'on the gradient' : 'on what is painted under it';
    violation.nodes[0]!.summary = `Measured in the browser: ${measured.lowest}:1 at its lowest (${where}), ${measured.needed}:1 needed.`;
    return violation;
  }
  if (key === 'elmPartiallyObscured' || key === 'outsideViewport') {
    await page
      .locator(selector)
      .evaluate((element) => element.scrollIntoView({ block: 'center', inline: 'center' }))
      .catch(() => undefined);
    const again = await new AxeBuilder({ page }).include(selector).withRules(['color-contrast']).analyze();
    const failed = again.violations.find((violation) => violation.nodes.length > 0);
    if (failed) {
      return reduce(failed, failed.nodes, false);
    }
    return again.incomplete.some((review) => review.nodes.length > 0) ? null : 'passes';
  }
  return null;
}

/**
 * Runs axe-core in the page with every WCAG 2.0, 2.1 and 2.2 A/AA rule, colour contrast and target size included, and
 * returns its violations and what it asks to review (less REVIEWED_IN_BROWSER, and less the text contrast a second look
 * can tell after all: secondLook). Tabster's focus sentinels are left out, as in jsdom. `include` limits the check to
 * part of the page (an open menu, whose page behind was checked on its own); `skip` leaves out rules that cannot
 * apply, each with the reason.
 */
export async function browserAxeViolations(
  page: Page,
  { include, skip = {} }: { include?: string; skip?: Record<string, string> } = {},
): Promise<BrowserViolation[]> {
  const builder = new AxeBuilder({ page }).withTags(WCAG_TAGS).exclude(NOT_CHECKED);
  if (include) {
    builder.include(include);
  }
  if (Object.keys(skip).length > 0) {
    builder.disableRules(Object.keys(skip));
  }
  const results = await builder.analyze();

  const measured: BrowserViolation[] = [];
  const toReview: BrowserViolation[] = [];
  for (const result of results.incomplete) {
    const nodes: NodeResult[] = [];
    for (const node of result.nodes.filter((candidate) => !isReviewed(candidate))) {
      const look = await secondLook(page, result, node);
      if (look === null) {
        nodes.push(node);
      } else if (look !== 'passes') {
        measured.push(look);
      }
    }
    if (nodes.length > 0) {
      toReview.push(reduce(result, nodes, true));
    }
  }
  return [...results.violations.map((result) => reduce(result, result.nodes, false)), ...measured, ...toReview];
}

/** One paragraph per rule: what it asks, where it failed and what would fix each place. */
export function describeBrowserViolations(violations: BrowserViolation[]): string {
  return violations
    .map((violation) => {
      const nodes = violation.nodes.map(
        (node) => `  - ${node.target}\n    ${node.html}\n      ${node.summary.replace(/\n/g, '\n      ')}`,
      );
      return `${violation.id} (${violation.impact ?? 'no impact given'}): ${violation.help}\n  ${violation.helpUrl}\n${nodes.join('\n')}`;
    })
    .join('\n\n');
}

/** A stop of the focus walk where focus cannot be seen, and why. */
export interface FocusProblem {
  element: string;
  problem: string;
}

/**
 * Where the focus walk starts: the top of the page; the main content, reached as keyboard users do (Tab to "Skip to
 * main content", Enter); or where focus is now, such as in an open dialog.
 */
export type FocusWalkStart = 'top' | 'skip-link' | 'here';

/** What the focus walk records about the focused element (in the page). */
interface FocusStop {
  label: string;
  visible: boolean;
  inWindow: boolean;
  seen: boolean;
}

/**
 * Runs in the page: records how everything that can take focus looks with nothing focused, and puts the function that
 * looks at the focused element on `window`. From the top, focus is put before everything on the page first (a screen
 * focuses its first field on opening); from 'here', it goes back where it was. Self-contained, as Playwright sends its
 * source to the page.
 */
function installFocusWalk(from: FocusWalkStart) {
  // How focus can show on an element: an outline, a box shadow or a border, on the element, its nearest ancestors (an
  // input's frame), or their ::before and ::after (Fluent draws its focus ring, and an input's underline, there).
  const look = (element: Element): string => {
    const border = (style: CSSStyleDeclaration, side: string) => {
      const width = style.getPropertyValue(`border-${side}-width`);
      const kind = style.getPropertyValue(`border-${side}-style`);
      const color = style.getPropertyValue(`border-${side}-color`);
      return parseFloat(width) > 0 && kind !== 'none' && kind !== 'hidden' ? `${width} ${kind} ${color}` : 'none';
    };
    const describe = (style: CSSStyleDeclaration) => {
      const outline =
        style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0
          ? `${style.outlineWidth} ${style.outlineStyle} ${style.outlineColor} ${style.outlineOffset}`
          : 'none';
      const borders = ['top', 'right', 'bottom', 'left'].map((side) => border(style, side)).join(',');
      return `${outline}|${style.boxShadow}|${borders}`;
    };
    const parts: string[] = [];
    let node: Element | null = element;
    for (let depth = 0; node && node !== document.body && depth < 4; depth++, node = node.parentElement) {
      parts.push(describe(getComputedStyle(node)));
      for (const pseudo of ['::before', '::after']) {
        const style = getComputedStyle(node, pseudo);
        const drawn = style.content !== 'none' && style.content !== 'normal' && style.display !== 'none';
        parts.push(drawn ? `${pseudo}${describe(style)}|${style.transform}|${style.opacity}` : `${pseudo}none`);
      }
    }
    return parts.join(';');
  };

  const here = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  here?.blur();
  if (from !== 'here') {
    const start = document.createElement('span');
    start.tabIndex = -1;
    document.body.prepend(start);
    start.focus();
  }
  const unfocused = new WeakMap<Element, string>();
  for (const element of document.querySelectorAll(
    'a[href], button, input, select, textarea, summary, iframe, [tabindex], [contenteditable="true"], [contenteditable=""]',
  )) {
    unfocused.set(element, look(element));
  }
  const visited = new Set<Element>();
  if (from === 'here') {
    here?.focus();
  }

  const stop = (): FocusStop | null => {
    const element = document.activeElement;
    if (!element || element === document.body || visited.has(element)) {
      return null;
    }
    visited.add(element);
    const role = element.getAttribute('role');
    const name = (
      element.getAttribute('aria-label') ??
      element.getAttribute('placeholder') ??
      element.textContent ??
      element.getAttribute('title') ??
      ''
    )
      .trim()
      .replace(/\s+/g, ' ')
      .slice(0, 60);
    const label = `<${element.tagName.toLowerCase()}${role ? ` role="${role}"` : ''}> "${name}"`;

    const focused = look(element);
    let before = unfocused.get(element);
    if (before === undefined) {
      // Rendered after the walk began: compare with the element blurred for a moment.
      (element as HTMLElement).blur();
      before = look(element);
      (element as HTMLElement).focus({ preventScroll: true });
    }

    const box = element.getBoundingClientRect();
    const inWindow =
      box.width > 0 &&
      box.height > 0 &&
      box.bottom > 0 &&
      box.right > 0 &&
      box.top < innerHeight &&
      box.left < innerWidth;
    // Not entirely covered (WCAG 2.4.11): some point of it is the element, inside it, or its label, when looked at.
    const labelFor = element.id ? document.querySelector(`label[for="${CSS.escape(element.id)}"]`) : null;
    const points = [
      [box.left + box.width / 2, box.top + box.height / 2],
      [box.left + 2, box.top + 2],
      [box.right - 2, box.top + 2],
      [box.left + 2, box.bottom - 2],
      [box.right - 2, box.bottom - 2],
    ] as const;
    const seen = points.some(([x, y]) => {
      const hit = document.elementFromPoint(x, y);
      return (
        hit !== null &&
        (hit === element || element.contains(hit) || hit.contains(element) || (labelFor?.contains(hit) ?? false))
      );
    });
    return { label, visible: focused !== before, inWindow, seen };
  };

  Object.assign(window, { __hc360FocusStop: stop });
}

/**
 * Presses Tab through the page (up to `maxStops` stops, or until focus comes back to an element it has been on) and
 * returns each stop where focus cannot be seen: nothing about the element, its nearest ancestors or their ::before and
 * ::after (outline, box shadow, border; the underline of Fluent's inputs) differs from the element unfocused (WCAG
 * 2.4.7); or the element is outside the window, or entirely covered by something else, such as a sticky header
 * (WCAG 2.4.11). From 'skip-link', the first stop must be the page's "Skip to main content" link, and the walk goes
 * on from the main content after pressing Enter on it.
 */
export async function focusProblems(
  page: Page,
  { from = 'top', maxStops = 40 }: { from?: FocusWalkStart; maxStops?: number } = {},
): Promise<{ stops: number; problems: FocusProblem[] }> {
  await page.evaluate(installFocusWalk, from);
  const next = async () => {
    await page.keyboard.press('Tab');
    return page.evaluate(() => (window as unknown as { __hc360FocusStop: () => FocusStop | null }).__hc360FocusStop());
  };

  const problems: FocusProblem[] = [];
  const record = (stop: FocusStop) => {
    if (!stop.visible) {
      problems.push({ element: stop.label, problem: 'no visible focus indicator' });
    } else if (!stop.inWindow) {
      problems.push({ element: stop.label, problem: 'focused outside the window' });
    } else if (!stop.seen) {
      problems.push({ element: stop.label, problem: 'focused but covered by something else' });
    }
  };

  let stops = 0;
  if (from === 'skip-link') {
    const skip = await next();
    if (!skip || !/skip/i.test(skip.label)) {
      throw new Error(`The first Tab stop is not a "Skip to main content" link: ${skip?.label ?? 'nothing'}.`);
    }
    record(skip);
    stops++;
    await page.keyboard.press('Enter');
  }
  for (; stops < maxStops; stops++) {
    const stop = await next();
    if (stop === null) {
      break;
    }
    record(stop);
  }
  return { stops, problems };
}

export interface ScreenCheckOptions {
  /**
   * A selector for the part of the page axe checks: an open menu or popover, which covers part of the page behind it
   * (checked on its own), so that axe cannot tell the colours under it.
   */
  include?: string;
  /** Rules that cannot apply to what is checked, each with the reason. Never to let a real problem through. */
  skip?: Record<string, string>;
  /** The focus walk (focusProblems), or false for a state Tab would end, such as an open menu. */
  focus?: false | { from?: FocusWalkStart; maxStops?: number };
}

/**
 * Checks the screen as it is now: axe-core with every WCAG 2.0, 2.1 and 2.2 A/AA rule (colour contrast and target size
 * included), then a walk with Tab that wants focus visible at every stop. Fails with one report of everything found,
 * which is also attached to the test.
 */
export async function expectAccessibleScreen(
  page: Page,
  testInfo: TestInfo,
  { include, skip = {}, focus = {} }: ScreenCheckOptions = {},
): Promise<void> {
  const reports: string[] = [];
  const violations = await browserAxeViolations(page, { ...(include ? { include } : {}), skip });
  if (violations.length > 0) {
    const count = violations.reduce((sum, violation) => sum + violation.nodes.length, 0);
    reports.push(
      `axe-core found ${count} accessibility problem${count === 1 ? '' : 's'} (WCAG 2.2 A/AA):\n\n` +
        describeBrowserViolations(violations),
    );
  }
  if (focus !== false) {
    const { stops, problems } = await focusProblems(page, focus);
    if (stops === 0) {
      reports.push('Tab reached nothing on the page.');
    } else if (problems.length > 0) {
      reports.push(
        `${problems.length} of ${stops} focus stops cannot be seen (WCAG 2.4.7, 2.4.11):\n` +
          problems.map((problem) => `  - ${problem.element}: ${problem.problem}`).join('\n'),
      );
    }
  }
  if (reports.length > 0) {
    const skipped = Object.entries(skip).map(([id, reason]) => `(${id} not checked: ${reason})`);
    const report = [...reports, ...skipped].join('\n\n');
    await testInfo.attach('accessibility.txt', { body: report, contentType: 'text/plain' });
    throw new Error(report);
  }
}
