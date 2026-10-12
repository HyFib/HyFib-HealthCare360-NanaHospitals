import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { WrenchRegular } from '@fluentui/react-icons';
import { isApiError, type PagedResult } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { SearchPicker } from '@/modules/inventory/ItemPicker';
import { assetApi, assetKeys, type AssetListItem } from './api';
import {
  assetStatusLabel,
  criticalityTone,
  TICKET_PRIORITIES,
  type AssetCriticality,
  type TicketPriority,
} from './labels';

const useStyles = makeStyles({
  asset: {
    display: 'flex',
    gap: '10px',
    alignItems: 'center',
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  grow: { flex: 1, minWidth: 0 },
});

export interface BreakdownAsset {
  id: string;
  assetTag: string;
  name: string;
  location: string | null;
  criticality: AssetCriticality;
}

/**
 * Report an equipment breakdown (AST-04). Any ward can report; the priority follows the equipment's criticality
 * unless changed, and "cannot be used" takes it out of service and alerts biomedical engineering at once.
 */
export function BreakdownDialog({
  asset: preset,
  onClose,
  onReported,
}: {
  asset?: BreakdownAsset | null;
  onClose: () => void;
  onReported?: (ticketId: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [asset, setAsset] = useState<BreakdownAsset | null>(preset ?? null);
  const [issue, setIssue] = useState('');
  const [priority, setPriority] = useState<TicketPriority | ''>('');
  const [down, setDown] = useState(false);
  const valid = asset !== null && issue.trim().length >= 5;
  const dirty = useValuesChanged({ asset, issue, priority, down });

  const report = useMutation({
    mutationFn: () =>
      assetApi.reportBreakdown({
        assetId: asset!.id,
        issue: issue.trim(),
        priority: priority || null,
        assetDown: down,
      }),
    onSuccess: (ticket) => {
      notify.success(
        `Breakdown ${ticket.number} reported`,
        down
          ? 'The equipment is marked out of service and biomedical engineering has been alerted.'
          : 'Biomedical engineering will pick it up.',
      );
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onReported?.(ticket.id);
      onClose();
    },
  });

  const guard = useUnsavedChanges({ dirty, busy: report.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 620, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Report equipment breakdown</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12, minHeight: 280 }}>
            {asset ? (
              <div className={styles.asset}>
                <WrenchRegular fontSize={22} aria-hidden />
                <div className={styles.grow}>
                  <Text weight="semibold">{asset.name}</Text>
                  <Caption1 block>
                    {asset.assetTag}
                    {asset.location ? ` · ${asset.location}` : ''}
                  </Caption1>
                </div>
                <Badge appearance="tint" color={criticalityTone[asset.criticality]}>
                  {asset.criticality}
                </Badge>
                {!preset && (
                  <Button size="small" onClick={() => setAsset(null)}>
                    Change
                  </Button>
                )}
              </div>
            ) : (
              <SearchPicker<AssetListItem>
                label="Equipment"
                required
                autoFocus
                placeholder="Asset tag (scan the label), name, model or serial number"
                queryKey={['assets', 'picker']}
                fetch={(search, signal) =>
                  api
                    .get<PagedResult<AssetListItem>>('/api/v1/assets', { query: { search, pageSize: 10 }, signal })
                    .then((r) => r.items)
                }
                getKey={(a) => a.id}
                bestMatch={(items, typed) =>
                  items.find((a) => a.assetTag.toUpperCase() === typed.trim().toUpperCase()) ?? items[0]
                }
                emptyText="No equipment matches. Check the tag on the label."
                onPick={(a) =>
                  setAsset({
                    id: a.id,
                    assetTag: a.assetTag,
                    name: a.name,
                    location: a.location ?? a.departmentName,
                    criticality: a.criticality,
                  })
                }
                renderOption={(a) => (
                  <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, width: '100%' }}>
                    <span>
                      <Text weight="semibold">{a.name}</Text>
                      <Caption1 block>
                        {a.assetTag}
                        {a.model ? ` · ${a.model}` : ''}
                        {a.location ? ` · ${a.location}` : a.departmentName ? ` · ${a.departmentName}` : ''}
                      </Caption1>
                    </span>
                    <Caption1>{assetStatusLabel[a.status]}</Caption1>
                  </div>
                )}
              />
            )}
            <Field
              label="What is wrong?"
              required
              hint="What happened and what you see, e.g. alarm keeps sounding, screen blank after power on"
            >
              <Textarea
                value={issue}
                rows={3}
                maxLength={2000}
                onChange={(_, d) => setIssue(d.value)}
                autoFocus={Boolean(preset)}
              />
            </Field>
            <Field label="Priority">
              <Select value={priority} onChange={(_, d) => setPriority(d.value as TicketPriority | '')}>
                <option value="">Based on how critical the equipment is</option>
                {TICKET_PRIORITIES.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={down}
              onChange={(_, d) => setDown(d.checked)}
              label="The equipment cannot be used until it is repaired"
            />
            {down && (
              <Caption1>
                It is marked under maintenance and the biomedical engineers are notified straight away.
              </Caption1>
            )}
            {report.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(report.error) ? report.error.userMessage : 'Not reported. Try again.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={report.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || report.isPending}
              icon={report.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => report.mutate()}
            >
              Report breakdown
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
