import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  AddRegular,
  CheckmarkCircleRegular,
  CopyRegular,
  PersonNoteRegular,
  PersonRegular,
} from '@fluentui/react-icons';
import { formatDate } from '@hc360/core';
import { Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { createFileRoute, Navigate, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/hr/shared/NoAccess';
import type { CreatedDataRequest } from '@/modules/privacy/api';
import { PRIVACY_PERMISSIONS as P } from '@/modules/privacy/labels';
import { RequestIntakeDialog } from '@/modules/privacy/RequestIntakeDialog';
import { isRecordId } from '@/modules/privacy/rules';

/**
 * Link target for "Log a privacy request" (quick action and navigation). Officers who can read the queue land on it
 * with the intake open; front-desk staff, who may only log requests, get the intake on its own page.
 */
export const Route = createFileRoute('/_app/privacy/requests/new')({
  validateSearch: (s: Record<string, unknown>): { patientId?: string } => ({
    patientId: isRecordId(s.patientId) ? s.patientId : undefined,
  }),
  component: NewRequestRoute,
});

function NewRequestRoute() {
  const { patientId } = Route.useSearch();
  const canRead = usePermission(P.requestsRead);
  const canLog = usePermission(P.requestsLog);
  if (canRead) {
    return <Navigate to="/privacy/requests" search={{ new: canLog ? true : undefined, patientId }} replace />;
  }
  return canLog ? <DeskIntakePage /> : <NoAccess what="privacy requests" />;
}

const useStyles = makeStyles({
  steps: { margin: 0, paddingLeft: '20px', display: 'flex', flexDirection: 'column', gap: '6px' },
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
    fontSize: '32px',
    lineHeight: '40px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  start: { marginTop: '12px' },
});

function DeskIntakePage() {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const { patientId } = Route.useSearch();
  const [open, setOpen] = useState(true);
  const [logged, setLogged] = useState<CreatedDataRequest | null>(null);

  const again = () => {
    setLogged(null);
    setOpen(true);
  };

  return (
    <Page maxWidth={820}>
      <PageHeader
        title="Log a privacy request"
        subtitle="When a patient, or someone acting for them, asks about their personal data at the desk, or by e-mail, letter or phone."
        icon={<PersonNoteRegular />}
      />
      {logged ? (
        <Section>
          <div className={styles.done} role="status">
            <CheckmarkCircleRegular fontSize={48} color={tokens.colorPaletteGreenForeground1} aria-hidden />
            <Text size={500} weight="semibold">
              Request logged
            </Text>
            <Caption1 className={styles.muted}>Request number</Caption1>
            <span className={styles.number}>{logged.requestNumber}</span>
            <Text weight="semibold">Reply by {formatDate(logged.dueAt)}</Text>
            <Text className={styles.muted}>
              Give the person this number. They also get it by SMS or e-mail, with the reply-by date, when we have their
              mobile number or e-mail address. The grievance officer handles the request from here.
            </Text>
            <div className={styles.actions}>
              <Button
                icon={<CopyRegular />}
                onClick={() =>
                  void navigator.clipboard?.writeText(logged.requestNumber).then(
                    () => notify.success('Request number copied'),
                    () => undefined,
                  )
                }
              >
                Copy number
              </Button>
              {patientId && (
                <Button
                  icon={<PersonRegular />}
                  onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId } })}
                >
                  Back to the patient record
                </Button>
              )}
              <Button appearance="primary" icon={<AddRegular />} onClick={again}>
                Log another request
              </Button>
            </div>
          </div>
        </Section>
      ) : (
        <Section title="Before you start">
          <ul className={styles.steps}>
            <li>
              <Text>
                Use this when someone asks to see the personal data the hospital holds about a patient, to correct or
                erase it, or complains about how it was handled.
              </Text>
            </li>
            <li>
              <Text>
                If you can, check who is asking (for example their photo ID) and that they may act for the patient. You
                can record it in the form.
              </Text>
            </li>
            <li>
              <Text>
                The grievance officer is alerted and replies by the published date. For a copy of medical records, use a
                medical records request instead.
              </Text>
            </li>
          </ul>
          {!open && (
            <Button appearance="primary" icon={<AddRegular />} className={styles.start} onClick={again}>
              Log a request
            </Button>
          )}
        </Section>
      )}

      <RequestIntakeDialog
        open={open}
        patientId={patientId}
        onClose={() => setOpen(false)}
        onCreated={(created) => {
          setLogged(created);
          setOpen(false);
        }}
      />
    </Page>
  );
}
