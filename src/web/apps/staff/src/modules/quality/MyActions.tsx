import { Caption1, Checkbox, Text } from '@fluentui/react-components';
import { formatDateOnly } from '@hc360/core';
import { DataTable } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { FilterPad } from '@/modules/hr/shared/layout';
import { qualityQueries } from './api';
import { CapaStatusBadge } from './badges';
import { CapaActionButtons } from './CapaActions';
import { capaSourceLabel } from './labels';

/** The signed-in person's corrective/preventive actions (any facility), with start and "mark done". */
export function MyActionsTable() {
  const navigate = useNavigate();
  const [includeClosed, setIncludeClosed] = useState(false);
  const capas = useQuery(qualityQueries.myCapas(includeClosed));

  return (
    <>
      <FilterPad>
        <Checkbox checked={includeClosed} onChange={(_, d) => setIncludeClosed(Boolean(d.checked))} label="Include finished and cancelled actions" />
      </FilterPad>
      <DataTable
        ariaLabel="My corrective and preventive actions"
        rows={capas.data}
        loading={capas.isLoading}
        error={capas.error}
        onRetry={() => void capas.refetch()}
        getRowId={(c) => c.id}
        onRowClick={(c) => void navigate({ to: '/quality/capas/$capaId', params: { capaId: c.id } })}
        columns={[
          {
            key: 'action',
            header: 'Action',
            render: (c) => (
              <span>
                <Text weight="semibold">{c.action}</Text>
                <Caption1 block>
                  {c.capaNumber} · {c.type} · {c.incidentNumber ? `incident ${c.incidentNumber}` : capaSourceLabel[c.source]}
                </Caption1>
              </span>
            ),
          },
          { key: 'due', header: 'Due', width: 120, render: (c) => formatDateOnly(c.dueDate) },
          { key: 'status', header: 'Status', render: (c) => <CapaStatusBadge status={c.status} isOverdue={c.isOverdue} /> },
        ]}
        rowActions={(c) => <CapaActionButtons capa={c} size="small" />}
        emptyTitle={includeClosed ? 'No actions assigned to you' : 'Nothing to do'}
        emptyDescription={includeClosed ? undefined : 'Actions the quality team assigns to you appear here.'}
        size="small"
      />
    </>
  );
}
