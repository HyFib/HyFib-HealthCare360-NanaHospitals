import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
} from '@fluentui/react-components';
import { DataTable, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { errorMessage } from '@/modules/lab/common/errors';
import { AREA_LABELS, erKeys, erQueries, type ErArea, type ErBay } from './api';

/**
 * The department's bays, for whoever keeps the masters: add a bay with its code and area, and take one out of use (it
 * takes no patient then; a patient already in it stays until moved). Who is in each bay is on the board.
 */
export function ErBaysDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const bays = useQuery(erQueries.bays());
  const [code, setCode] = useState('');
  const [area, setArea] = useState<ErArea>('Acute');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: erKeys.all });
  const next = (bays.data?.bays.length ?? 0) + 1;

  const add = useMutation({
    mutationFn: () =>
      api.post<ErBay>('/api/v1/emergency/bays', {
        code: code.trim(),
        area,
        displayOrder: next,
        isActive: true,
        version: null,
      }),
    onSuccess: (bay) => {
      notify.success('Bay added', bay.code);
      setCode('');
      refresh();
    },
  });
  const toggle = useMutation({
    mutationFn: (bay: ErBay) =>
      api.put<ErBay>(`/api/v1/emergency/bays/${bay.id}`, {
        code: bay.code,
        area: bay.area,
        displayOrder: bay.displayOrder,
        isActive: !bay.isActive,
        version: bay.version,
      }),
    onSuccess: refresh,
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>ER bays</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <DataTable<ErBay>
              ariaLabel="Bays of the emergency department"
              rows={bays.data?.bays}
              loading={bays.isLoading}
              error={bays.error}
              getRowId={(b) => b.id}
              size="small"
              emptyTitle="No bays yet"
              columns={[
                { key: 'code', header: 'Bay', width: 90, render: (b) => b.code },
                { key: 'area', header: 'Area', render: (b) => AREA_LABELS[b.area] },
                { key: 'who', header: 'Patient', render: (b) => b.patientName ?? '—' },
                {
                  key: 'use',
                  header: 'In use',
                  width: 90,
                  render: (b) => (
                    <Checkbox
                      aria-label={`Bay ${b.code} in use`}
                      checked={b.isActive}
                      disabled={toggle.isPending}
                      onChange={() => toggle.mutate(b)}
                    />
                  ),
                },
              ]}
            />
            <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end' }}>
              <Field label="New bay" style={{ width: 120 }}>
                <Input value={code} onChange={(_, d) => setCode(d.value)} maxLength={16} />
              </Field>
              <Field label="Area" style={{ minWidth: 180 }}>
                <Select value={area} onChange={(_, d) => setArea(d.value as ErArea)}>
                  {Object.entries(AREA_LABELS).map(([k, label]) => (
                    <option key={k} value={k}>
                      {label}
                    </option>
                  ))}
                </Select>
              </Field>
              <Button appearance="primary" disabled={!code.trim() || add.isPending} onClick={() => add.mutate()}>
                Add bay
              </Button>
            </div>
            {(add.error ?? toggle.error) && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {errorMessage(add.error ?? toggle.error, 'The bay could not be saved.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
