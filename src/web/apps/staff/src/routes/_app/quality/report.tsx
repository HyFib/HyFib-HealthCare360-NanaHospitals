import { Button, Caption1, makeStyles, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, CopyRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { qualityKeys, type IncidentReceipt } from '@/modules/quality/api';
import { incidentTypeLabel, QUALITY_PERMISSIONS as Q, severityLabel } from '@/modules/quality/labels';
import { ReportIncidentForm } from '@/modules/quality/ReportIncidentForm';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/quality/report')({
  component: () => (
    <RequirePermission permission={Q.incidentsReport} what="incident reporting">
      <ReportPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  intro: { display: 'flex', flexDirection: 'column', gap: '4px' },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '10px',
    padding: '24px 16px',
    textAlign: 'center',
  },
  number: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '34px',
    lineHeight: '42px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function ReportPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [done, setDone] = useState<{ receipt: IncidentReceipt; anonymous: boolean } | null>(null);

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Report an incident"
        subtitle="Thank you for speaking up. Reports help us learn and make care safer; they are not used to blame anyone."
        icon={<ShieldErrorRegular />}
      />
      {done ? (
        <Section>
          <div className={styles.done} role="status">
            <CheckmarkCircleRegular fontSize={48} color={tokens.colorPaletteGreenForeground1} />
            <Text size={500} weight="semibold">
              Your report has been sent
            </Text>
            <Caption1 className={styles.muted}>Report number</Caption1>
            <span className={styles.number}>{done.receipt.incidentNumber}</span>
            <Text>
              {incidentTypeLabel[done.receipt.type]} · {severityLabel[done.receipt.severity]}
            </Text>
            {done.receipt.isSentinel && (
              <MessageBar intent="warning">
                <MessageBarBody>This is a sentinel event: the quality team has been alerted.</MessageBarBody>
              </MessageBar>
            )}
            <Text className={styles.muted}>
              {done.anonymous
                ? 'Your name was not stored. Note the report number if you want to ask the quality team about it later.'
                : 'You can follow it under "My reports". The quality team may contact you for more details.'}
            </Text>
            <div className={styles.actions}>
              <Button
                icon={<CopyRegular />}
                onClick={() =>
                  void navigator.clipboard?.writeText(done.receipt.incidentNumber).then(
                    () => notify.success('Report number copied'),
                    () => undefined,
                  )
                }
              >
                Copy number
              </Button>
              {!done.anonymous && (
                <Button onClick={() => void navigate({ to: '/quality/my-reports' })}>My reports</Button>
              )}
              <Button appearance="primary" onClick={() => setDone(null)}>
                Report another incident
              </Button>
            </div>
          </div>
        </Section>
      ) : (
        <ReportIncidentForm
          onReported={(receipt, anonymous) => {
            setDone({ receipt, anonymous });
            void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
            window.scrollTo({ top: 0 });
          }}
        />
      )}
    </Page>
  );
}
