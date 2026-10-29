import { Caption1, MessageBar, MessageBarBody, MessageBarTitle, Text, tokens } from '@fluentui/react-components';
import { TagMultipleRegular } from '@fluentui/react-icons';
import { formatDate } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { PACKAGE_USE_LABEL, SERVICE_CATEGORY_LABEL } from '@/modules/billing/labels';
import {
  languageNotes,
  RATE_LIST_PATH,
  rateListQueries,
  type RateLine,
  type RateList,
} from '@/modules/billing/rateListApi';
import { Amount, NoAccess } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/rate-list')({
  component: RateListPage,
});

const READERS = [
  'billing.tariffs.manage',
  'billing.charges.read',
  'billing.op.create',
  'billing.ip.manage',
  'billing.payments.collect',
] as const;

/**
 * The rate list (Clinical Establishments Rules 2012, r.9(i)): what a self-paying patient is charged today, worked out as
 * a bill works it out, to print and display at the counter in English and the local language.
 */
function RateListPage() {
  const canRead = usePermission([...READERS]);
  const rateList = useQuery({ ...rateListQueries.current(), enabled: canRead });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Rate list" />
        <NoAccess what="the rate list" />
      </Page>
    );
  }

  const view = rateList.data;
  return (
    <Page>
      <PageHeader
        title="Rate list"
        subtitle={
          view
            ? `What a patient paying for themselves is charged on ${formatDate(view.rates.asOn)}`
            : 'What a patient paying for themselves is charged today'
        }
        icon={<TagMultipleRegular />}
        actions={<DocumentButtons path={RATE_LIST_PATH} printLabel="Print for display" disabled={!view} />}
      />
      {view &&
        languageNotes(view).map((note) => (
          <MessageBar key={note.text} intent={note.intent}>
            {/* The title is what the bar is labelled by: without one, its label points at nothing. */}
            <MessageBarBody>
              <MessageBarTitle>Languages</MessageBarTitle>
              {note.text}
            </MessageBarBody>
          </MessageBar>
        ))}
      {rateList.isLoading && <LoadingState label="Working out the rates…" />}
      {rateList.error && <ErrorState error={rateList.error} onRetry={() => void rateList.refetch()} />}
      {view && <RateTables rates={view.rates} lang={view.localLanguage?.code} />}
    </Page>
  );
}

