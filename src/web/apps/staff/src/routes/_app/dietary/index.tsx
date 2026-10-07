import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  CheckmarkRegular,
  EditRegular,
  FoodRegular,
  MoreHorizontalRegular,
  StopRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  DIET_SOURCE_LABEL,
  dietaryApi,
  dietaryKeys,
  dietaryQueries,
  type DietOrderDto,
  type DietOrderStatus,
} from '@/modules/dietary/api';
import { DietOrderDialog, type DietAdmission } from '@/modules/dietary/DietOrderDialog';
import { describeSlots, PREFERENCE_SHORT } from '@/modules/dietary/rules';
import { AdmissionPicker, type PickedAdmission } from '@/modules/inpatient/AdmissionPicker';
import { inpatientQueries } from '@/modules/inpatient/api';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';

const STATUSES: DietOrderStatus[] = ['Active', 'Stopped', 'Completed'];

interface DietSearch {
  wardId?: string;
  status?: DietOrderStatus;
  review?: boolean;
}

export const Route = createFileRoute('/_app/dietary/')({
  validateSearch: (s: Record<string, unknown>): DietSearch => ({
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    status: STATUSES.includes(s.status as DietOrderStatus) ? (s.status as DietOrderStatus) : undefined,
    review: s.review === true ? true : undefined,
  }),
  component: DietOrdersPage,
});

type Editing = { admission: DietAdmission; current: DietOrderDto | null } | null;

