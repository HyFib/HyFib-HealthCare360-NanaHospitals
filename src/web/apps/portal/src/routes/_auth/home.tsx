import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  CalendarAddRegular,
  DocumentTextRegular,
  HeartPulseRegular,
  PillRegular,
  ReceiptRegular,
  VideoRegular,
  WalletRegular,
} from '@fluentui/react-icons';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import type { ComponentType, ReactNode } from 'react';
import { ButtonLink, CardLink, TextLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { firstName } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { useNow } from '@/lib/hooks';
import { AppointmentCard } from '@/modules/appointments/AppointmentCard';
import { appointmentQueries } from '@/modules/appointments/api';
import { isOpenAppointment } from '@/modules/appointments/labels';
import { billQueries } from '@/modules/bills/api';
import { isDue, totalDue } from '@/modules/bills/labels';
import { greetingKey } from '@/modules/home/greeting';
import { prescriptionQueries } from '@/modules/prescriptions/api';
import { kindSegment, reportQueries } from '@/modules/reports/api';
import { REPORT_KIND } from '@/modules/reports/icons';
import { videoQueries } from '@/modules/video/api';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/home')({
  component: HomePage,
});

const useStyles = makeStyles({
  quick: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '10px',
    '@media (min-width: 720px)': { gridTemplateColumns: 'repeat(3, minmax(0, 1fr))' },
  },
  quickItem: {
    flexDirection: 'column',
    alignItems: 'flex-start',
    justifyContent: 'center',
    gap: '8px',
    minHeight: '96px',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  quickIcon: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '22px',
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gap: '16px',
    '@media (min-width: 1000px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', alignItems: 'start' },
  },
  section: { display: 'flex', flexDirection: 'column', gap: '10px', minWidth: 0 },
  sectionHead: { display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: '12px' },
  sectionTitle: { margin: 0, fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold },
  none: { margin: 0, color: tokens.colorNeutralForeground2 },
  banner: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    border: `2px solid ${tokens.colorStatusSuccessBorder1}`,
    backgroundColor: tokens.colorStatusSuccessBackground1,
  },
  bannerTitle: { margin: 0, fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  bannerText: { margin: 0 },
  due: { border: `1px solid ${tokens.colorStatusWarningBorder1}`, backgroundColor: tokens.colorStatusWarningBackground1 },
  dueAmount: { margin: 0, fontSize: tokens.fontSizeBase600, fontWeight: tokens.fontWeightSemibold },
});

interface QuickAction {
  to: '/doctors' | '/video' | '/reports' | '/bills' | '/prescriptions' | '/records';
  label: MessageKey;
  icon: ComponentType<{ className?: string }>;
}

const QUICK_ACTIONS: QuickAction[] = [
  { to: '/doctors', label: 'home.quick.book', icon: CalendarAddRegular },
  { to: '/video', label: 'home.quick.video', icon: VideoRegular },
  { to: '/reports', label: 'home.quick.reports', icon: DocumentTextRegular },
  { to: '/bills', label: 'home.quick.bills', icon: ReceiptRegular },
  { to: '/prescriptions', label: 'home.quick.rx', icon: PillRegular },
  { to: '/records', label: 'home.quick.records', icon: HeartPulseRegular },
];

function HomePage() {
  const styles = useStyles();
  const t = useT();
  const now = useNow(60_000);
  const { profile, profiles } = useActiveProfile();
  const sessions = useQuery(videoQueries.sessions(profile.id));
  const ready = (sessions.data ?? []).find((session) => session.canJoinNow);

  return (
    <PortalPage
      title={t(greetingKey(now), { name: firstName(profile.fullName) || profile.fullName })}
      documentTitle={t('nav.home')}
      subtitle={profiles.length > 1 ? t('home.viewing', { name: profile.fullName }) : undefined}
      width="wide"
    >
      {ready && (
        <div className={styles.banner} role="status">
          <h2 className={styles.bannerTitle}>{t('home.video.ready')}</h2>
          <p className={styles.bannerText}>{t('home.video.readyBody', { doctor: ready.practitionerName })}</p>
          <div>
            <ButtonLink to="/video/$sessionId" params={{ sessionId: ready.id }} appearance="primary" icon={<VideoRegular />}>
              {t('home.video.join')}
            </ButtonLink>
          </div>
        </div>
      )}

      <section className={styles.section} aria-labelledby="home-quick">
        <h2 id="home-quick" className={styles.sectionTitle}>
          {t('home.quick')}
        </h2>
        <nav className={styles.quick} aria-labelledby="home-quick">
          {QUICK_ACTIONS.map((action) => {
            const Icon = action.icon;
            return (
              <CardLink key={action.to} to={action.to} className={styles.quickItem}>
                <span className={styles.quickIcon} aria-hidden>
                  <Icon />
                </span>
                {t(action.label)}
              </CardLink>
            );
          })}
        </nav>
      </section>

      <div className={styles.columns}>
        <NextAppointment />
        <BillsDue />
        <RecentReports />
        <RecentPrescriptions />
      </div>
    </PortalPage>
  );
}

