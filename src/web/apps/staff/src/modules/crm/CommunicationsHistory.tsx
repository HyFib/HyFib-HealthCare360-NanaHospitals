import { Caption1, Field, Select, Text } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { DataTable, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { COMMUNICATIONS_PAGE_SIZE, crmQueries } from './api';
import {
  COMMUNICATION_KINDS,
  communicationDetail,
  communicationKindLabel,
  communicationOutcome,
  communicationTitle,
  type CommunicationKind,
} from './communications';

/**
 * Everything the hospital has sent the patient or called them about, newest first (CRM-04): appointment and report
 * messages, campaigns and follow-up calls. Each page read is audited on the server as access to the patient's record.
 */
export function CommunicationsHistory({ patientId }: { patientId: string }) {
  const [kind, setKind] = useState<CommunicationKind | undefined>();
  const [page, setPage] = useState(1);
  const history = useQuery(crmQueries.communications(patientId, { kind, page }));

  return (
    <Section
      title="Communications"
      actions={
        <Field label="Show" orientation="horizontal" size="small">
          <Select
            size="small"
            value={kind ?? ''}
            onChange={(_, d) => {
              setKind((d.value || undefined) as CommunicationKind | undefined);
              setPage(1);
            }}
          >
            <option value="">Everything</option>
            {COMMUNICATION_KINDS.map((k) => (
              <option key={k} value={k}>
                {communicationKindLabel[k]}
              </option>
            ))}
          </Select>
        </Field>
      }
    >
      <DataTable
        ariaLabel="Communications"
        size="small"
        rows={history.data?.items}
        loading={history.isLoading}
        error={history.error}
        onRetry={() => void history.refetch()}
        getRowId={(e) => e.id}
        page={history.data?.page ?? page}
        pageSize={COMMUNICATIONS_PAGE_SIZE}
        totalCount={history.data?.totalCount}
        onPageChange={setPage}
        columns={[
          { key: 'when', header: 'When', width: 150, render: (e) => formatDateTime(e.occurredAt) },
          {
            key: 'what',
            header: 'What',
            render: (e) => (
              <>
                <Text block>{communicationTitle(e)}</Text>
                <Caption1>{communicationKindLabel[e.kind]}</Caption1>
              </>
            ),
          },
          { key: 'how', header: 'How', priority: 'low', render: (e) => communicationDetail(e) },
          {
            key: 'outcome',
            header: 'Outcome',
            render: (e) => {
              const outcome = communicationOutcome(e);
              return <StatusBadge status={outcome.label} label={outcome.label} tone={outcome.tone} />;
            },
          },
        ]}
        emptyTitle={kind ? `No ${communicationKindLabel[kind].toLowerCase()}s` : 'Nothing sent yet'}
        emptyDescription="Appointment reminders, report messages, campaigns and follow-up calls to this patient appear here."
      />
      <Caption1 block style={{ marginTop: 8 }}>
        Message text is not kept; numbers and addresses are shown masked.
      </Caption1>
    </Section>
  );
}