/** Diet orders (DIET-01): the dietitian's round list, active by default, with orders needing review flagged. */
function DietOrdersPage() {
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('dietary.orders.manage');
  const canPickAdmission = usePermission('inpatient.admissions.read');
  const wards = useQuery({ ...inpatientQueries.wards(), enabled: canPickAdmission });
  const orders = useQuery(
    dietaryQueries.orders({ status: search.status, wardId: search.wardId, needsReview: search.review }),
  );
  const [picking, setPicking] = useState(false);
  const [picked, setPicked] = useState<PickedAdmission | null>(null);
  const [editing, setEditing] = useState<Editing>(null);
  const [stopping, setStopping] = useState<DietOrderDto | null>(null);
  const set = (patch: Partial<DietSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  const refresh = () => void queryClient.invalidateQueries({ queryKey: dietaryKeys.all });
  const review = useMutation({
    mutationFn: (order: DietOrderDto) => dietaryApi.reviewOrder(order.id),
    onSuccess: (order) => {
      notify.success(`Diet for ${order.patientName ?? 'the patient'} reviewed`);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not mark the diet as reviewed'),
  });
  const stop = useMutation({
    mutationFn: ({ order, reason }: { order: DietOrderDto; reason: string }) => dietaryApi.stopOrder(order.id, reason),
    onSuccess: () => {
      notify.success('Diet stopped', 'Meals not yet served were cancelled.');
      setStopping(null);
      refresh();
    },
  });

  const admissionOf = (o: DietOrderDto): DietAdmission => ({
    admissionId: o.admissionId,
    patientId: o.patientId,
    patientName: o.patientName ?? 'Patient',
    place: [o.wardName, o.bedLabel ? `bed ${o.bedLabel}` : null].filter(Boolean).join(', '),
  });
  const reviewCount = (orders.data ?? []).filter((o) => o.needsReview).length;

  return (
    <Page>
      <PageHeader
        title="Diet orders"
        icon={<FoodRegular />}
        subtitle={
          search.review
            ? 'Orders created from clinical orders that need a dietitian’s review'
            : 'Diets of admitted patients, ward by ward'
        }
        actions={
          canManage &&
          canPickAdmission && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setPicking(true)}>
              New diet order
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            {canPickAdmission && (
              <Field label="Ward">
                <Select value={search.wardId ?? ''} onChange={(_, d) => set({ wardId: d.value || undefined })}>
                  <option value="">All wards</option>
                  {wards.data?.map((w) => (
                    <option key={w.id} value={w.id}>
                      {w.name}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <Field label="Status">
              <Select
                value={search.status ?? 'Active'}
                onChange={(_, d) => set({ status: d.value === 'Active' ? undefined : (d.value as DietOrderStatus) })}
              >
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(search.review)}
              onChange={(_, d) => set({ review: d.checked || undefined })}
              label={`Needs review${!search.review && reviewCount ? ` (${reviewCount})` : ''}`}
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Diet orders"
          rows={orders.data}
          loading={orders.isLoading}
          error={orders.error}
          onRetry={() => void orders.refetch()}
          getRowId={(o) => o.id}
          onRowClick={
            canPickAdmission
              ? (o) =>
                  void navigate({
                    to: '/inpatient/admissions/$admissionId',
                    params: { admissionId: o.admissionId },
                    search: { tab: 'diet' },
                  })
              : undefined
          }
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (o) => (
                <span>
                  <Text weight="semibold">{o.patientName ?? '—'}</Text>
                  <Caption1 block>{[o.wardName, o.bedLabel].filter(Boolean).join(' · ') || o.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'diet',
              header: 'Diet',
              render: (o) => (
                <span>
                  <Text weight="semibold">{o.dietName ?? o.dietCode}</Text>{' '}
                  <Badge size="small" appearance="outline">
                    {PREFERENCE_SHORT[o.preference]}
                  </Badge>
                  {o.needsReview && (
                    <Badge size="small" appearance="tint" color="warning" style={{ marginLeft: 4 }}>
                      Review
                    </Badge>
                  )}
                  <Caption1 block>{describeSlots(o.meals)}</Caption1>
                </span>
              ),
            },
            {
              key: 'notes',
              header: 'Restrictions and allergies',
              priority: 'low',
              render: (o) => (
                <span>
                  {o.restrictions.length ? o.restrictions.join(', ') : '—'}
                  {o.allergyNotes && (
                    <Caption1 block style={{ color: tokens.colorPaletteRedForeground1, fontWeight: 600 }}>
                      <WarningRegular aria-hidden /> {o.allergyNotes}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'from', header: 'Since', width: 120, priority: 'low', render: (o) => formatDateOnly(o.startDate) },
            {
              key: 'by',
              header: 'Ordered by',
              priority: 'low',
              render: (o) => `${o.orderedByName ?? '—'} (${DIET_SOURCE_LABEL[o.source]})`,
            },
          ]}
          rowActions={
            canManage
              ? (o) =>
                  o.status === 'Active' ? (
                    <Menu>
                      <MenuTrigger disableButtonEnhancement>
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<MoreHorizontalRegular />}
                          aria-label={`Actions for ${o.patientName ?? 'patient'}`}
                          {...restoreFocusTarget}
                        />
                      </MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          {o.needsReview && (
                            <MenuItem icon={<CheckmarkRegular />} onClick={() => review.mutate(o)}>
                              Mark reviewed
                            </MenuItem>
                          )}
                          <MenuItem
                            icon={<EditRegular />}
                            onClick={() => setEditing({ admission: admissionOf(o), current: o })}
                          >
                            Change diet
                          </MenuItem>
                          <MenuItem icon={<StopRegular />} onClick={() => setStopping(o)}>
                            Stop diet
                          </MenuItem>
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  ) : null
              : undefined
          }
          emptyTitle={search.review ? 'No diet orders need review' : 'No diet orders'}
          emptyDescription="Doctors, dietitians and nurses order diets from here or from the admission."
          size="small"
        />
      </Section>

      <Dialog open={picking} onOpenChange={(_, d) => !d.open && setPicking(false)}>
        <DialogSurface style={{ maxWidth: 520 }}>
          <DialogBody>
            <DialogTitle>New diet order</DialogTitle>
            <DialogContent>
              <AdmissionPicker value={picked} onChange={setPicked} wardId={search.wardId} required />
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setPicking(false)}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={!picked}
                onClick={() => {
                  if (picked) {
                    const current =
                      (orders.data ?? []).find((o) => o.admissionId === picked.admissionId && o.status === 'Active') ??
                      null;
                    setEditing({
                      admission: {
                        admissionId: picked.admissionId,
                        patientId: picked.patientId,
                        patientName: picked.patientName,
                        place: [picked.wardName, picked.bedLabel ? `bed ${picked.bedLabel}` : null]
                          .filter(Boolean)
                          .join(', '),
                      },
                      current,
                    });
                    setPicking(false);
                    setPicked(null);
                  }
                }}
              >
                Continue
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {editing && (
        <DietOrderDialog admission={editing.admission} current={editing.current} onClose={() => setEditing(null)} />
      )}
      <ReasonDialog
        open={stopping !== null}
        title={stopping ? `Stop the diet of ${stopping.patientName ?? 'the patient'}?` : 'Stop diet?'}
        description="Meals not yet served are cancelled."
        confirmLabel="Stop diet"
        destructive
        busy={stop.isPending}
        error={stop.error}
        onConfirm={(reason) => stopping && stop.mutate({ order: stopping, reason })}
        onCancel={() => {
          setStopping(null);
          stop.reset();
        }}
      />
    </Page>
  );
}
