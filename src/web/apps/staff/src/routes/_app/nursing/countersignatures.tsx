import {
  Badge,
  Button,
  Caption1,
  Field,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Switch,
  Text,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  CheckmarkRegular,
  DismissRegular,
  MoreHorizontalRegular,
  OpenRegular,
  SignatureRegular,
} from '@fluentui/react-icons';
import { formatDateTime, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inpatientQueries } from '@/modules/inpatient/api';
import { CountersignDialog, type Countersigning } from '@/modules/inpatient/nursing/MedicationOrders';
import {
  nursingApi,
  nursingKeys,
  nursingQueries,
  ORDER_MODE_LABEL,
  type AwaitingCountersignatureItem,
  type MedicationOrderDto,
} from '@/modules/inpatient/nursingApi';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { countersignState } from '@/modules/inpatient/verbalOrders';

type Scope = 'mine' | 'ward' | 'all';

interface CountersignSearch {
  scope?: Scope;
  wardId?: string;
  overdue?: boolean;
}

export const Route = createFileRoute('/_app/nursing/countersignatures')({
  validateSearch: (s: Record<string, unknown>): CountersignSearch => ({
    scope: s.scope === 'mine' || s.scope === 'ward' || s.scope === 'all' ? s.scope : undefined,
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    overdue: s.overdue === true || s.overdue === 'true' ? true : undefined,
  }),
  component: CountersignaturesPage,
});

/**
 * Verbal and telephone medication orders, and nurses' stops, waiting for a doctor's countersignature: the doctor's own
 * (countersigned here), a ward's for its nurses and nurse in charge, and those past the hospital's time, which are
 * flagged and also told to the doctor and the nurses in charge. Nothing is kept here silently.
 */
function CountersignaturesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const restoreFocusTarget = useRestoreFocusTarget();
  const myPractitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const isPrescriber = usePermission('clinical.prescriptions.sign') && myPractitionerId !== null;
  const canOpenChart = usePermission(['nursing.mar', 'nursing.chart']);
  const scope: Scope = search.scope ?? (isPrescriber ? 'mine' : 'all');
  const wards = useQuery(inpatientQueries.wards());
  const items = useQuery(
    nursingQueries.awaiting({
      mine: scope === 'mine',
      wardId: scope === 'ward' ? (search.wardId ?? null) : null,
      overdueOnly: search.overdue === true,
    }),
  );
  const [answering, setAnswering] = useState<Countersigning | null>(null);
  const [declining, setDeclining] = useState<MedicationOrderDto | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: nursingKeys.all });
  const decline = useMutation({
    mutationFn: ({ order, reason }: { order: MedicationOrderDto; reason: string }) =>
      nursingApi.declineStop(order.id, reason, order.version),
    onSuccess: (order) => {
      notify.success(`Stop of ${order.drugName} declined`, 'The medicine goes on, and its doses are given again.');
      setDeclining(null);
      refresh();
    },
  });

  const rows = items.data ?? [];
  const overdue = rows.filter((r) => r.order.countersignOverdue || r.order.stopCountersignOverdue).length;

  return (
    <Page>
      <PageHeader
        title="Orders to countersign"
        subtitle="Verbal and telephone medication orders, and nurses' stops, waiting for the doctor's countersignature. Past the hospital's time, they are flagged and the doctor and the ward's nurse in charge are told."
      />
      <Section>
        <FilterBar>
          <Field label="Show">
            <Select
              value={scope}
              onChange={(_, d) => void navigate({ search: (s) => ({ ...s, scope: d.value as Scope }) })}
            >
              {isPrescriber && <option value="mine">Mine to countersign</option>}
              <option value="ward">One ward</option>
              <option value="all">Every ward</option>
            </Select>
          </Field>
          {scope === 'ward' && (
            <Field label="Ward">
              <Select
                value={search.wardId ?? ''}
                onChange={(_, d) => void navigate({ search: (s) => ({ ...s, wardId: d.value || undefined }) })}
              >
                <option value="">Choose a ward</option>
                {(wards.data ?? []).map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.name}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          <Switch
            checked={search.overdue === true}
            onChange={(_, d) => void navigate({ search: (s) => ({ ...s, overdue: d.checked || undefined }) })}
            label="Past their time only"
          />
        </FilterBar>
        {items.data && (
          <Caption1 block style={{ marginBottom: 8 }}>
            {rows.length === 0
              ? 'Nothing waits.'
              : `${rows.length} waiting${overdue ? `, ${overdue} past their time` : ''}`}
          </Caption1>
        )}
        <DataTable
          ariaLabel="Orders to countersign"
          rows={items.data}
          loading={items.isLoading}
          error={items.error}
          onRetry={() => void items.refetch()}
          getRowId={(r) => r.order.id}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (r: AwaitingCountersignatureItem) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 block>
                    {r.uhid}
                    {r.wardName ? ` · ${r.wardName}` : ''}
                    {r.bedLabel ? `, bed ${r.bedLabel}` : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'medicine',
              header: 'Medicine',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.order.drugName}</Text>
                  {(r.order.isControlledDrug || r.order.isHighAlert) && (
                    <Badge size="small" color="severe" appearance="tint" style={{ marginLeft: 6 }}>
                      {r.order.isControlledDrug ? 'Controlled' : 'High alert'}
                    </Badge>
                  )}
                  <Caption1 block>
                    {r.order.dose}
                    {r.order.route ? ` · ${r.order.route}` : ''} · {r.order.frequency}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'what',
              header: 'Waiting',
              render: (r) => {
                const waiting = countersignState(r.order);
                return (
                  <span>
                    {waiting && (
                      <Badge size="small" color={waiting.overdue ? 'danger' : 'warning'} appearance="tint">
                        {waiting.label}
                      </Badge>
                    )}
                    {r.order.isEmergency && r.order.awaitingCountersignature && (
                      <Badge size="small" color="danger" appearance="outline" style={{ marginLeft: 6 }}>
                        Emergency
                      </Badge>
                    )}
                    <Caption1 block>
                      {r.order.stopPending
                        ? `Stop asked for by ${r.order.stopRequestedByName ?? 'a nurse'}: ${r.order.stopRequestReason ?? ''}`
                        : `${ORDER_MODE_LABEL[r.order.mode]} written by ${r.order.enteredByName ?? 'a nurse'}`}
                    </Caption1>
                  </span>
                );
              },
            },
            {
              key: 'doctor',
              header: 'Doctor',
              priority: 'low',
              render: (r) => r.order.orderedByName ?? 'The patient’s consultant',
            },
            {
              key: 'due',
              header: 'Countersign by',
              width: 170,
              render: (r) => {
                const due = r.order.stopPending ? null : r.order.countersignDueAt;
                return due
                  ? formatDateTime(due)
                  : r.order.stopRequestedAt
                    ? `Asked ${formatDateTime(r.order.stopRequestedAt)}`
                    : '—';
              },
            },
          ]}
          rowActions={(r) => {
            const o = r.order;
            const countersign = o.canCountersign && o.awaitingCountersignature;
            const answerStop = o.canCountersign && o.stopPending;
            if (!countersign && !answerStop && !canOpenChart) {
              return null;
            }
            return (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<MoreHorizontalRegular />}
                    aria-label={`Actions for ${o.drugName}, ${r.patientName}`}
                    {...restoreFocusTarget}
                  />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {countersign && (
                      <MenuItem icon={<SignatureRegular />} onClick={() => setAnswering({ order: o, what: 'order' })}>
                        Countersign
                      </MenuItem>
                    )}
                    {answerStop && (
                      <MenuItem icon={<CheckmarkRegular />} onClick={() => setAnswering({ order: o, what: 'stop' })}>
                        Confirm stop
                      </MenuItem>
                    )}
                    {answerStop && (
                      <MenuItem icon={<DismissRegular />} onClick={() => setDeclining(o)}>
                        Decline stop
                      </MenuItem>
                    )}
                    {canOpenChart && (
                      <MenuItem
                        icon={<OpenRegular />}
                        onClick={() =>
                          void navigate({
                            to: '/nursing',
                            search: { wardId: r.wardId ?? undefined, admissionId: r.admissionId, tab: 'medicines' },
                          })
                        }
                      >
                        Open the chart
                      </MenuItem>
                    )}
                  </MenuList>
                </MenuPopover>
              </Menu>
            );
          }}
          emptyTitle={search.overdue ? 'Nothing past its time' : 'Nothing to countersign'}
          emptyDescription="A verbal or telephone order, or a nurse's stop of a medicine, appears here until the doctor countersigns it."
        />
      </Section>
      {answering && (
        <CountersignDialog countersigning={answering} onClose={() => setAnswering(null)} onDone={refresh} />
      )}
      <ReasonDialog
        open={declining !== null}
        title={declining ? `Decline the stop of ${declining.drugName}?` : 'Decline the stop?'}
        description="Declined, the medicine goes on and its doses are given again; the nurse who asked is told why."
        placeholder="e.g. Still needed: continue for two more days"
        confirmLabel="Decline stop"
        busy={decline.isPending}
        error={decline.error}
        onConfirm={(reason) => declining && decline.mutate({ order: declining, reason })}
        onCancel={() => {
          setDeclining(null);
          decline.reset();
        }}
      />
    </Page>
  );
}
