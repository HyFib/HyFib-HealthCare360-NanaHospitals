/**
 * Personal link tokens (a feedback invitation, an unsubscribe link). The page reads the token from the address once,
 * keeps it in this tab only (memory and session storage), and puts a placeholder in the address bar, so the token does
 * not linger in history, screenshots, synced tabs or anything that reads the address later. A reload keeps working in
 * the same tab; the placeholder address opened anywhere else simply asks for the original link.
 */

export const TOKEN_PLACEHOLDER = '_';

export type LinkTokenScope = 'feedback' | 'unsubscribe';

const memory = new Map<LinkTokenScope, string>();
const storageKey = (scope: LinkTokenScope) => `hc360:portal:link:${scope}`;

/** The server's opaque tokens: 16–64 letters, digits, '-' or '_' (anything else is never sent). */
export function isWellFormedLinkToken(value: string | null | undefined): value is string {
  return typeof value === 'string' && /^[A-Za-z0-9_-]{16,64}$/.test(value);
}

/** Keeps a token from the address for this tab. Returns false when it is not a token at all. */
export function keepLinkToken(scope: LinkTokenScope, token: string): boolean {
  if (!isWellFormedLinkToken(token)) {
    return false;
  }
  memory.set(scope, token);
  try {
    sessionStorage.setItem(storageKey(scope), token);
  } catch {
    // Memory alone still serves this visit.
  }
  return true;
}

/** The token for the page: from the address when it carries one, otherwise the one kept in this tab. */
export function linkTokenFor(scope: LinkTokenScope, param: string): string | null {
  if (param !== TOKEN_PLACEHOLDER) {
    return isWellFormedLinkToken(param) ? param : null;
  }
  let kept = memory.get(scope) ?? null;
  if (!kept) {
    try {
      kept = sessionStorage.getItem(storageKey(scope));
    } catch {
      kept = null;
    }
  }
  return isWellFormedLinkToken(kept) ? kept : null;
}

/** Forgets the token once the link has done its job. */
export function forgetLinkToken(scope: LinkTokenScope): void {
  memory.delete(scope);
  try {
    sessionStorage.removeItem(storageKey(scope));
  } catch {
    // Nothing was stored.
  }
}
