import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowUndoRegular, CheckmarkCircleRegular, LockClosedRegular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { InfoList } from '@/components/InfoList';
import { BackLink } from '@/components/links';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { useNow } from '@/lib/hooks';
import { isGuid } from '@/lib/ids';
import { useToast } from '@/lib/toast';
import type { PortalCorrectionItem, PortalDataRequestDetail } from '@/lib/types';
import { privacyApi, privacyQueries } from '@/modules/privacy/api';
import { ContactCard } from '@/modules/privacy/ContactCard';
import { DECISION, REQUEST_STATUS, REQUEST_TYPE } from '@/modules/privacy/labels';
import {
  awaitsRequesterCheck,
  correctionValue,
  erasureOutcome,
  eventView,
  fieldLabel,
  isOpenRequest,
  isReplyLate,
  requesterText,
} from '@/modules/privacy/model';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

/** One privacy request: where it stands, what was asked, our reply and its history. */
export const Route = createFileRoute('/_auth/my-privacy/requests/$requestId')({
  component: PrivacyRequestPage,
});

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (min-width: 900px)': { padding: '24px' },
  },
  pills: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
  text: {
    margin: 0,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '12px' },
  change: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    paddingBottom: '12px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none', paddingBottom: 0 },
  },
  changeHead: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  changeTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  value: { fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400, overflowWrap: 'anywhere' },
  meta: { color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' },
  timeline: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  event: {
    position: 'relative',
    display: 'flex',
    gap: '12px',
    paddingBottom: '16px',
    ':last-child': { paddingBottom: 0 },
    // The line joining one step to the next.
    '::before': {
      content: '""',
      position: 'absolute',
      left: '6px',
      top: '18px',
      bottom: '-2px',
      width: '2px',
      backgroundColor: tokens.colorNeutralStroke2,
    },
    ':last-child::before': { display: 'none' },
  },
  dot: {
    flexShrink: 0,
    width: '14px',
    height: '14px',
    marginTop: '4px',
    borderRadius: '50%',
    backgroundColor: tokens.colorBrandBackground,
  },
  eventText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  eventTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  when: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  withdraw: { alignSelf: 'flex-start', minWidth: '200px' },
  erased: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  erasedItem: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  erasedIcon: { flexShrink: 0, fontSize: '20px', marginTop: '2px', color: tokens.colorStatusSuccessForeground1 },
  kept: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  keptIcon: { flexShrink: 0, fontSize: '20px', marginTop: '2px', color: tokens.colorNeutralForeground3 },
});

