import {
  Button,
  Caption1,
  Field,
  makeStyles,
  mergeClasses,
  SearchBox,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, InfoRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useId, useState, type KeyboardEvent } from 'react';
import { labQueries, type LabTestListItem } from './api';
import { TubeChip } from './badges';
import { sectionLabel } from './labels';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  quick: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  list: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    maxHeight: '320px',
    overflowY: 'auto',
  },
  option: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 12px',
    padding: '8px 12px',
    cursor: 'pointer',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: {
    backgroundColor: tokens.colorBrandBackground2,
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover },
  },
  chosen: { color: tokens.colorNeutralForeground3, cursor: 'default' },
  meta: { display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap', color: tokens.colorNeutralForeground3 },
  price: { textAlign: 'end', fontWeight: tokens.fontWeightSemibold },
  prep: { display: 'inline-flex', gap: '4px', alignItems: 'center', color: tokens.colorPaletteMarigoldForeground1 },
  empty: { padding: '12px', color: tokens.colorNeutralForeground3 },
});

/** Common counter tests offered as one-click chips (shown only when they exist in the test master). */
const QUICK_CODES = [
  'LAB-CBC',
  'LAB-FBS',
  'LAB-PPBS',
  'LAB-HBA1C',
  'LAB-LIPID',
  'LAB-LFT',
  'LAB-KFT',
  'LAB-TFT',
  'LAB-URINE-RE',
  'LAB-CRP',
];

const CATALOG_SIZE = 200;

function matches(test: LabTestListItem, words: string[]): boolean {
  const haystack = `${test.code} ${test.name} ${test.shortName ?? ''}`.toLowerCase();
  return words.every((word) => haystack.includes(word));
}

/**
 * Orderable test search for the lab counter. Typing filters instantly (the catalogue is cached on the workstation),
 * ↑/↓ moves, Enter adds the highlighted test and clears the box for the next one.
 */
export function TestPicker({
  selectedIds,
  onAdd,
  autoFocus,
  validationMessage,
}: {
  selectedIds: readonly string[];
  onAdd: (test: LabTestListItem) => void;
  autoFocus?: boolean;
  validationMessage?: string;
}) {
  const styles = useStyles();
  const listId = useId();
  const [term, setTerm] = useState('');
  const [active, setActive] = useState(0);
  const debounced = useDebouncedValue(term.trim(), 200);
  const catalog = useQuery(labQueries.tests({ orderable: true, pageSize: CATALOG_SIZE }));
  const complete = catalog.data !== undefined && catalog.data.totalCount <= catalog.data.items.length;
  const remote = useQuery({
    ...labQueries.tests({ orderable: true, search: debounced, pageSize: 20 }),
    enabled: !complete && catalog.data !== undefined && debounced.length >= 2,
  });

  const words = term.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const results: LabTestListItem[] =
    words.length === 0
      ? []
      : complete
        ? (catalog.data?.items ?? []).filter((test) => matches(test, words)).slice(0, 20)
        : (remote.data?.items ?? []);
  const quick = QUICK_CODES.map((code) => catalog.data?.items.find((test) => test.code === code)).filter(
    (test): test is LabTestListItem => Boolean(test),
  );
  const highlighted = Math.min(active, Math.max(results.length - 1, 0));

  const add = (test: LabTestListItem | undefined) => {
    if (!test || selectedIds.includes(test.id)) {
      return;
    }
    onAdd(test);
    setTerm('');
    setActive(0);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActive(Math.min(highlighted + 1, results.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive(Math.max(highlighted - 1, 0));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      add(results[highlighted]);
    } else if (event.key === 'Escape' && term) {
      event.preventDefault();
      setTerm('');
    }
  };

  return (
    <div className={styles.root}>
      <Field
        label="Add tests"
        hint="Type a test name or code, then press Enter. ↑ ↓ to choose."
        validationMessage={validationMessage}
        validationState={validationMessage ? 'error' : 'none'}
      >
        <SearchBox
          value={term}
          onChange={(_, data) => {
            setTerm(data.value);
            setActive(0);
          }}
          onKeyDown={onKeyDown}
          placeholder="e.g. CBC, sugar, thyroid"
          autoFocus={autoFocus}
          role="combobox"
          aria-expanded={results.length > 0}
          aria-controls={listId}
          aria-activedescendant={results[highlighted] ? `${listId}-${results[highlighted].id}` : undefined}
          contentAfter={catalog.isFetching || remote.isFetching ? <Spinner size="extra-tiny" /> : undefined}
        />
      </Field>

      {words.length === 0 && quick.length > 0 && (
        <div className={styles.quick} role="group" aria-label="Common tests">
          {quick.map((test) => (
            <Button
              key={test.id}
              size="small"
              shape="circular"
              appearance={selectedIds.includes(test.id) ? 'primary' : 'outline'}
              icon={selectedIds.includes(test.id) ? <CheckmarkRegular /> : undefined}
              disabled={selectedIds.includes(test.id)}
              onClick={() => add(test)}
            >
              {test.shortName ?? test.name}
            </Button>
          ))}
        </div>
      )}

      {words.length > 0 && (
        <ul id={listId} role="listbox" aria-label="Matching tests" className={styles.list}>
          {results.length === 0 ? (
            <li className={styles.empty} role="presentation">
              {catalog.isLoading || remote.isFetching
                ? 'Searching…'
                : 'No orderable test matches. Check the spelling or search by code.'}
            </li>
          ) : (
            results.map((test, index) => {
              const chosen = selectedIds.includes(test.id);
              return (
                <li
                  key={test.id}
                  id={`${listId}-${test.id}`}
                  role="option"
                  aria-selected={index === highlighted}
                  aria-disabled={chosen}
                  className={mergeClasses(
                    styles.option,
                    index === highlighted && styles.active,
                    chosen && styles.chosen,
                  )}
                  onMouseEnter={() => setActive(index)}
                  onClick={() => add(test)}
                >
                  <span>
                    <Text weight="semibold">{test.name}</Text> <Caption1>{test.code}</Caption1>
                    {chosen && (
                      <Caption1>
                        {' '}
                        <CheckmarkRegular aria-hidden /> added
                      </Caption1>
                    )}
                  </span>
                  <span className={styles.price}>
                    {test.price !== null ? formatInr(test.price, { whole: true }) : '—'}
                  </span>
                  <span className={styles.meta}>
                    <span>{sectionLabel[test.section]}</span>
                    <TubeChip container={test.container} />
                    {test.isPanel && <span>{test.componentCount} parameters</span>}
                    {test.preparation && (
                      <span className={styles.prep}>
                        <InfoRegular aria-hidden /> {test.preparation}
                      </span>
                    )}
                  </span>
                  <Caption1 className={styles.price}>{test.tatHours ? `Report in ${test.tatHours} h` : ''}</Caption1>
                </li>
              );
            })
          )}
        </ul>
      )}
    </div>
  );
}
