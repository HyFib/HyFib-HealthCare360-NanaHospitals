import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatDate, formatDateTime, formatPhone, humanize, isApiError } from '@hc360/core';
import { ErrorState, Section, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { privacyQueries, type DataRequestDetail, type DataSummary, type SummaryRecipient } from './api';
import { consentTypeLabel, genderLabel } from './labels';
import { PlainTable } from './PlainTable';
import { printDataSummary } from './print';
import { birthDateText, isOpenStatus, itemDatesText } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  block: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  header: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start' },
  panel: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '4px 0 12px 12px' },
});

/**
 * Access requests: the summary of the patient's personal data (DPDP Act s11). For a completed request it is exactly
 * the summary that was given; otherwise it is prepared now, to check before answering. Opening it is audited.
 */
export function DataSummaryPanel({ request }: { request: DataRequestDetail }) {
  const notify = useNotify();
  const [printing, setPrinting] = useState(false);
  const given = request.hasSummary && !isOpenStatus(request.status);
  const summary = useQuery({ ...privacyQueries.summary(request.id), enabled: Boolean(request.patientId) });

  if (!request.patientId) {
    return (
      <Section title="Data summary">
        <Text>
          The person is not a registered patient, so HC360 holds no patient record for them. Say in the reply what the
          hospital holds, if anything.
        </Text>
      </Section>
    );
  }

  const print = async (data: DataSummary) => {
    setPrinting(true);
    try {
      await printDataSummary(data, request.requestNumber);
    } catch (error) {
      notify.error(error, 'Could not print the summary');
    } finally {
      setPrinting(false);
    }
  };

  return (
    <Section
      title="Data summary"
      description={
        given
          ? `This is the summary that was given${request.closedAt ? ` on ${formatDateTime(request.closedAt)}` : ''}, kept exactly as it was.`
          : isOpenStatus(request.status)
            ? 'Prepared now from the records. When you complete the request, the summary as it is then is kept as the record of what was given.'
            : 'No summary was given with the answer. This one is prepared now from the records.'
      }
      actions={
        summary.data && (
          <Button
            icon={printing ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
            // While printing it stays focusable (aria-disabled) and ignores clicks: a disabled button drops focus.
            disabledFocusable={printing}
            onClick={() => summary.data && void print(summary.data)}
          >
            Print summary
          </Button>
        )
      }
    >
      {summary.error ? (
        isApiError(summary.error) && summary.error.code === 'patient.restricted' ? (
          <MessageBar intent="warning">
            <MessageBarBody>{summary.error.userMessage}</MessageBarBody>
          </MessageBar>
        ) : (
          <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />
        )
      ) : !summary.data ? (
        <Spinner size="small" label="Preparing the summary…" labelPosition="after" />
      ) : (
        <SummaryView summary={summary.data} />
      )}
    </Section>
  );
}

function Recipients({ list, empty, label }: { list: SummaryRecipient[]; empty: string; label: string }) {
  const styles = useStyles();
  return list.length === 0 ? (
    <Text className={styles.muted}>{empty}</Text>
  ) : (
    <PlainTable
      ariaLabel={label}
      rows={list}
      getRowId={(r, index) => `${r.name}-${index}`}
      columns={[
        { key: 'name', header: 'Organisation', render: (r) => <Text weight="semibold">{r.name}</Text> },
        { key: 'role', header: 'Role', render: (r) => r.role },
        { key: 'data', header: 'What they received', render: (r) => r.data },
      ]}
    />
  );
}

function SummaryView({ summary }: { summary: DataSummary }) {
  const styles = useStyles();
  const p = summary.person;
  return (
    <div className={styles.stack}>
      <Caption1 className={styles.muted}>Prepared {formatDateTime(summary.generatedAt)}</Caption1>

      <div className={styles.block}>
        <Subtitle2 as="h3">Registration details</Subtitle2>
        <DetailGrid minColumnWidth={200}>
          <Detail label="Name">{p.fullName}</Detail>
          <Detail label="UHID" mono>
            {p.uhid}
          </Detail>
          <Detail label="Gender">{genderLabel[p.gender] ?? humanize(p.gender)}</Detail>
          <Detail label="Date of birth">{birthDateText(p)}</Detail>
          <Detail label="Mobile number">{p.phone ? formatPhone(p.phone) : null}</Detail>
          <Detail label="Other phone number">{p.alternatePhone ? formatPhone(p.alternatePhone) : null}</Detail>
          <Detail label="E-mail address">{p.email}</Detail>
          <Detail label="Emergency contact">{p.emergencyContact}</Detail>
          <Detail label="Address" wide>
            {p.address}
          </Detail>
          <Detail label="ABHA number">{p.abhaNumber}</Detail>
          <Detail label="ABHA address">{p.abhaAddress}</Detail>
          <Detail label="Aadhaar">{p.aadhaarLast4}</Detail>
          <Detail label="Photograph on record">{p.hasPhoto ? 'Yes' : 'No'}</Detail>
          <Detail label="Registered">
            {formatDateTime(p.registeredAt)}
            {p.registeredAtFacility ? ` at ${p.registeredAtFacility}` : ''}
          </Detail>
        </DetailGrid>
      </div>

      <div className={styles.block}>
        <Subtitle2 as="h3">What the hospital holds</Subtitle2>
        <Accordion multiple collapsible defaultOpenItems={summary.categories.slice(0, 1).map((c) => c.key)}>
          {summary.categories.map((c) => {
            const records = c.items.reduce((total, item) => total + item.count, 0);
            return (
              <AccordionItem key={c.key} value={c.key}>
                <AccordionHeader>
                  <span className={styles.header}>
                    <Text weight="semibold">{c.title}</Text>
                    <Caption1 className={styles.muted}>
                      {c.items.length === 0
                        ? 'The details above'
                        : `${records.toLocaleString('en-IN')} record${records === 1 ? '' : 's'}`}
                    </Caption1>
                  </span>
                </AccordionHeader>
                <AccordionPanel>
                  <div className={styles.panel}>
                    <Text>{c.description}</Text>
                    <DetailGrid minColumnWidth={200}>
                      <Detail label="Why we hold it">{c.purpose}</Detail>
                      <Detail label="Legal basis">{c.basis}</Detail>
                      <Detail label="How long we keep it">{c.retention}</Detail>
                    </DetailGrid>
                    {c.items.length > 0 && (
                      <PlainTable
                        ariaLabel={`${c.title}: records held`}
                        rows={c.items}
                        getRowId={(item, index) => `${item.key}-${index}`}
                        columns={[
                          { key: 'label', header: 'Records', render: (item) => item.label },
                          {
                            key: 'count',
                            header: 'How many',
                            align: 'end',
                            width: 100,
                            render: (item) => item.count.toLocaleString('en-IN'),
                          },
                          {
                            key: 'dates',
                            header: 'Dates',
                            render: (item) => itemDatesText(item.first, item.last) || '—',
                          },
                        ]}
                      />
                    )}
                  </div>
                </AccordionPanel>
              </AccordionItem>
            );
          })}
        </Accordion>
      </div>

      <div className={styles.block}>
        <Subtitle2 as="h3">Consents</Subtitle2>
        {summary.consents.length === 0 ? (
          <Text className={styles.muted}>No consents are recorded.</Text>
        ) : (
          <PlainTable
            ariaLabel="Consents"
            rows={summary.consents}
            getRowId={(c, index) => `${c.type}-${index}`}
            columns={[
              { key: 'type', header: 'Consent', render: (c) => consentTypeLabel(c.type, humanize) },
              { key: 'answer', header: 'Answer', render: (c) => (c.granted ? 'Given' : 'Refused') },
              { key: 'captured', header: 'Recorded', render: (c) => formatDate(c.capturedAt) },
              { key: 'revoked', header: 'Withdrawn', render: (c) => (c.revokedAt ? formatDate(c.revokedAt) : '—') },
            ]}
          />
        )}
      </div>

      <div className={styles.block}>
        <Subtitle2 as="h3">Who it was shared with</Subtitle2>
        <Recipients
          list={summary.sharedWith}
          label="Organisations the data was shared with"
          empty="Not shared with any other organisation."
        />
      </div>

      <div className={styles.block}>
        <Subtitle2 as="h3">Service providers who process it for the hospital</Subtitle2>
        <Recipients
          list={summary.processors}
          label="Service providers"
          empty="No outside service provider processes it."
        />
      </div>

      <DetailGrid minColumnWidth={220}>
        <Detail label="Nominee">
          {summary.nominee ? `${summary.nominee.name} (${summary.nominee.relationship})` : 'None named'}
        </Detail>
        <Detail label="Whom to contact">
          {[
            summary.contact.name ?? summary.contact.title,
            summary.contact.email,
            summary.contact.phone ? formatPhone(summary.contact.phone) : null,
          ]
            .filter(Boolean)
            .join(' · ')}
        </Detail>
      </DetailGrid>
    </div>
  );
}