function PrivacyRequestPage() {
  const { requestId } = Route.useParams();
  const t = useT();
  const { profile } = useActiveProfile();
  const request = useQuery({ ...privacyQueries.request(profile.id, requestId), enabled: isGuid(requestId) });
  const back = <BackLink to="/my-privacy/requests">{t('myPrivacy.hub.requests')}</BackLink>;

  if (!isGuid(requestId)) {
    return (
      <PortalPage title={t('myPrivacy.hub.requests')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (request.isPending) {
    return <PageSpinner />;
  }
  if (request.isError) {
    return (
      <PortalPage title={t('myPrivacy.hub.requests')} back={back} width="narrow">
        <ErrorBlock error={request.error} onRetry={() => void request.refetch()} />
      </PortalPage>
    );
  }
  return (
    <OwnerGuard ownerId={request.data.patientId}>
      <RequestDetail request={request.data} back={back} />
    </OwnerGuard>
  );
}

function RequestDetail({ request, back }: { request: PortalDataRequestDetail; back: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const now = useNow(60_000);
  const [withdrawOpen, setWithdrawOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const type = REQUEST_TYPE[request.type];
  const status = REQUEST_STATUS[request.status];
  const open = isOpenRequest(request.status);
  const late = isReplyLate(request, now);
  const details = request.details.trim();
  const response = request.response?.trim();
  const requesterName = request.requesterName?.trim();
  const erasure = erasureOutcome(request, t, f);
  const shown = { response: request.response, erasureShown: (erasure?.erased.length ?? 0) > 0 };

  const withdraw = async () => {
    setBusy(true);
    try {
      await privacyApi.withdraw(request.id);
      toast.success(t('myPrivacy.request.withdrawn'));
      setWithdrawOpen(false);
      await queryClient.invalidateQueries({ queryKey: privacyQueries.all(profile.id) });
    } catch (error) {
      toast.failure(error, t);
    } finally {
      setBusy(false);
    }
  };

  return (
    <PortalPage
      title={t(type.key)}
      subtitle={t('myPrivacy.requests.number', { number: request.requestNumber })}
      back={back}
      width="narrow"
    >
      <section className={styles.card} aria-label={t('common.details')}>
        <div className={styles.pills}>
          <StatusPill tone={status.tone}>{t(status.key)}</StatusPill>
        </div>
        <InfoList
          items={[
            { label: t('myPrivacy.request.received'), value: f.date(request.receivedAt) },
            { label: t('myPrivacy.request.replyBy'), value: open ? f.date(request.dueAt) : null, hideEmpty: true },
            {
              label: t('myPrivacy.request.closed'),
              value: request.closedAt ? f.date(request.closedAt) : null,
              hideEmpty: true,
            },
            { label: t('myPrivacy.request.by'), value: requesterText(request, t) },
          ]}
        />
      </section>

      {late && <Notice intent="warning">{t('myPrivacy.request.late')}</Notice>}

      {awaitsRequesterCheck(request) && (
        <Notice intent="info">
          {requesterName
            ? t('myPrivacy.request.checking', { name: requesterName, patient: profile.fullName })
            : t('myPrivacy.request.checkingNoName', { patient: profile.fullName })}
        </Notice>
      )}

      {response && (
        <ReportCard title={t('myPrivacy.request.reply')}>
          <p className={styles.text}>{response}</p>
        </ReportCard>
      )}

      {erasure && (
        <ReportCard title={t('myPrivacy.request.erased')}>
          {erasure.erased.length > 0 && (
            <ul className={styles.erased}>
              {erasure.erased.map((item) => (
                <li key={item} className={styles.erasedItem}>
                  <CheckmarkCircleRegular className={styles.erasedIcon} aria-hidden />
                  <span>{item}</span>
                </li>
              ))}
            </ul>
          )}
          {erasure.kept && (
            <p className={styles.kept}>
              <LockClosedRegular className={styles.keptIcon} aria-hidden />
              <span>{erasure.kept}</span>
            </p>
          )}
        </ReportCard>
      )}

      {(details || request.corrections.length > 0) && (
        <ReportCard title={t('myPrivacy.request.asked')}>
          {request.corrections.length > 0 && (
            <ul className={styles.list}>
              {request.corrections.map((item, index) => (
                <CorrectionRow key={`${item.field}-${index}`} item={item} />
              ))}
            </ul>
          )}
          {details && <p className={styles.text}>{details}</p>}
        </ReportCard>
      )}

      {request.timeline.length > 0 && (
        <ReportCard title={t('myPrivacy.request.history')}>
          <ol className={styles.timeline}>
            {request.timeline.map((event, index) => {
              const view = eventView(event, shown, t);
              return (
                <li key={`${event.at}-${index}`} className={styles.event}>
                  <span className={styles.dot} aria-hidden />
                  <span className={styles.eventText}>
                    <span className={styles.eventTitle}>{view.label}</span>
                    <time className={styles.when} dateTime={event.at}>
                      {f.dateTime(event.at)}
                    </time>
                    {view.text && <span className={styles.meta}>{view.text}</span>}
                  </span>
                </li>
              );
            })}
          </ol>
        </ReportCard>
      )}

      {request.canWithdraw && (
        <Button
          appearance="secondary"
          size="large"
          className={styles.withdraw}
          icon={<ArrowUndoRegular />}
          disabled={busy}
          onClick={() => setWithdrawOpen(true)}
        >
          {t('myPrivacy.request.withdraw')}
        </Button>
      )}

      <ContactCard contact={request.contact} />

      <ConfirmDialog
        open={withdrawOpen}
        title={t('myPrivacy.request.withdraw.title')}
        confirmLabel={t('myPrivacy.request.withdraw')}
        dismissLabel={t('myPrivacy.request.withdraw.keep')}
        destructive
        busy={busy}
        onConfirm={() => void withdraw()}
        onDismiss={() => setWithdrawOpen(false)}
      >
        <p style={{ margin: 0 }}>{t('myPrivacy.request.withdraw.body')}</p>
      </ConfirmDialog>
    </PortalPage>
  );
}

/** One requested change: what was on record, what it should be, and (once decided) the decision and why. */
function CorrectionRow({ item }: { item: PortalCorrectionItem }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const decided = item.decision !== 'Pending';
  const decision = DECISION[item.decision];
  return (
    <li className={styles.change}>
      <span className={styles.changeHead}>
        <span className={styles.changeTitle}>{fieldLabel(item.field, t)}</span>
        {decided && <StatusPill tone={decision.tone}>{t(decision.key)}</StatusPill>}
      </span>
      {item.currentValue?.trim() && (
        <span className={styles.meta}>
          {t('myPrivacy.request.was', { value: correctionValue(item.field, item.currentValue, t, f) })}
        </span>
      )}
      <span className={styles.value}>
        {t('myPrivacy.request.to', { value: correctionValue(item.field, item.requestedValue, t, f) })}
      </span>
      {decided && item.note?.trim() && (
        <span className={styles.meta}>{t('myPrivacy.request.note', { note: item.note.trim() })}</span>
      )}
    </li>
  );
}
