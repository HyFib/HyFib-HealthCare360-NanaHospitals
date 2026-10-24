import { Badge, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { formatDateOnly } from '@hc360/core';
import { DataTable, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { emrQueries } from './api';

const useStyles = makeStyles({
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'baseline', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The medicines the patient should be taking now (EHR-03): every line of a signed prescription of the last year whose
 * course has not ended, from any of the hospital's facilities, with who prescribed it. Opening it is an audited read.
 */
export function CurrentMedications({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const medicines = useQuery(emrQueries.currentMedications(patientId));
  const count = medicines.data?.length;

  return (
    <Section
      flush
      title={count === undefined ? 'Current medications' : `Current medications (${count})`}
      description="Lines of signed prescriptions whose course has not ended."
    >
      <DataTable
        ariaLabel="Current medications"
        rows={medicines.data}
        loading={medicines.isLoading}
        error={medicines.error}
        onRetry={() => void medicines.refetch()}
        getRowId={(m) => m.itemId}
        size="small"
        columns={[
          {
            key: 'medicine',
            header: 'Medicine',
            render: (m) => (
              <span className={styles.cell}>
                <span className={styles.row}>
                  <Text weight="semibold">
                    {m.drugName}
                    {m.strength ? ` ${m.strength}` : ''}
                  </Text>
                  {m.isControlledDrug && (
                    <Badge size="small" color="danger" appearance="outline">
                      H1/X
                    </Badge>
                  )}
                </span>
                {m.genericName && <Caption1 className={styles.muted}>{m.genericName}</Caption1>}
              </span>
            ),
          },
          {
            key: 'dose',
            header: 'Dose',
            render: (m) => (
              <span className={styles.cell}>
                <Text>{m.dose}</Text>
                <Caption1 className={styles.muted}>{m.frequency}</Caption1>
              </span>
            ),
          },
          {
            key: 'course',
            header: 'Course',
            render: (m) => (
              <span className={styles.cell}>
                <Text>Until {formatDateOnly(m.endsOn)}</Text>
                <Caption1 className={styles.muted}>
                  {m.durationDays} {m.durationDays === 1 ? 'day' : 'days'} from {formatDateOnly(m.startedOn)}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'prescriber',
            header: 'Prescribed by',
            priority: 'low',
            render: (m) => (
              <span className={styles.cell}>
                <Text>{m.prescriberName ?? 'Doctor'}</Text>
                <Caption1 className={styles.muted}>{m.prescriptionNumber}</Caption1>
              </span>
            ),
          },
        ]}
        emptyTitle="No current medications"
        emptyDescription="No signed prescription of this patient has a course still running."
      />
    </Section>
  );
}
