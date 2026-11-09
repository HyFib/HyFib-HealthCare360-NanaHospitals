import { Button, Caption1, Field, MessageBar, MessageBarBody, Select, Text } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage } from '@/modules/lab/common/errors';
import { AREA_LABELS, erApi, erKeys, erQueries, type EmergencyCase } from './api';

/**
 * The patient's bay in the emergency department: put them in a free bay, move them to another, or free theirs (gone for
 * a scan, moved to a chair). A bay holds one patient; the disposition frees it. Only free bays in use are offered; the
 * server refuses a bay someone else filled a moment ago.
 */
export function BayControl({ c, canMove }: { c: EmergencyCase; canMove: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const bays = useQuery({ ...erQueries.bays(), enabled: canMove && !c.disposition });
  const [chosen, setChosen] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: erKeys.all });
  const free = (bays.data?.bays ?? []).filter((b) => b.isActive && b.caseId === null && b.id !== c.bayId);

  const assign = useMutation({
    mutationFn: () => erApi.assignBay(c.id, chosen, c.version),
    onSuccess: (updated) => {
      notify.success(c.bayId ? 'Moved' : 'In a bay', `Bay ${updated.bayCode ?? ''}`);
      setChosen('');
      refresh();
    },
    onError: refresh,
  });
  const release = useMutation({
    mutationFn: () => erApi.releaseBay(c.id, c.version),
    onSuccess: () => {
      notify.success('Bay freed', 'The patient is still in the department.');
      refresh();
    },
  });
  const error = assign.error ?? release.error;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <Text weight="semibold">Bay</Text>
      <Caption1>
        {c.bayCode
          ? `In bay ${c.bayCode}${c.bayAssignedAt ? ` since ${formatDateTime(c.bayAssignedAt)}` : ''}`
          : c.disposition
            ? 'Left the department.'
            : 'Not in a bay.'}
      </Caption1>
      {canMove && !c.disposition && (
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label={c.bayId ? 'Move to bay' : 'Put in bay'} style={{ minWidth: 240 }}>
            <Select value={chosen} onChange={(_, d) => setChosen(d.value)} disabled={bays.isLoading}>
              <option value="">{free.length === 0 ? 'No free bay' : 'Choose a free bay'}</option>
              {free.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.code} · {AREA_LABELS[b.area]}
                </option>
              ))}
            </Select>
          </Field>
          <Button appearance="primary" disabled={!chosen || assign.isPending} onClick={() => assign.mutate()}>
            {c.bayId ? 'Move' : 'Put in bay'}
          </Button>
          {c.bayId && (
            <Button disabled={release.isPending} onClick={() => release.mutate()}>
              Free the bay
            </Button>
          )}
        </div>
      )}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(error, 'The bay could not be changed.')}</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
