import { Button, Caption1, Field, makeStyles, SearchBox, Text, tokens } from '@fluentui/react-components';
import { BarcodeScannerRegular, DeleteRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type KeyboardEvent } from 'react';
import { ActionError, Detail, DetailGrid, ReasonDialog } from '@/modules/ot/shared/feedback';
import { bloodApi, bloodKeys, bloodQueries, type BloodUnitDto } from './api';
import { BloodGroupBadge, ExpiryText, TtiBadge, UnitStatusBadge } from './BloodBadges';
import { componentLabel } from './labels';
import { printUnitLabels } from './print';
import { canDiscardUnit } from './rules';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  head: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  number: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  lookup: { display: 'flex', flexDirection: 'column', gap: '10px' },
});

export function UnitCard({
  unit,
  now,
  canManage,
  onDiscard,
}: {
  unit: BloodUnitDto;
  now: number;
  canManage: boolean;
  onDiscard?: (unit: BloodUnitDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  return (
    <div className={styles.card} aria-label={`Blood unit ${unit.unitNumber}`}>
      <div className={styles.head}>
        <BloodGroupBadge group={unit.bloodGroup} />
        <span className={styles.number}>{unit.unitNumber}</span>
        <UnitStatusBadge status={unit.status} />
        <TtiBadge tti={unit.tti} />
      </div>
      <DetailGrid minColumnWidth={180}>
        <Detail label="Component">{`${componentLabel[unit.componentType]} · ${unit.volumeMl} ml`}</Detail>
        <Detail label="Expiry">
          <ExpiryText expiresAt={unit.expiresAt} now={now} />
        </Detail>
        <Detail label="Collected">{formatDateTime(unit.collectedAt)}</Detail>
        <Detail label="Stored at">{unit.storageLocation}</Detail>
        {unit.issuedAt && <Detail label="Issued">{formatDateTime(unit.issuedAt)}</Detail>}
        {unit.discardReason && <Detail label="Discarded because">{unit.discardReason}</Detail>}
      </DetailGrid>
      <div className={styles.actions}>
        <Button
          icon={<PrintRegular />}
          onClick={() => void printUnitLabels([unit]).catch((error: unknown) => notify.error(error, 'Printing failed'))}
        >
          Print label
        </Button>
        {unit.reservedForRequestId && (
          <Button
            onClick={() =>
              unit.reservedForRequestId &&
              void navigate({ to: '/blood-bank/requests/$id', params: { id: unit.reservedForRequestId } })
            }
          >
            {unit.status === 'Issued' ? 'Open the request' : 'Open the reserving request'}
          </Button>
        )}
        {canManage && canDiscardUnit(unit.status) && onDiscard && (
          <Button icon={<DeleteRegular />} onClick={() => onDiscard(unit)}>
            Discard unit
          </Button>
        )}
      </div>
    </div>
  );
}

/** Discards an in-stock unit (broken bag, haemolysis, clots, failed QC); a reservation is released. */
export function DiscardUnitDialog({ unit, onClose }: { unit: BloodUnitDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const discard = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => bloodApi.discardUnit(id, reason),
    onSuccess: () => {
      notify.success('Unit discarded');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onClose();
    },
  });
  return (
    <ReasonDialog
      open={unit !== null}
      title={`Discard unit ${unit?.unitNumber ?? ''}?`}
      description={
        unit?.status === 'Reserved'
          ? 'This unit is reserved for a patient; the reservation is released and the request needs another unit.'
          : 'A discarded unit can never be issued. The reason is kept on the unit record.'
      }
      confirmLabel="Discard"
      destructive
      busy={discard.isPending}
      error={discard.error}
      presets={['Bag leak or damage', 'Haemolysed', 'Clots seen', 'Lipaemic', 'Failed quality check', 'Expired']}
      onConfirm={(reason) => unit && discard.mutate({ id: unit.id, reason })}
      onCancel={() => {
        discard.reset();
        onClose();
      }}
    />
  );
}

/** Scan or type a unit number (the barcode on the label) to see the unit and act on it. */
export function UnitLookup({ now, canManage }: { now: number; canManage: boolean }) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const [number, setNumber] = useState('');
  const [discarding, setDiscarding] = useState<BloodUnitDto | null>(null);
  const unit = useQuery(bloodQueries.unitByNumber(number));
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      setNumber(term.trim().toUpperCase());
    }
  };
  const notFound = isApiError(unit.error) && unit.error.status === 404;

  return (
    <div className={styles.lookup}>
      <Field label="Scan or type a unit number" hint="Press Enter after typing. Barcode scanners press Enter for you.">
        <SearchBox
          value={term}
          onChange={(_, d) => setTerm(d.value)}
          onKeyDown={onKeyDown}
          placeholder="e.g. NHBB26000123-R"
          contentBefore={<BarcodeScannerRegular />}
          aria-label="Unit number"
        />
      </Field>
      {unit.isFetching && <Caption1>Looking up {number}…</Caption1>}
      {notFound && <Text>No unit with the number {number}. Check the label and scan again.</Text>}
      {!notFound && unit.error ? <ActionError error={unit.error} /> : null}
      {unit.data && <UnitCard unit={unit.data} now={now} canManage={canManage} onDiscard={setDiscarding} />}
      <DiscardUnitDialog unit={discarding} onClose={() => setDiscarding(null)} />
    </div>
  );
}
