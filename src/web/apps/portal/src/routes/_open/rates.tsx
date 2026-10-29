import { Field, makeStyles, SearchBox, Select, tokens } from '@fluentui/react-components';
import { ReceiptMoneyRegular } from '@fluentui/react-icons';
import { isApiError, useDebouncedValue } from '@hc360/core';
import { EmptyState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState, type ReactNode } from 'react';
import { DocumentActions } from '@/components/DocumentActions';
import { PortalUnavailable } from '@/components/FeatureOff';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards, Notice } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { usePortalFeatures } from '@/lib/features';
import { guidParam, textParam } from '@/lib/ids';
import type { PublicRateList, RateLine } from '@/lib/types';
import { rateListPdfPath, rateListQueries } from '@/modules/rates/api';
import { categoryKey, filterRateList, isEmptyRateList, rateNotes } from '@/modules/rates/model';
import { percentText, rateListDocument } from '@/modules/rates/print';
import { ReportCard } from '@/modules/reports/ReportParts';

interface RateListSearch {
  facilityId?: string;
  q?: string;
}

/**
 * The rate list each facility displays at its counter (Clinical Establishments Rules 2012, r.9(i)), open to everyone:
 * what a patient who pays for themselves is charged, worked out as their bill is. Each service's name is also in the
 * facility's local language where the hospital has entered it; "Download PDF" gives the list the counter prints.
 */
export const Route = createFileRoute('/_open/rates')({
  validateSearch: (search: Record<string, unknown>): RateListSearch => ({
    facilityId: guidParam(search.facilityId),
    q: textParam(search.q),
  }),
  component: RateListPage,
});

const useStyles = makeStyles({
  controls: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 240px), 1fr))',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  list: { listStyleType: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  row: {
    display: 'flex',
    flexWrap: 'wrap',
    justifyContent: 'space-between',
    alignItems: 'baseline',
    gap: '4px 16px',
    padding: '10px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none' },
  },
  what: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0, flex: '1 1 200px' },
  name: { fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  local: { overflowWrap: 'anywhere' },
  meta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  amount: { fontWeight: tokens.fontWeightSemibold, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' },
  text: { margin: 0, fontSize: tokens.fontSizeBase300, lineHeight: tokens.lineHeightBase300 },
  notes: { margin: 0, paddingLeft: '20px', display: 'flex', flexDirection: 'column', gap: '6px' },
});

function RateListPage() {
  const styles = useStyles();
  const t = useT();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { portal, settled } = usePortalFeatures();
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  // Asked only once the hospital's switches are known, so a closed portal is never asked.
  const facilities = useQuery({ ...rateListQueries.facilities(), enabled: portal && settled });
  const facilityId = search.facilityId ?? facilities.data?.[0]?.id;
  const list = useQuery({ ...rateListQueries.list(facilityId ?? ''), enabled: portal && settled && !!facilityId });

  if (!portal) {
    return (
      <PortalPage title={t('rates.title')} width="narrow">
        <PortalUnavailable />
      </PortalPage>
    );
  }

  if (facilities.isError) {
    return (
      <PortalPage title={t('rates.title')}>
        <ErrorBlock error={facilities.error} onRetry={() => void facilities.refetch()} />
      </PortalPage>
    );
  }

  if (facilities.data?.length === 0) {
    return (
      <PortalPage title={t('rates.title')} width="narrow">
        <EmptyState icon={<ReceiptMoneyRegular />} title={t('rates.none.title')} description={t('rates.none.body')} />
      </PortalPage>
    );
  }

  const choices = facilities.data ?? [];
  return (
    <PortalPage
      title={t('rates.title')}
      subtitle={list.data && <Subtitle list={list.data} />}
      actions={list.data && facilityId && <RateListActions list={list.data} facilityId={facilityId} />}
    >
      <section className={styles.controls} aria-label={t('rates.search.label')}>
        {choices.length > 1 && (
          <Field label={t('rates.branch')}>
            <Select
              size="large"
              value={facilityId ?? ''}
              onChange={(_, data) =>
                void navigate({ search: (prev) => ({ ...prev, facilityId: data.value || undefined }), replace: true })
              }
            >
              {choices.map((facility) => (
                <option key={facility.id} value={facility.id}>
                  {facility.city ? `${facility.name}, ${facility.city}` : facility.name}
                </option>
              ))}
            </Select>
          </Field>
        )}
        <Field label={t('rates.search.label')}>
          <SearchBox
            size="large"
            value={term}
            onChange={(_, data) => setTerm(data.value)}
            placeholder={t('rates.search.placeholder')}
            autoComplete="off"
            enterKeyHint="search"
          />
        </Field>
      </section>
      {list.isPending || facilities.isPending ? (
        <LoadingCards />
      ) : list.isError ? (
        isApiError(list.error) && list.error.status === 404 ? (
          <Notice intent="warning">{t('rates.notFound')}</Notice>
        ) : (
          <ErrorBlock error={list.error} onRetry={() => void list.refetch()} />
        )
      ) : (
        <Rates list={list.data} term={search.q ?? ''} />
      )}
    </PortalPage>
  );
}

