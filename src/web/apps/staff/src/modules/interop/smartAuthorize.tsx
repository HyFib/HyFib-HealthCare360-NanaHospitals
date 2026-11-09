import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { Page, PageHeader, Section } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';

/** GET /api/v1/smart/authorize/preview → SmartAuthorizationPreview. */
export interface SmartAuthorizationPreview {
  clientName: string;
  clientId: string;
  scopes: string[];
  needsPatient: boolean;
}

/** The app's authorization request, as /oauth2/authorize handed it to this screen. */
export interface SmartAuthorizeSearch {
  client_id?: string;
  redirect_uri?: string;
  scope?: string;
  state?: string;
  code_challenge?: string;
  code_challenge_method?: string;
}

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, maxWidth: '560px' },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS },
});

/** A scope in words: "patient/Observation.rs" → "Read and search Observation of the chosen patient". */
export function describeScope(scope: string): string {
  const match = /^(patient|user|system)\/([A-Za-z*]+)\.(read|r|s|rs)$/.exec(scope);
  if (!match) {
    return scope;
  }
  const [, context, type, permissions] = match;
  const what = type === '*' ? 'every kind of record' : type;
  const verb = permissions === 'r' ? 'Read' : permissions === 's' ? 'Search' : 'Read and search';
  const whose = context === 'patient' ? 'of the chosen patient' : 'that you may read';
  return `${verb} ${what} ${whose}`;
}

/**
 * A staff member decides on a partner app's request to read the FHIR API in their name: the app, what it asks for, and
 * for a patient/ request the patient's UHID. Allow sends the browser back to the app with a code; Deny with an error.
 * Each patient's record-sharing consent still applies to everything the app reads.
 */
export function SmartAuthorizeScreen({
  search,
  onRedirect,
}: {
  search: SmartAuthorizeSearch;
  onRedirect: (url: string) => void;
}) {
  const styles = useStyles();
  const [uhid, setUhid] = useState('');
  const preview = useQuery({
    queryKey: ['smart', 'authorize', search.client_id, search.redirect_uri, search.scope],
    queryFn: ({ signal }) =>
      api.get<SmartAuthorizationPreview>('/api/v1/smart/authorize/preview', {
        signal,
        query: { client_id: search.client_id, redirect_uri: search.redirect_uri, scope: search.scope },
      }),
    retry: false,
  });
  const decide = useMutation({
    mutationFn: (approve: boolean) =>
      api.post<{ redirect: string }>('/api/v1/smart/authorize', {
        clientId: search.client_id ?? '',
        redirectUri: search.redirect_uri ?? '',
        scope: search.scope ?? null,
        state: search.state ?? null,
        codeChallenge: search.code_challenge ?? '',
        codeChallengeMethod: search.code_challenge_method ?? '',
        patientUhid: uhid.trim() || null,
        approve,
      }),
    onSuccess: (result) => onRedirect(result.redirect),
  });

  return (
    <Page>
      <PageHeader title="Authorize an app" />
      <Section title={preview.data ? preview.data.clientName : 'App'}>
        {preview.isError ? (
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Unknown app</MessageBarTitle>
              This app is not registered here, or it asked to return to an address not registered for it.
            </MessageBarBody>
          </MessageBar>
        ) : preview.data ? (
          <form
            className={styles.form}
            onSubmit={(event) => {
              event.preventDefault();
              decide.mutate(true);
            }}
          >
            <Text>
              {preview.data.clientName} asks to read records through the FHIR API in your name. Each patient's
              record-sharing consent still decides what it gets.
            </Text>
            <ul aria-label="What the app asks for">
              {preview.data.scopes.map((scope) => (
                <li key={scope}>{describeScope(scope)}</li>
              ))}
            </ul>
            {preview.data.needsPatient && (
              <Field label="Patient's UHID" required hint="The app reads this patient's record only.">
                <Input value={uhid} onChange={(_, data) => setUhid(data.value)} maxLength={20} autoComplete="off" />
              </Field>
            )}
            {decide.isError && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <MessageBarTitle>Not authorized</MessageBarTitle>
                  The app could not be authorized. Check the UHID and try again.
                </MessageBarBody>
              </MessageBar>
            )}
            <div className={styles.actions}>
              <Button
                type="submit"
                appearance="primary"
                disabled={decide.isPending || (preview.data.needsPatient && !uhid.trim())}
              >
                Allow
              </Button>
              <Button disabled={decide.isPending} onClick={() => decide.mutate(false)}>
                Deny
              </Button>
            </div>
          </form>
        ) : (
          <Text>Loading…</Text>
        )}
      </Section>
    </Page>
  );
}