function RateTables({ rates, lang }: { rates: RateList; lang: string | undefined }) {
  const anyVideo = rates.doctors.some((d) => d.video !== null);
  const empty =
    !rates.registration &&
    rates.doctors.length === 0 &&
    rates.rooms.length === 0 &&
    rates.sections.length === 0 &&
    rates.packages.length === 0;
  if (empty) {
    return (
      <EmptyState
        icon={<TagMultipleRegular />}
        title="No services to list"
        description="Add services in Setup › Services, then the rate list shows what each costs."
      />
    );
  }

  return (
    <>
      {rates.concessionPercent > 0 && (
        <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
          The rates include the {rates.concessionPercent}% concession of the {rates.planName ?? 'default'} tariff.
        </Caption1>
      )}
      {rates.registration && (
        <Section title="Registration of a new patient" actions={<Payable line={rates.registration} />}>
          <Caption1>Charged once, on a patient&apos;s first visit.</Caption1>
        </Section>
      )}
      {rates.doctors.length > 0 && (
        <Section title="Consultation fees" flush>
          <DataTable
            ariaLabel="Consultation fees"
            rows={rates.doctors}
            getRowId={(d) => `${d.name}|${d.department ?? ''}`}
            columns={[
              { key: 'doctor', header: 'Doctor', render: (d) => <Text weight="semibold">{d.name}</Text> },
              { key: 'department', header: 'Department', render: (d) => d.department ?? '—' },
              { key: 'fee', header: 'In person', align: 'end', render: (d) => <Payable line={d.fee} /> },
              ...(anyVideo
                ? [
                    {
                      key: 'video',
                      header: 'By video',
                      align: 'end' as const,
                      render: (d: (typeof rates.doctors)[number]) => (d.video ? <Payable line={d.video} /> : '—'),
                    },
                  ]
                : []),
              {
                key: 'followUp',
                header: 'Follow-up',
                render: (d) =>
                  d.freeFollowUpDays > 0 ? `Free within ${d.freeFollowUpDays} days` : 'Charged as a consultation',
              },
            ]}
            size="small"
          />
        </Section>
      )}
      {rates.rooms.length > 0 && (
        <Section title="Rooms (per day)" flush>
          <DataTable
            ariaLabel="Rooms"
            rows={rates.rooms}
            getRowId={(r) => r.day.code}
            columns={[
              {
                key: 'room',
                header: 'Room',
                render: (r) => (
                  <ServiceName
                    line={r.day}
                    lang={lang}
                    note={r.intensiveCare ? 'Intensive care: no GST on the room' : undefined}
                  />
                ),
              },
              { key: 'wards', header: 'Wards', priority: 'low', render: (r) => r.wards.join(', ') },
              { key: 'bed', header: 'Bed', align: 'end', render: (r) => <Payable line={r.day} withGst /> },
              {
                key: 'nursing',
                header: 'Nursing',
                align: 'end',
                render: (r) => (r.nursing ? <Payable line={r.nursing} /> : '—'),
              },
              {
                key: 'visit',
                header: "Doctor's visit",
                align: 'end',
                render: (r) => (r.visit ? <Payable line={r.visit} /> : '—'),
              },
              {
                key: 'multiplier',
                header: 'Multiplier',
                align: 'end',
                render: (r) => `×${r.multiplier}`,
              },
            ]}
            size="small"
          />
          <Caption1 block style={{ color: tokens.colorNeutralForeground3, padding: '8px 12px' }}>
            For a patient in a room, procedures, surgery, nursing and doctors&apos; visits cost their rate times the
            room&apos;s figure. The nursing and visit charges above already include it.
          </Caption1>
        </Section>
      )}
      {rates.sections.map((section) => (
        <Section key={section.category} title={SERVICE_CATEGORY_LABEL[section.category]} flush>
          <DataTable
            ariaLabel={SERVICE_CATEGORY_LABEL[section.category]}
            rows={section.lines}
            getRowId={(line) => line.code}
            columns={[
              { key: 'service', header: 'Service', render: (line) => <ServiceName line={line} lang={lang} /> },
              {
                key: 'gst',
                header: 'GST',
                align: 'end',
                priority: 'low',
                render: (line) => (line.gstRate > 0 ? `${line.gstRate}%` : 'Exempt'),
              },
              { key: 'payable', header: 'Patient pays', align: 'end', render: (line) => <Payable line={line} /> },
            ]}
            size="small"
          />
        </Section>
      ))}
      {rates.packages.length > 0 && (
        <Section title="Packages" flush>
          <DataTable
            ariaLabel="Packages"
            rows={rates.packages}
            getRowId={(p) => p.price.code}
            columns={[
              { key: 'package', header: 'Package', render: (p) => <ServiceName line={p.price} lang={lang} /> },
              {
                key: 'stay',
                header: 'Stay',
                align: 'end',
                render: (p) => (p.lengthOfStayDays ? `${p.lengthOfStayDays} days` : '—'),
              },
              { key: 'for', header: 'For', priority: 'low', render: (p) => PACKAGE_USE_LABEL[p.usedFor] },
              { key: 'payable', header: 'Patient pays', align: 'end', render: (p) => <Payable line={p.price} /> },
            ]}
            size="small"
          />
        </Section>
      )}
    </>
  );
}

/**
 * The English name with the code, and the local name under it when there is one, marked with its language so a screen
 * reader reads it as that language (WCAG 3.1.2).
 */
function ServiceName({ line, note, lang }: { line: RateLine; note?: string; lang: string | undefined }) {
  return (
    <span>
      <Text weight="semibold">{line.name}</Text>
      {line.localName && (
        <Caption1 block lang={lang}>
          {line.localName}
        </Caption1>
      )}
      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
        {note ? `${line.code} · ${note}` : line.code}
      </Caption1>
    </span>
  );
}

/** What the patient pays; a price the counter sets by what is used, and one it may change, say so. */
function Payable({ line, withGst = false }: { line: RateLine; withGst?: boolean }) {
  if (line.mayVary && line.payable === 0) {
    return <Caption1>By what is used</Caption1>;
  }
  return (
    <span>
      <Amount value={line.payable} strong />
      {line.mayVary && <Caption1 block>The counter may change it</Caption1>}
      {withGst && line.gstRate > 0 && <Caption1 block>With {line.gstRate}% GST</Caption1>}
    </span>
  );
}
