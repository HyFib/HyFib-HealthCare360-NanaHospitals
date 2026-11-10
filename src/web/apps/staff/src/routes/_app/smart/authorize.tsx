import { createFileRoute } from '@tanstack/react-router';
import { SmartAuthorizeScreen, type SmartAuthorizeSearch } from '@/modules/interop/smartAuthorize';

/** Where /oauth2/authorize sends a partner app's authorization request for a signed-in staff member to decide. */
export const Route = createFileRoute('/_app/smart/authorize')({
  validateSearch: (search: Record<string, unknown>): SmartAuthorizeSearch => {
    const text = (key: string) => (typeof search[key] === 'string' ? (search[key] as string) : undefined);
    return {
      client_id: text('client_id'),
      redirect_uri: text('redirect_uri'),
      scope: text('scope'),
      state: text('state'),
      code_challenge: text('code_challenge'),
      code_challenge_method: text('code_challenge_method'),
    };
  },
  component: SmartAuthorizePage,
});

function SmartAuthorizePage() {
  const search = Route.useSearch();
  return <SmartAuthorizeScreen search={search} onRedirect={(url) => window.location.assign(url)} />;
}
