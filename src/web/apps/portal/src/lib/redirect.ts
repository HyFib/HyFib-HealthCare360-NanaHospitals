/**
 * Where to go after signing in. Only same-app relative paths are allowed, so a crafted `?redirect=` link can never
 * send a patient to another site (open redirect) or loop back to the sign-in page. Paths are router paths (without
 * the /portal base); a full "/portal/..." path is accepted and normalised.
 */
export const HOME_PATH = '/home';
export const SIGN_IN_PATH = '/sign-in';

export function safeRedirect(target: string | null | undefined): string {
  if (!target || typeof target !== 'string') {
    return HOME_PATH;
  }
  let path = target.trim();
  if (!path.startsWith('/') || path.startsWith('//') || path.startsWith('/\\') || hasControlCharacters(path)) {
    return HOME_PATH;
  }
  if (path === '/portal' || path.startsWith('/portal/') || path.startsWith('/portal?')) {
    path = path.slice('/portal'.length) || '/';
  }
  const pathname = path.split(/[?#]/)[0] ?? '';
  if (pathname === '/' || pathname === '' || pathname === SIGN_IN_PATH || pathname.startsWith(`${SIGN_IN_PATH}/`)) {
    return HOME_PATH;
  }
  return path;
}

function hasControlCharacters(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 32 || code === 127) {
      return true;
    }
  }
  return false;
}
