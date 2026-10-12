import {
  Button,
  Caption1,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownRegular, ArrowUpRegular, DeleteRegular, SaveRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage } from '../common/errors';
import { labApi, labKeys, type LabTestDetail } from '../api';
import { TestLookup } from '../TestLookup';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  list: { display: 'flex', flexDirection: 'column', gap: '6px', margin: 0, padding: 0, listStyleType: 'none' },
  item: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(0, 1fr) minmax(140px, 200px) auto',
    gap: '8px',
    alignItems: 'center',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', justifyContent: 'flex-end' },
});

interface ComponentRow {
  testId: string;
  code: string;
  name: string;
  groupName: string;
}

/**
 * Analytes of a panel in print order, with optional group headings ("Differential count"). A panel cannot contain
 * itself, directly or through another panel; the server refuses such cycles.
 */
export function ComponentsEditor({ test }: { test: LabTestDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<ComponentRow[]>(() =>
    [...test.components]
      .sort((a, b) => a.displayOrder - b.displayOrder)
      .map((c) => ({ testId: c.testId, code: c.code, name: c.name, groupName: c.groupName ?? '' })),
  );
  const [dirty, setDirty] = useState(false);

  const save = useMutation({
    mutationFn: () =>
      labApi.setComponents(
        test.id,
        rows.map((row) => ({ testId: row.testId, groupName: row.groupName.trim() || null })),
        test.version,
      ),
    onSuccess: () => {
      setDirty(false);
      notify.success('Panel saved', 'New orders of this panel get these analytes.');
      void queryClient.invalidateQueries({ queryKey: labKeys.masters });
    },
  });

  const update = (next: ComponentRow[]) => {
    setRows(next);
    setDirty(true);
  };

  const move = (index: number, step: -1 | 1) => {
    const target = index + step;
    if (target < 0 || target >= rows.length) {
      return;
    }
    const next = [...rows];
    [next[index], next[target]] = [next[target]!, next[index]!];
    update(next);
  };

  return (
    <div className={styles.root}>
      {save.error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(save.error, 'The panel could not be saved.')}</MessageBarBody>
        </MessageBar>
      )}
      <TestLookup
        label="Add an analyte"
        value={null}
        filter={(candidate) => candidate.id !== test.id && !rows.some((row) => row.testId === candidate.id)}
        onChange={(candidate) =>
          candidate &&
          update([
            ...rows,
            {
              testId: candidate.id,
              code: candidate.code,
              name: candidate.name,
              groupName: rows.at(-1)?.groupName ?? '',
            },
          ])
        }
      />
      {rows.length === 0 ? (
        <Text>This panel has no analytes yet. Add the tests it reports, in print order.</Text>
      ) : (
        <ol className={styles.list} aria-label="Panel analytes in print order">
          {rows.map((row, index) => (
            <li key={row.testId} className={styles.item}>
              <Caption1 className={styles.muted}>{index + 1}</Caption1>
              <span>
                <Text weight="semibold">{row.name}</Text> <Caption1 className={styles.muted}>{row.code}</Caption1>
              </span>
              <Input
                size="small"
                value={row.groupName}
                placeholder="Group heading (optional)"
                aria-label={`Group heading for ${row.name}`}
                onChange={(_, data) =>
                  update(rows.map((r) => (r.testId === row.testId ? { ...r, groupName: data.value.slice(0, 100) } : r)))
                }
              />
              <span>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<ArrowUpRegular />}
                  aria-label={`Move ${row.name} up`}
                  disabled={index === 0}
                  onClick={() => move(index, -1)}
                />
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<ArrowDownRegular />}
                  aria-label={`Move ${row.name} down`}
                  disabled={index === rows.length - 1}
                  onClick={() => move(index, 1)}
                />
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Remove ${row.name}`}
                  onClick={() => update(rows.filter((r) => r.testId !== row.testId))}
                />
              </span>
            </li>
          ))}
        </ol>
      )}
      <div className={styles.footer}>
        <Button
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
          disabled={!dirty || rows.length === 0 || rows.length > 100 || save.isPending}
          onClick={() => save.mutate()}
        >
          Save panel
        </Button>
      </div>
    </div>
  );
}
