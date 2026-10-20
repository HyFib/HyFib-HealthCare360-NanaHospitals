import {
  Button,
  Caption1,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  DeleteRegular,
  EditRegular,
  MoreHorizontalRegular,
  TextBulletListSquareRegular,
} from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { flagParam } from '@/modules/billing/searchParams';
import { ActionError, NoAccess } from '@/modules/billing/ui';
import { feeApi, feeKeys, feeQueries, type ShareRuleDto } from '@/modules/doctor-fees/api';
import { ruleScope, ruleShare } from '@/modules/doctor-fees/logic';
import { PractitionerPicker, type PickedDoctor } from '@/modules/doctor-fees/PractitionerPicker';
import { RuleDrawer } from '@/modules/doctor-fees/RuleDrawer';

export const Route = createFileRoute('/_app/doctor-fees/rules')({
  validateSearch: (s: Record<string, unknown>): { inactive?: true } => ({ inactive: flagParam(s.inactive) }),
  component: RulesPage,
});

/** Doctor revenue-share rules (DFA-01). The most specific rule in force on the service date decides the share. */
function RulesPage() {
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission(['doctorfees.rules.manage', 'doctorfees.payouts.manage']);
  const canManage = usePermission('doctorfees.rules.manage');
  const [doctor, setDoctor] = useState<PickedDoctor | null>(null);
  const [editing, setEditing] = useState<ShareRuleDto | 'new' | null>(null);
  const [deleting, setDeleting] = useState<ShareRuleDto | null>(null);
  const rules = useQuery({ ...feeQueries.rules(doctor?.id, Boolean(search.inactive)), enabled: canRead });
  const remove = useMutation({
    mutationFn: (id: string) => feeApi.deleteRule(id),
    onSuccess: () => {
      notify.success('Rule deleted');
      setDeleting(null);
      void queryClient.invalidateQueries({ queryKey: feeKeys.rules });
    },
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Sharing rules" />
        <NoAccess what="doctor sharing rules" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Sharing rules"
        subtitle="What share of each bill line a doctor earns"
        icon={<TextBulletListSquareRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add rule
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <div style={{ flex: '1 1 280px', maxWidth: 420 }}>
              <PractitionerPicker
                value={doctor}
                onChange={setDoctor}
                hint="Shows the doctor's own rules and the hospital-wide defaults"
              />
            </div>
            <Switch
              checked={Boolean(search.inactive)}
              onChange={(_, d) => void navigate({ search: { inactive: d.checked ? true : undefined } })}
              label="Show inactive"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Sharing rules"
          rows={rules.data}
          loading={rules.isLoading}
          error={rules.error}
          onRetry={() => void rules.refetch()}
          getRowId={(r) => r.id}
          onRowClick={canManage ? (r) => setEditing(r) : undefined}
          columns={[
            {
              key: 'doctor',
              header: 'Doctor',
              render: (r) =>
                r.practitionerId ? (
                  <Text weight="semibold">{r.practitionerName}</Text>
                ) : (
                  <Text style={{ color: tokens.colorNeutralForeground3 }}>All doctors (default)</Text>
                ),
            },
            { key: 'scope', header: 'Applies to', render: (r) => ruleScope(r) },
            {
              key: 'share',
              header: 'Share',
              align: 'end',
              render: (r) => <Text weight="semibold">{ruleShare(r.method, r.value)}</Text>,
            },
            {
              key: 'valid',
              header: 'In force',
              priority: 'low',
              render: (r) => (
                <span>
                  {formatDateOnly(r.effectiveFrom)} – {r.effectiveTo ? formatDateOnly(r.effectiveTo) : 'open'}
                  {r.notes && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {r.notes}
                    </Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (r) => (
                <StatusBadge
                  status={r.isActive ? 'Active' : 'Inactive'}
                  tone={r.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          rowActions={
            canManage
              ? (r) => (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<MoreHorizontalRegular />}
                        aria-label="Rule actions"
                        {...restoreFocusTarget}
                      />
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItem icon={<EditRegular />} onClick={() => setEditing(r)}>
                          Edit
                        </MenuItem>
                        <MenuItem icon={<DeleteRegular />} onClick={() => setDeleting(r)}>
                          Delete
                        </MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                )
              : undefined
          }
          emptyTitle="No sharing rules"
          emptyDescription="Add a hospital-wide default first (for example consultations 80 %), then doctor-specific exceptions."
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
                Add rule
              </Button>
            ) : undefined
          }
          size="small"
        />
      </Section>
      {editing && <RuleDrawer rule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={deleting !== null}
        title="Delete this sharing rule?"
        confirmLabel="Delete rule"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        Fees already earned are not changed. To stop a rule from a date, set its end date instead; that keeps the
        history.
        <ActionError error={remove.error} />
      </ConfirmDialog>
    </Page>
  );
}
