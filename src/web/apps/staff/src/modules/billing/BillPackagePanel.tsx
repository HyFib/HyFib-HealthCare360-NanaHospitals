import { Button, makeStyles, Select, Text, tokens, useRestoreFocusTarget } from '@fluentui/react-components';
import { BoxRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { ConfirmDialog, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingApi, type BillDetail } from './api';
import { mastersQueries, packagesForOpBill } from './mastersApi';
import { ActionError, Amount, Detail, DetailGrid } from './ui';

const useStyles = makeStyles({
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The package a bill is priced at (MB-02, OPB): on an OP draft, choose, change or remove it; on any bill priced at a
 * package (an OP bill, or an IP final bill whose package is the IP account's), see what it absorbs and what is billed
 * beyond it. Renders nothing for a bill that has no package and cannot take one.
 */
export function BillPackagePanel({ bill, onChanged }: { bill: BillDetail; onChanged: (detail: BillDetail) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const restoreFocusTarget = useRestoreFocusTarget();
  const canEdit = usePermission('billing.op.create');
  const editable = canEdit && bill.status === 'Draft' && bill.type === 'OutPatient';
  const packages = useQuery({ ...mastersQueries.packages(), enabled: editable });
  // undefined: no dialog; null: removing the package; a string: the package chosen.
  const [choice, setChoice] = useState<string | null | undefined>(undefined);

  const eligible = editable ? packagesForOpBill(packages.data ?? [], bill) : [];
  const chosen = eligible.find((p) => p.id === choice);
  const current = bill.package;

  const apply = useMutation({
    mutationFn: (packageId: string | null) => billingApi.setPackage(bill.id, packageId, bill.version),
    onSuccess: (detail, packageId) => {
      onChanged(detail);
      setChoice(undefined);
      notify.success(
        packageId ? 'Package applied' : 'Package removed',
        `The patient's share is now ${formatInr(detail.patientAmount)}.`,
      );
    },
  });

  if (!current && eligible.length === 0) {
    return null;
  }

  const picker = editable && eligible.length > 0 && (
    <Select
      value=""
      onChange={(_, d) => d.value && setChoice(d.value)}
      aria-label={current ? 'Change the package' : 'Bill at a package'}
      {...restoreFocusTarget}
    >
      <option value="">{current ? 'Change package…' : 'Bill at a package…'}</option>
      {eligible.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name} · {formatInr(p.price)}
        </option>
      ))}
    </Select>
  );

  return (
    <>
      <Section
        title={current ? `Package: ${current.name}` : 'Package'}
        description={
          current
            ? 'The package absorbs the services it includes, up to its limits; the rest is billed as usual.'
            : 'Bill this visit at a fixed-price package, for example a day-care procedure.'
        }
        actions={
          editable && (
            <div className={styles.row}>
              {picker}
              {current && (
                <Button icon={<BoxRegular />} onClick={() => setChoice(null)} {...restoreFocusTarget}>
                  Remove package
                </Button>
              )}
            </div>
          )
        }
      >
        {current ? (
          <DetailGrid>
            <Detail label="Package price">
              <Amount value={current.price} size="large" />
            </Detail>
            <Detail label="Services at full price">
              <Amount value={current.actuals} />
            </Detail>
            <Detail label="Covered by the package">
              <Amount value={current.covered} />
            </Detail>
            <Detail label="Billed beyond the package">
              <Amount value={current.beyondPackage} tone={current.beyondPackage > 0 ? 'negative' : undefined} />
            </Detail>
          </DetailGrid>
        ) : (
          <Text className={styles.muted}>
            The package is one line at its price; the services it includes are not charged again.
          </Text>
        )}
      </Section>
      <ConfirmDialog
        open={choice !== undefined}
        title={choice ? `Bill at ${chosen?.name ?? 'this package'}?` : 'Remove the package?'}
        confirmLabel={choice ? 'Apply package' : 'Remove package'}
        destructive={choice === null}
        busy={apply.isPending}
        onConfirm={() => apply.mutate(choice ?? null)}
        onCancel={() => {
          setChoice(undefined);
          apply.reset();
        }}
      >
        <Text block>
          {choice
            ? `${formatInr(chosen?.price ?? 0)} is billed for the package, and the services it includes are not charged again. Services beyond it are billed as usual.`
            : 'Every service on the bill is charged in full again.'}
        </Text>
        <ActionError error={apply.error} />
      </ConfirmDialog>
    </>
  );
}