function Subtitle({ list }: { list: PublicRateList }) {
  const t = useT();
  const f = useFormat();
  return <>{t('rates.subtitle', { facility: list.facility.name, date: f.calendarDate(list.rates.asOn) })}</>;
}

function RateListActions({ list, facilityId }: { list: PublicRateList; facilityId: string }) {
  const t = useT();
  const f = useFormat();
  return (
    <DocumentActions
      pdf={rateListPdfPath(facilityId)}
      build={() => rateListDocument(list, { t, f, printedAt: new Date().toISOString() })}
    />
  );
}

function Rates({ list, term }: { list: PublicRateList; term: string }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const rates = filterRateList(list.rates, term);
  const lang = list.localLanguage?.code;
  const money = (line: RateLine) => f.inr(line.payable);

  if (term && isEmptyRateList(rates)) {
    return <EmptyState icon={<ReceiptMoneyRegular />} title={t('rates.search.none', { term })} />;
  }

  const service = (line: RateLine, detail?: ReactNode) => (
    <Row key={line.code} amount={money(line)}>
      <ServiceName line={line} lang={lang} />
      {detail}
    </Row>
  );

  return (
    <>
      {rates.registration && (
        <ReportCard title={t('rates.registration')}>
          <ul className={styles.list}>{service(rates.registration)}</ul>
        </ReportCard>
      )}
      {rates.doctors.length > 0 && (
        <ReportCard title={t('rates.consultations')}>
          <ul className={styles.list}>
            {rates.doctors.map((doctor, index) => (
              <Row key={`${doctor.name}|${index}`} amount={`${t('rates.inPerson')} ${money(doctor.fee)}`}>
                <span className={styles.name}>{doctor.name}</span>
                {doctor.department && <span className={styles.meta}>{doctor.department}</span>}
                <span className={styles.meta}>
                  {t('rates.followUp')}:{' '}
                  {doctor.freeFollowUpDays > 0
                    ? t('rates.followUpFree', { days: doctor.freeFollowUpDays })
                    : t('rates.followUpCharged')}
                  {doctor.video && ` · ${t('rates.byVideo')} ${money(doctor.video)}`}
                </span>
              </Row>
            ))}
          </ul>
        </ReportCard>
      )}
      {rates.rooms.length > 0 && (
        <ReportCard title={t('rates.rooms')}>
          <ul className={styles.list}>
            {rates.rooms.map((room) =>
              service(
                room.day,
                <span className={styles.meta}>
                  {[
                    room.intensiveCare ? t('rates.intensiveCare') : '',
                    room.wards.length ? t('rates.wards', { wards: room.wards.join(', ') }) : '',
                    room.nursing ? `${t('rates.nursing')} ${money(room.nursing)}` : '',
                    room.visit ? `${t('rates.visit')} ${money(room.visit)}` : '',
                    t('rates.multiplier', { multiplier: f.number(room.multiplier) }),
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </span>,
              ),
            )}
          </ul>
          <p className={styles.meta}>{t('rates.note.multiplier')}</p>
        </ReportCard>
      )}
      {rates.sections.map((section) => (
        <ReportCard key={section.category} title={t(categoryKey(section.category))}>
          <ul className={styles.list}>{section.lines.map((line) => service(line))}</ul>
        </ReportCard>
      ))}
      {rates.packages.length > 0 && (
        <ReportCard title={t('rates.packages')}>
          <ul className={styles.list}>
            {rates.packages.map((pack) =>
              service(
                pack.price,
                <span className={styles.meta}>
                  {[
                    pack.lengthOfStayDays ? t('rates.packageStay', { days: pack.lengthOfStayDays }) : '',
                    t(`rates.packageFor.${pack.usedFor}`),
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </span>,
              ),
            )}
          </ul>
          <p className={styles.meta}>{t('rates.note.packages')}</p>
        </ReportCard>
      )}
      <ReportCard title={t('rates.notes')}>
        <ul className={styles.notes}>
          {rateNotes(list.rates, (value) => percentText(value, f)).map((note) => (
            <li key={note.key} className={styles.text}>
              {t(note.key, note.vars)}
            </li>
          ))}
        </ul>
      </ReportCard>
    </>
  );
}

function Row({ amount, children }: { amount: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <li className={styles.row}>
      <span className={styles.what}>{children}</span>
      <span className={styles.amount}>{amount}</span>
    </li>
  );
}

/** The English name, the local name under it in its own language, and the code with any GST. */
function ServiceName({ line, lang }: { line: RateLine; lang: string | undefined }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  return (
    <>
      <span className={styles.name}>
        {line.name}
        {line.mayVary && ' *'}
      </span>
      {line.localName && (
        <span className={styles.local} lang={lang}>
          {line.localName}
        </span>
      )}
      <span className={styles.meta}>
        {t('rates.code')} {line.code}
        {line.gstRate > 0 && ` · ${t('rates.withGst', { percent: percentText(line.gstRate, f) })}`}
      </span>
    </>
  );
}
