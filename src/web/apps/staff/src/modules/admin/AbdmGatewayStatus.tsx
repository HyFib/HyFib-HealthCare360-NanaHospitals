import {
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ErrorState, LoadingState, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useFeatureOn } from '@/lib/features';
import { abdmGatewayDisplay, consentManagerLabel, integrationQueries } from './integrationsApi';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  value: { wordBreak: 'break-all' },
  list: { margin: 0, paddingInlineStart: '20px' },
});

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.fact}>
      <Caption1 className={styles.muted}>{label}</Caption1>
      <Text className={styles.value}>{children}</Text>
    </div>
  );
}

/**
 * The ABDM gateway (the National Health Authority's health information exchange) next to the other integrations: is it
 * switched on, is it set up, and what is missing before health records are sent. Shown only while ABDM health records
 * are switched on, as the server answers only then; the client secret itself is never shown.
 */
export function AbdmGatewayStatus() {
  const styles = useStyles();
  const switchedOn = useFeatureOn('abdm');
  const gateway = useQuery({ ...integrationQueries.abdmGateway(), enabled: switchedOn });

  if (!switchedOn) {
    return null;
  }

  const status = gateway.data;
  const display = status ? abdmGatewayDisplay(status) : null;
  const credentials = status
    ? status.hasClientId && status.hasClientSecret
      ? 'Client id and secret saved'
      : status.hasClientId
        ? 'Client secret missing'
        : status.hasClientSecret
          ? 'Client id missing'
          : 'Not saved'
    : '';

  return (
    <Section
      title="ABDM gateway"
      description="Sharing health records with the Ayushman Bharat Digital Mission. Downloading a record as an ABDM file does not need the gateway."
      actions={
        display &&
        status && <StatusBadge status={status.live ? 'live' : 'not-live'} tone={display.tone} label={display.label} />
      }
    >
      {gateway.isLoading ? (
        <LoadingState />
      ) : gateway.error ? (
        <ErrorState error={gateway.error} onRetry={() => void gateway.refetch()} />
      ) : status && display ? (
        <div className={styles.body}>
          <Caption1 className={styles.muted}>{display.hint}</Caption1>
          <div className={styles.facts}>
            <Fact label="Gateway address">{status.baseUrl || 'Not set'}</Fact>
            <Fact label="HIP id (HFR id)">{status.hipId || 'Not set'}</Fact>
            <Fact label="Consent manager">{consentManagerLabel(status.cmId)}</Fact>
            <Fact label="Client credentials">{credentials}</Fact>
          </div>
          {status.problems.length > 0 && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>To finish the set-up</MessageBarTitle>
                <ul className={styles.list}>
                  {status.problems.map((problem) => (
                    <li key={problem}>{problem}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}
          {!status.live && status.remaining.length > 0 && (
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Before records are sent to ABDM</MessageBarTitle>
                <ul className={styles.list}>
                  {status.remaining.map((step) => (
                    <li key={step}>{step}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
      ) : null}
    </Section>
  );
}
