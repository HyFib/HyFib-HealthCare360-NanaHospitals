import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Text,
  ToggleButton,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { FormGrid } from '@hc360/ui';
import { useId, useState } from 'react';
import type { SusceptibilityDto, SusceptibilityInterpretation } from './api';

const GROWTH_STATEMENTS = ['No growth after 48 hours of incubation', 'Growth isolated', 'Mixed growth of more than two organisms (likely contamination)'];

const COMMON_ANTIBIOTICS = [
  'Amikacin',
  'Amoxicillin-clavulanate',
  'Ampicillin',
  'Cefepime',
  'Cefoperazone-sulbactam',
  'Ceftazidime',
  'Ceftriaxone',
  'Cefuroxime',
  'Ciprofloxacin',
  'Colistin',
  'Co-trimoxazole',
  'Doxycycline',
  'Ertapenem',
  'Fosfomycin',
  'Gentamicin',
  'Imipenem',
  'Levofloxacin',
  'Linezolid',
  'Meropenem',
  'Nitrofurantoin',
  'Norfloxacin',
  'Piperacillin-tazobactam',
  'Teicoplanin',
  'Tigecycline',
  'Vancomycin',
];

const INTERPRETATIONS: { value: SusceptibilityInterpretation; code: string; label: string }[] = [
  { value: 'Sensitive', code: 'S', label: 'Sensitive' },
  { value: 'Intermediate', code: 'I', label: 'Intermediate' },
  { value: 'Resistant', code: 'R', label: 'Resistant' },
];

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '14px' },
  quick: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '6px' },
  table: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(160px, 1fr) auto 110px auto', gap: '8px', alignItems: 'center' },
  header: { color: tokens.colorNeutralForeground3 },
  sir: { display: 'flex', gap: '4px' },
});

export interface CultureValue {
  value: string;
  organism: string;
  colonyCount: string;
  susceptibilities: SusceptibilityDto[];
}

/**
 * Culture and sensitivity entry: growth statement, organism, colony count and the antibiogram (S / I / R with MIC).
 * The values go back to the result sheet and are saved with the other results (Ctrl+S).
 */
