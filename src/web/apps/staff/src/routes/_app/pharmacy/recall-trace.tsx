import {
  Badge,
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
} from '@fluentui/react-components';
import { PrintRegular, SearchRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, ErrorState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import type { ItemLookup } from '@/modules/inventory/api';
import { formatQty } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import { PHARMACY_PERMISSIONS } from '@/modules/pharmacy/permissions';
import {
  printRecallTrace,
  recallTraceApi,
  SOURCE_LABEL,
  type RecallStockRow,
  type RecallTraceRow,
} from '@/modules/pharmacy/recallTrace';

export const Route = createFileRoute('/_app/pharmacy/recall-trace')({
  component: () => (
    <RequirePermission permission={PHARMACY_PERMISSIONS.registers} what="the recall trace">
      <RecallTracePage />
    </RequirePermission>
  ),
});

/**
 * From a recalled batch to every patient it was dispensed, sold or issued to, with what came back, and the stores still
 * holding it: the list the pharmacy works through to call patients back. Every run is recorded with who ran it.
 */
function RecallTracePage() {
  const notify = useNotify();
  const letterhead = useLetterhead();
  const [item, setItem] = useState<ItemLookup | null>(null);
  const [batch, setBatch] = useState('');
  const trace = useMutation({
    mutationFn: ({ itemId, batchNumber }: { itemId: string; batchNumber: string }) =>
      recallTraceApi.trace(itemId, batchNumber),
  });
  const t = trace.data;
  return (
    <Page>
      <PageHeader
        title="Recall trace"
        subtitle="Every patient a recalled batch reached: OP dispensing, counter sales and ward issues."
      />
      <Section title="Batch">
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <div style={{ minWidth: 280, flex: 2 }}>
            <ItemPicker label="Item" showStock={false} onPick={setItem} />
            {item && <Text size={200}>{`${item.code} ${item.name}`}</Text>}
          </div>
          <Field label="Batch number on the recall notice" style={{ flex: 1, minWidth: 180 }}>
            <Input value={batch} maxLength={40} onChange={(_, d) => setBatch(d.value)} />
          </Field>
          <Button
            appearance="primary"
            icon={<SearchRegular />}
            disabled={!item || batch.trim() === '' || trace.isPending}
            onClick={() => item && trace.mutate({ itemId: item.id, batchNumber: batch.trim() })}
          >
            Trace
          </Button>
        </div>
        <MessageBar intent="info" style={{ marginTop: 12 }}>
          <MessageBarBody>
            <MessageBarTitle>Recorded</MessageBarTitle>
            The list shows patients&apos; names and phone numbers. Each run is recorded with your name and the batch.
          </MessageBarBody>
        </MessageBar>
      </Section>
      {trace.error ? <ErrorState error={trace.error} /> : null}
      {t && (
        <>
          <Section
            title={`${t.patientCount} patient(s): ${formatQty(t.quantityWithPatients)} still with patients`}
            actions={
              <Button
                icon={<PrintRegular />}
                onClick={() =>
                  void printRecallTrace(t, letterhead).catch((error: unknown) => notify.error(error, 'Printing failed'))
                }
              >
                Print call-back list
              </Button>
            }
          >
            <Text size={200}>
              {`${t.itemCode} ${t.itemName}, batch ${t.batchNumber}. Run by ${t.generatedBy} at ${formatDateTime(t.generatedAt)}.`}
            </Text>
            <DataTable<RecallTraceRow>
              ariaLabel="Patients who had the batch"
              rows={t.patients}
              getRowId={(r) => `${r.document}-${r.at}`}
              size="small"
              emptyTitle="No patient had this batch"
              columns={[
                { key: 'at', header: 'When', render: (r) => formatDateTime(r.at) },
                { key: 'how', header: 'How', render: (r) => SOURCE_LABEL[r.source] },
                { key: 'doc', header: 'Document', render: (r) => <Text font="monospace">{r.document}</Text> },
                {
                  key: 'patient',
                  header: 'Patient',
                  render: (r) => (
                    <span>
                      <Text weight="semibold">{r.patientName}</Text>
                      {r.uhid && <Text size={200}>{` ${r.uhid}`}</Text>}
                    </span>
                  ),
                },
                { key: 'phone', header: 'Phone', render: (r) => r.phone ?? '—' },
                { key: 'given', header: 'Given', align: 'end', render: (r) => formatQty(r.quantity) },
                {
                  key: 'back',
                  header: 'Back',
                  align: 'end',
                  render: (r) => (r.returned ? formatQty(r.returned) : '—'),
                },
                {
                  key: 'kept',
                  header: 'Kept',
                  align: 'end',
                  render: (r) => <Text weight="semibold">{formatQty(r.kept)}</Text>,
                },
              ]}
            />
          </Section>
          <Section title="Stores still holding it">
            <DataTable<RecallStockRow>
              ariaLabel="Stores holding the batch"
              rows={t.stock}
              getRowId={(s) => s.storeName}
              size="small"
              emptyTitle="No store holds any of this batch"
              columns={[
                { key: 'store', header: 'Store', render: (s) => s.storeName },
                { key: 'onHand', header: 'On hand', align: 'end', render: (s) => formatQty(s.onHand) },
                {
                  key: 'held',
                  header: 'Held',
                  render: (s) =>
                    s.isRecalled ? (
                      <Badge appearance="tint" color="danger">
                        Held on recall
                      </Badge>
                    ) : s.isBlocked ? (
                      <Badge appearance="tint" color="warning">
                        On hold
                      </Badge>
                    ) : (
                      <Badge appearance="filled" color="danger">
                        Not held: hold it in Stock
                      </Badge>
                    ),
                },
              ]}
            />
          </Section>
        </>
      )}
    </Page>
  );
}
