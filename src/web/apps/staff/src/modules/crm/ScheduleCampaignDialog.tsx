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
  MessageBar,
  MessageBarBody,
  Spinner,
} from '@fluentui/react-components';
import { CalendarClockRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { addDays, fromIstInput, nowIstInput } from '@/modules/hr/shared/dates';
import { crmApi, crmKeys, type CampaignDetail } from './api';
import { CRM_PERMISSIONS } from './labels';
import { inSendingWindow } from './rules';

/** Tomorrow at 10:30, hospital time: a sensible first suggestion inside the sending window. */
function suggestedTime(now: string): string {
  return `${addDays(now.slice(0, 10), 1)}T10:30`;
}

/** Plain-words help for the reasons the server refuses to schedule. */
const REFUSALS: Record<string, string> = {
  'campaign.empty_audience':
    'No patients who agreed to marketing messages match the audience. Widen the audience first.',
  'campaign.no_portal_url':
    "The hospital's public portal address is not set, so the opt-out link cannot be built. Ask an administrator to set 'portal.baseUrl'.",
  'campaign.not_draft': 'Only a draft can be scheduled. Someone may have scheduled or cancelled it already.',
};

/**
 * Schedules a draft campaign. Messages go out only between 10:00 and 21:00 (TRAI); a time outside the window waits for
 * the next morning. The audience is counted again at send time, so late opt-outs are respected.
 */
export function ScheduleCampaignDialog({
  open,
  onClose,
  campaign,
}: {
  open: boolean;
  onClose: () => void;
  campaign: CampaignDetail;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>{open && <Body onClose={onClose} campaign={campaign} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, campaign }: { onClose: () => void; campaign: CampaignDetail }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canPreview = usePermission(CRM_PERMISSIONS.campaignsManage);
  const [now] = useState(() => nowIstInput());
  const [value, setValue] = useState(() => suggestedTime(now));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const audience = useQuery({
    queryKey: ['crm', 'campaigns', 'preview', campaign.segment],
    queryFn: () => crmApi.previewAudience(campaign.segment),
    enabled: canPreview,
    staleTime: 30_000,
    retry: false,
  });

  const schedule = async () => {
    const at = fromIstInput(value);
    if (!at || value < now) {
      setError('Choose a time in the future.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await crmApi.scheduleCampaign(campaign.id, at);
      notify.success(
        'Campaign scheduled',
        inSendingWindow(value) ? undefined : 'It will start at 10:00, when the sending window opens.',
      );
      void queryClient.invalidateQueries({ queryKey: crmKeys.campaigns });
      onClose();
    } catch (err) {
      if (isApiError(err) && err.code && REFUSALS[err.code]) {
        setError(REFUSALS[err.code] ?? err.userMessage);
      } else if (isApiError(err) && err.isValidation) {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not schedule the campaign');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void schedule();
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Schedule “{campaign.name}”</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Field label="Start sending at (hospital time)" required hint="Messages go out between 10:00 and 21:00 only.">
            <Input
              type="datetime-local"
              value={value}
              min={now}
              onChange={(_, d) => setValue(d.value)}
              contentBefore={<CalendarClockRegular />}
            />
          </Field>
          {value && !inSendingWindow(value) && (
            <MessageBar intent="info">
              <MessageBarBody>This time is outside 10:00–21:00, so sending will wait until 10:00.</MessageBarBody>
            </MessageBar>
          )}
          {canPreview && (
            <Caption1>
              {audience.isLoading
                ? 'Counting the audience…'
                : audience.data
                  ? `${audience.data.count.toLocaleString('en-IN')} patients with a marketing consent match today. The list is checked again when sending starts.`
                  : 'The audience is counted again when sending starts.'}
            </Caption1>
          )}
          <Caption1>The saved draft is what gets sent. Save any changes to the message or audience first.</Caption1>
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy || audience.data?.count === 0}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Schedule
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
