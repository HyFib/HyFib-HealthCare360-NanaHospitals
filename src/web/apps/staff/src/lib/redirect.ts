/**
 * Post-login destination. Only same-app relative paths are allowed, so a crafted `?redirect=` link can never send a
 * signed-in user to another site (open redirect) or back to the login page.
 */
export function safeRedirect(target: string | undefined): string {
  if (!target || !target.startsWith('/') || target.startsWith('//') || target.startsWith('/\\')) {
    return '/';
  }
  return target === '/login' || target.startsWith('/login?') || target.startsWith('/login/') ? '/' : target;
}