function Section({ id, title, action, children }: { id: string; title: string; action?: ReactNode; children: ReactNode }) {
  const styles = useStyles();
  return (
    <section className={styles.section} aria-labelledby={id}>
      <div className={styles.sectionHead}>
        <h2 id={id} className={styles.sectionTitle}>
          {title}
        </h2>
        {action}
      </div>
      {children}
    </section>
  );
}

function NextAppointment() {
  const styles = useStyles();
  const t = useT();
  const { profile } = useActiveProfile();
  const upcoming = useQuery(appointmentQueries.list(profile.id, 'upcoming'));
  const open = (upcoming.data ?? []).filter((row) => isOpenAppointment(row.status)).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
  const next = open[0];

  return (
    <Section id="home-next" title={t('home.next')} action={open.length > 1 ? <TextLink to="/appointments">{t('home.next.more', { count: open.length - 1 })}</TextLink> : undefined}>
      {upcoming.isPending ? (
        <LoadingCards count={1} />
      ) : upcoming.isError ? (
        <ErrorBlock error={upcoming.error} onRetry={() => void upcoming.refetch()} />
      ) : next ? (
        <AppointmentCard appointment={next} />
      ) : (
        <>
          <p className={styles.none}>{t('home.next.none')}</p>
          <div>
            <ButtonLink to="/doctors" appearance="primary" icon={<CalendarAddRegular />}>
              {t('home.book')}
            </ButtonLink>
          </div>
        </>
      )}
    </Section>
  );
}

function BillsDue() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const bills = useInfiniteQuery(billQueries.list(profile.id));
  const rows = bills.data?.pages.flatMap((page) => page.items) ?? [];
  const due = totalDue(rows);
  const count = rows.filter((bill) => isDue(bill.dueAmount)).length;

  // Nothing to say when everything is paid (or the list cannot load): the Bills page has the details.
  if (!bills.isSuccess || due <= 0) {
    return null;
  }
  return (
    <Section id="home-bills" title={t('home.bills')}>
      <div className={mergeClasses(styles.banner, styles.due)}>
        <p className={styles.dueAmount}>{t('home.bills.due', { amount: f.inr(due) })}</p>
        <p className={styles.bannerText}>{t('home.bills.count', { count })}</p>
        <div>
          <ButtonLink to="/bills" search={{ show: 'due' }} appearance="primary" icon={<WalletRegular />}>
            {t('home.bills.pay')}
          </ButtonLink>
        </div>
      </div>
    </Section>
  );
}

function RecentReports() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const reports = useQuery(reportQueries.list(profile.id));
  const recent = (reports.data ?? []).slice(0, 3);

  return (
    <Section id="home-reports" title={t('home.reports')} action={recent.length ? <TextLink to="/reports">{t('common.viewAll')}</TextLink> : undefined}>
      {reports.isPending ? (
        <LoadingCards count={2} />
      ) : reports.isError ? (
        <ErrorBlock error={reports.error} onRetry={() => void reports.refetch()} />
      ) : recent.length === 0 ? (
        <p className={styles.none}>{t('home.reports.none')}</p>
      ) : (
        recent.map((item) => {
          const kind = REPORT_KIND[item.kind];
          const Icon = kind.icon;
          return (
            <CardLink key={`${item.kind}-${item.id}`} to="/reports/$kind/$reportId" params={{ kind: kindSegment(item.kind), reportId: item.id }}>
              <ListRow icon={<Icon />} tone={kind.tone} title={item.title || t(kind.label)} subtitle={t('reports.released', { date: f.date(item.releasedAt) })} />
            </CardLink>
          );
        })
      )}
    </Section>
  );
}

function RecentPrescriptions() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const list = useInfiniteQuery(prescriptionQueries.list(profile.id));
  const recent = (list.data?.pages[0]?.items ?? []).filter((rx) => rx.status === 'Signed').slice(0, 2);

  return (
    <Section id="home-rx" title={t('home.rx')} action={recent.length ? <TextLink to="/prescriptions">{t('common.viewAll')}</TextLink> : undefined}>
      {list.isPending ? (
        <LoadingCards count={1} />
      ) : list.isError ? (
        <ErrorBlock error={list.error} onRetry={() => void list.refetch()} />
      ) : recent.length === 0 ? (
        <p className={styles.none}>{t('home.rx.none')}</p>
      ) : (
        recent.map((rx) => (
          <CardLink key={rx.id} to="/prescriptions/$prescriptionId" params={{ prescriptionId: rx.id }}>
            <ListRow
              icon={<PillRegular />}
              title={rx.medicines.slice(0, 2).join(', ') + (rx.medicines.length > 2 ? ` +${rx.medicines.length - 2}` : '') || t('rx.number', { number: rx.prescriptionNumber })}
              subtitle={[rx.doctor ? t('rx.by', { doctor: rx.doctor }) : null, f.date(rx.signedAt)].filter(Boolean).join(' · ')}
            />
          </CardLink>
        ))
      )}
    </Section>
  );
}
