import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Spinner,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  CheckmarkCircleRegular,
  EditRegular,
  MoreHorizontalRegular,
  SearchRegular,
  ShieldCheckmarkRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { formatPercent } from '@/modules/billing/money';
import { flagParam, idParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { insuranceApi, insuranceQueries, type PolicyDto } from '@/modules/insurance/api';
import { PolicyDrawer, VerifyPolicyDialog } from '@/modules/insurance/PolicyDrawer';
import { PolicyNumberReveal } from '@/modules/insurance/PolicyNumberReveal';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker } from '@/modules/patients/PatientPicker';

interface PoliciesSearch {
  patientId?: string;
  inactive?: true;
}

export const Route = createFileRoute('/_app/insurance/policies')({
  validateSearch: (s: Record<string, unknown>): PoliciesSearch => ({
    patientId: idParam(s.patientId),
    inactive: flagParam(s.inactive),
  }),
  component: PoliciesPage,
});

/** Patient insurance policies (INS-03): record, verify with the insurer, and reveal the number only when needed (audited). */
function PoliciesPage() {
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const canRead = usePermission([
    'insurance.policies.manage',
    'insurance.preauth.manage',
    'insurance.claims.manage',
    'billing.ip.manage',
    'billing.op.create',
  ]);
  const canManage = usePermission('insurance.policies.manage');
  const canSearchNumber = usePermission(['insurance.policies.manage', 'insurance.claims.manage']);
  const [editing, setEditing] = useState<PolicyDto | 'new' | null>(null);
  const [verifying, setVerifying] = useState<PolicyDto | null>(null);
  const [number, setNumber] = useState('');
  const patient = useQuery({ ...patientQueries.detail(search.patientId ?? ''), enabled: Boolean(search.patientId) });
  const policies = useQuery({
    ...insuranceQueries.policies(search.patientId ?? '', Boolean(search.inactive)),
    enabled: canRead && Boolean(search.patientId),
  });

  const find = useMutation({
    mutationFn: () => insuranceApi.searchPolicies(number.trim()),
    onSuccess: (found) => {
      const first = found[0];
      if (!first) {
        notify.warning('No policy with that number', 'Check the number exactly as printed on the card.');
      } else {
        void navigate({ search: { patientId: first.patientId, inactive: first.isActive ? undefined : true } });
        if (found.length > 1) {
          notify.info('Found on more than one record', 'Showing the first patient.');
        }
      }
    },
    onError: (error) => notify.error(error, 'Search failed'),
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Patient policies" />
        <NoAccess what="patient policies" />
      </Page>
    );
  }

  const picked = patient.data
    ? { id: patient.data.id, uhid: patient.data.uhid, fullName: patient.data.fullName }
    : null;

  return (
    <Page>
      <PageHeader
        title="Patient policies"
        subtitle="Insurance and scheme cover of a patient; policy numbers are stored encrypted"
        icon={<ShieldCheckmarkRegular />}
        actions={
          canManage &&
          search.patientId && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Record a policy
            </Button>
          )
        }
      />
      <Section>
        <FilterBar>
          <div style={{ flex: '1 1 320px', maxWidth: 520 }}>
            <PatientPicker
              value={picked}
              onChange={(p) => void navigate({ search: (prev) => ({ ...prev, patientId: p?.id }) })}
              hint="UHID, mobile number or name"
            />
          </div>
          {canSearchNumber && (
            <form
              onSubmit={(event) => {
                event.preventDefault();
                if (number.trim() && !find.isPending) {
                  find.mutate();
                }
              }}
            >
              <Field label="Or the exact policy / card number">
                <Input
                  value={number}
                  onChange={(_, d) => setNumber(d.value)}
                  maxLength={64}
                  autoComplete="off"
                  contentAfter={find.isPending ? <Spinner size="extra-tiny" /> : <SearchRegular />}
                  aria-label="Policy number"
                />
              </Field>
            </form>
          )}
        </FilterBar>
      </Section>

      {!search.patientId ? (
        <EmptyState
          icon={<ShieldCheckmarkRegular />}
          title="Find the patient"
          description="Search by UHID, mobile or name, or type the policy number from the insurance card."
        />
      ) : (
        <>
          <BillingPatientBanner patientId={search.patientId} showDues={false} />
          <Section
            title="Policies"
            actions={
              <Switch
                checked={Boolean(search.inactive)}
                onChange={(_, d) =>
                  void navigate({ search: (prev) => ({ ...prev, inactive: d.checked ? true : undefined }) })
                }
                label="Show inactive"
              />
            }
            flush
          >
            <DataTable
              ariaLabel="Policies"
              rows={policies.data}
              loading={policies.isLoading}
              error={policies.error}
              onRetry={() => void policies.refetch()}
              getRowId={(p) => p.id}
              columns={[
                {
                  key: 'payer',
                  header: 'Insurer',
                  render: (p) => (
                    <span>
                      <Text weight="semibold">{p.payerName ?? '—'}</Text>
                      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                        {p.tpaName ? `TPA ${p.tpaName}` : 'No TPA'}
                        {p.memberId ? ` · member ${p.memberId}` : ''}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'number',
                  header: 'Policy number',
                  render: (p) => <PolicyNumberReveal policyId={p.id} masked={p.policyNumberMasked} />,
                },
                {
                  key: 'valid',
                  header: 'Valid',
                  render: (p) => (
                    <span>
                      {formatDateOnly(p.validFrom)} – {formatDateOnly(p.validTo)}{' '}
                      {!p.isValidToday && (
                        <Badge size="small" appearance="tint" color="danger">
                          Not valid today
                        </Badge>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'sum',
                  header: 'Sum insured',
                  align: 'end',
                  priority: 'low',
                  render: (p) => <Amount value={p.sumInsured} />,
                },
                {
                  key: 'copay',
                  header: 'Co-pay',
                  align: 'end',
                  priority: 'low',
                  render: (p) => formatPercent(p.coPayPercent),
                },
                {
                  key: 'verified',
                  header: 'Verified',
                  priority: 'low',
                  render: (p) =>
                    p.verifiedAt ? (
                      <span title={p.verificationNotes ?? undefined}>
                        <CheckmarkCircleRegular color={tokens.colorPaletteGreenForeground1} aria-hidden />{' '}
                        {formatDateTime(p.verifiedAt)}
                      </span>
                    ) : (
                      <Text style={{ color: tokens.colorPaletteMarigoldForeground1 }}>Not yet</Text>
                    ),
                },
              ]}
              rowActions={
                canManage
                  ? (p) => (
                      <Menu>
                        <MenuTrigger disableButtonEnhancement>
                          <Button
                            size="small"
                            appearance="subtle"
                            icon={<MoreHorizontalRegular />}
                            aria-label={`Actions for policy ${p.policyNumberMasked}`}
                            {...restoreFocusTarget}
                          />
                        </MenuTrigger>
                        <MenuPopover>
                          <MenuList>
                            <MenuItem icon={<EditRegular />} onClick={() => setEditing(p)}>
                              Edit policy
                            </MenuItem>
                            <MenuItem icon={<CheckmarkCircleRegular />} onClick={() => setVerifying(p)}>
                              Mark as verified
                            </MenuItem>
                          </MenuList>
                        </MenuPopover>
                      </Menu>
                    )
                  : undefined
              }
              emptyTitle="No policy recorded"
              emptyDescription={canManage ? 'Record the policy from the patient’s insurance card.' : undefined}
              size="small"
            />
          </Section>
        </>
      )}
      {editing && search.patientId && (
        <PolicyDrawer
          patientId={search.patientId}
          policy={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
      {verifying && <VerifyPolicyDialog policy={verifying} onClose={() => setVerifying(null)} />}
    </Page>
  );
}