export function CultureDialog({
  testName,
  initial,
  options,
  readOnly,
  onApply,
  onClose,
}: {
  testName: string;
  initial: CultureValue;
  options: readonly string[];
  readOnly?: boolean;
  onApply: (value: CultureValue) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const listId = useId();
  const [value, setValue] = useState(initial.value);
  const [organism, setOrganism] = useState(initial.organism);
  const [colonyCount, setColonyCount] = useState(initial.colonyCount);
  const [rows, setRows] = useState<SusceptibilityDto[]>(initial.susceptibilities);
  const statements = options.length > 0 ? options : GROWTH_STATEMENTS;

  const update = (index: number, patch: Partial<SusceptibilityDto>) => setRows((current) => current.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  const addAntibiotic = (name = '') =>
    setRows((current) => (name && current.some((row) => row.antibiotic.toLowerCase() === name.toLowerCase()) ? current : [...current, { antibiotic: name, interpretation: 'Sensitive', mic: null }]));

  const complete = rows.every((row) => row.antibiotic.trim().length > 0);
  const unused = COMMON_ANTIBIOTICS.filter((name) => !rows.some((row) => row.antibiotic.toLowerCase() === name.toLowerCase()));

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (!readOnly && complete) {
              onApply({ value: value.trim(), organism: organism.trim(), colonyCount: colonyCount.trim(), susceptibilities: rows.map((row) => ({ ...row, antibiotic: row.antibiotic.trim(), mic: row.mic?.trim() || null })) });
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{testName}: culture and sensitivity</DialogTitle>
            <DialogContent className={styles.content}>
              <Field label="Growth" required hint="Choose a statement or type your own">
                <Input value={value} onChange={(_, data) => setValue(data.value.slice(0, 2000))} disabled={readOnly} autoFocus />
                {!readOnly && (
                  <div className={styles.quick}>
                    {statements.map((statement) => (
                      <Button key={statement} size="small" shape="circular" appearance={value === statement ? 'primary' : 'outline'} onClick={() => setValue(statement)}>
                        {statement}
                      </Button>
                    ))}
                  </div>
                )}
              </Field>
              <FormGrid columns={2}>
                <Field label="Organism isolated" hint="Leave empty when there is no growth">
                  <Input value={organism} onChange={(_, data) => setOrganism(data.value.slice(0, 200))} disabled={readOnly} placeholder="e.g. Escherichia coli" />
                </Field>
                <Field label="Colony count">
                  <Input value={colonyCount} onChange={(_, data) => setColonyCount(data.value.slice(0, 100))} disabled={readOnly} placeholder="e.g. >10^5 CFU/mL" />
                </Field>
              </FormGrid>

              <div>
                <Text weight="semibold">Antibiotic sensitivity</Text>
                <div className={styles.table} role="table" aria-label="Antibiotic sensitivity">
                  {rows.length > 0 && (
                    <div className={styles.row} role="row">
                      <Caption1 className={styles.header} role="columnheader">
                        Antibiotic
                      </Caption1>
                      <Caption1 className={styles.header} role="columnheader">
                        Result
                      </Caption1>
                      <Caption1 className={styles.header} role="columnheader">
                        MIC (µg/mL)
                      </Caption1>
                      <span />
                    </div>
                  )}
                  {rows.map((row, index) => (
                    <div key={index} className={styles.row} role="row">
                      <Input
                        value={row.antibiotic}
                        onChange={(_, data) => update(index, { antibiotic: data.value.slice(0, 100) })}
                        disabled={readOnly}
                        aria-label={`Antibiotic ${index + 1}`}
                        list={listId}
                      />
                      <div className={styles.sir} role="radiogroup" aria-label={`Result for ${row.antibiotic || `antibiotic ${index + 1}`}`}>
                        {INTERPRETATIONS.map((option) => (
                          <ToggleButton
                            key={option.value}
                            size="small"
                            checked={row.interpretation === option.value}
                            onClick={() => update(index, { interpretation: option.value })}
                            disabled={readOnly}
                            title={option.label}
                            aria-label={option.label}
                            role="radio"
                            aria-checked={row.interpretation === option.value}
                          >
                            {option.code}
                          </ToggleButton>
                        ))}
                      </div>
                      <Input value={row.mic ?? ''} onChange={(_, data) => update(index, { mic: data.value.slice(0, 32) })} disabled={readOnly} aria-label={`MIC for ${row.antibiotic}`} />
                      {!readOnly && (
                        <Button
                          appearance="subtle"
                          icon={<DeleteRegular />}
                          aria-label={`Remove ${row.antibiotic || 'row'}`}
                          onClick={() => setRows((current) => current.filter((_, i) => i !== index))}
                        />
                      )}
                    </div>
                  ))}
                </div>
                <datalist id={listId}>
                  {COMMON_ANTIBIOTICS.map((name) => (
                    <option key={name} value={name} />
                  ))}
                </datalist>
                {!readOnly && (
                  <>
                    <div className={styles.quick}>
                      <Button size="small" icon={<AddRegular />} onClick={() => addAntibiotic()}>
                        Add antibiotic
                      </Button>
                    </div>
                    {organism.trim() && unused.length > 0 && (
                      <div className={styles.quick} role="group" aria-label="Common antibiotics">
                        {unused.slice(0, 14).map((name) => (
                          <Button key={name} size="small" shape="circular" appearance="outline" onClick={() => addAntibiotic(name)}>
                            + {name}
                          </Button>
                        ))}
                      </div>
                    )}
                  </>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>{readOnly ? 'Close' : 'Cancel'}</Button>
              {!readOnly && (
                <Button type="submit" appearance="primary" disabled={!complete || !value.trim()}>
                  Use these results
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
