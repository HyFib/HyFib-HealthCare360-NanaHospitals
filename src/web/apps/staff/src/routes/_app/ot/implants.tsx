import { Button, Field, Input, makeStyles, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import { BoxSearchRegular, SearchRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { usePermission } from '@/lib/permissions';
import { otQueries } from '@/modules/ot/api';
import { NoAccess } from '@/modules/ot/shared/feedback';

interface TraceSearch {
  lot?: string;
  serial?: string;
}

const CODE = /^[A-Za-z0-9./_-]{1,64}$/;

export const Route = createFileRoute('/_app/ot/implants')({
  validateSearch: (s: Record<string, unknown>): TraceSearch => ({
    lot: typeof s.lot === 'string' && CODE.test(s.lot) ? s.lot : undefined,
    serial: typeof s.serial === 'string' && CODE.test(s.serial) ? s.serial : undefined,
  }),
  component: ImplantTracePage,
});

const useStyles = makeStyles({
  form: { padding: '0 16px 12px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

/**
 * Device recall: every patient who received an implant with a given lot or serial number. Results identify
 * patients, so each search is written to the audit log.
 */
function ImplantTracePage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canTrace = usePermission('ot.theatres.manage');
  const [lot, setLot] = useState(search.lot ?? '');
  const [serial, setSerial] = useState(search.serial ?? '');
  const results = useQuery({ ...otQueries.trace(search.lot ?? '', search.serial ?? ''), enabled: canTrace && Boolean(search.lot || search.serial) });

  if (!canTrace) {
    return (
      <Page>
        <NoAccess what="implant tracing" />
      </Page>
    );
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    void navigate({ search: { lot: lot.trim() || undefined, serial: serial.trim() || undefined } });
  };

  return (
    <Page>
      <PageHeader
        title="Implant trace"
        icon={<BoxSearchRegular />}
        subtitle="For a manufacturer recall: find every patient who received an implant from a lot, or a single device by serial number."
      />
      <MessageBar intent="info" icon={<ShieldKeyholeRegular />}>
        <MessageBarBody>Results identify patients. Every search is recorded in the audit log with your name.</MessageBarBody>
      </MessageBar>
      <Section flush>
        <form className={styles.form} onSubmit={submit} noValidate>
          <FilterBar>
            <Field label="Lot / batch number">
              <Input value={lot} onChange={(_, d) => setLot(d.value)} placeholder="Scan or type" autoFocus maxLength={64} />
            </Field>
            <Field label="Serial number">
              <Input value={serial} onChange={(_, d) => setSerial(d.value)} placeholder="Scan or type" maxLength={64} />
            </Field>
            <Button type="submit" appearance="primary" icon={<SearchRegular />} disabled={!lot.trim() && !serial.trim()}>
              Trace
            </Button>
          </FilterBar>
        </form>
        {!search.lot && !search.serial ? (
          <EmptyState title="Enter a lot or serial number" description="Use the number from the manufacturer's recall notice." icon={<BoxSearchRegular />} />
        ) : (
          <DataTable
            ariaLabel="Patients who received the implant"
            rows={results.data}
            loading={results.isLoading}
            error={results.error}
            onRetry={() => void results.refetch()}
            getRowId={(r) => r.materialId}
            onRowClick={(r) => void navigate({ to: '/ot/bookings/$id', params: { id: r.surgeryBookingId } })}
            columns={[
              {
                key: 'patient',
                header: 'Patient',
                render: (r) => (
                  <span>
                    <Text weight="semibold">{r.patientName}</Text> <Text className={styles.mono}>{r.uhid}</Text>
                  </span>
                ),
              },
              { key: 'phone', header: 'Mobile', width: 140, render: (r) => r.phone ?? '—' },
              { key: 'item', header: 'Implant', render: (r) => `${r.itemName}${r.manufacturer ? ` · ${r.manufacturer}` : ''}` },
              {
                key: 'trace',
                header: 'Lot / serial',
                render: (r) => (
                  <span className={styles.mono}>
                    {[r.lotNumber ? `Lot ${r.lotNumber}` : null, r.serialNumber ? `S/N ${r.serialNumber}` : null].filter(Boolean).join(' · ') || '—'}
                  </span>
                ),
              },
              { key: 'booking', header: 'Surgery', priority: 'low', render: (r) => <Text className={styles.mono}>{r.bookingNumber}</Text> },
              { key: 'at', header: 'Implanted', priority: 'low', render: (r) => formatDateTime(r.recordedAt) },
            ]}
            emptyTitle="No patient received this implant"
            emptyDescription="Check the number on the recall notice. Lot and serial numbers are matched exactly."
          />
        )}
      </Section>
    </Page>
  );
}
