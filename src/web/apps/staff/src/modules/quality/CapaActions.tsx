import { Button, Radio, RadioGroup, Spinner, Field } from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  DismissCircleRegular,
  EditRegular,
  PlayRegular,
  ShieldCheckmarkRegular,
} from '@fluentui/react-icons';
import { useSession } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { qualityApi, qualityKeys } from './api';
import { QUALITY_PERMISSIONS, type CapaStatus } from './labels';
import { capaActions } from './rules';

export interface CapaLike {
  id: string;
  capaNumber: string;
  action: string;
  status: CapaStatus;
  ownerUserId: string;
}

/**
 * Next steps on a corrective/preventive action: the owner starts and completes it; the quality team checks that it
 * worked (never on their own action) or cancels it.
 */
export function CapaActionButtons({
  capa,
  onEdit,
  size = 'medium',
}: {
  capa: CapaLike;
  onEdit?: () => void;
  size?: 'small' | 'medium';
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const userId = useSession((s) => s.user?.id);
  const isManager = usePermission(QUALITY_PERMISSIONS.capaManage);
  const actions = capaActions(capa.status, { isOwner: capa.ownerUserId === userId, isManager });
  const [dialog, setDialog] = useState<'complete' | 'verify' | 'cancel' | null>(null);
  const [effective, setEffective] = useState<'yes' | 'no'>('yes');
  const [busy, setBusy] = useState(false);

  const run = async (work: () => Promise<unknown>, success: string, detail?: string) => {
    setBusy(true);
    try {
      await work();
      notify.success(success, detail);
      setDialog(null);
      void queryClient.invalidateQueries({ queryKey: qualityKeys.all });
    } catch (error) {
      notify.error(error, 'The action could not be updated');
    } finally {
      setBusy(false);
    }
  };

  if (actions.length === 0) {
    return null;
  }

  return (
    <span style={{ display: 'inline-flex', gap: 6, flexWrap: 'wrap' }}>
      {actions.includes('start') && (
        <Button
          size={size}
          icon={busy && !dialog ? <Spinner size="tiny" aria-hidden /> : <PlayRegular />}
          disabled={busy}
          onClick={() => void run(() => qualityApi.startCapa(capa.id), 'Action started')}
        >
          Start
        </Button>
      )}
      {actions.includes('complete') && (
        <Button
          size={size}
          appearance="primary"
          icon={<CheckmarkCircleRegular />}
          disabled={busy}
          onClick={() => setDialog('complete')}
        >
          Mark done
        </Button>
      )}
      {actions.includes('verify') && (
        <Button
          size={size}
          appearance="primary"
          icon={<ShieldCheckmarkRegular />}
          disabled={busy}
          onClick={() => setDialog('verify')}
        >
          Check effectiveness
        </Button>
      )}
      {actions.includes('edit') && onEdit && (
        <Button size={size} icon={<EditRegular />} disabled={busy} onClick={onEdit}>
          Edit
        </Button>
      )}
      {actions.includes('cancel') && (
        <Button
          size={size}
          appearance="subtle"
          icon={<DismissCircleRegular />}
          disabled={busy}
          onClick={() => setDialog('cancel')}
        >
          Cancel action
        </Button>
      )}

      <ReasonDialog
        open={dialog === 'complete'}
        title={`Mark ${capa.capaNumber} as done`}
        description={capa.action}
        label="What was done"
        hint="The quality team checks the effect about 30 days later."
        confirmLabel="Mark done"
        maxLength={2000}
        busy={busy}
        onConfirm={(note) =>
          void run(
            () => qualityApi.completeCapa(capa.id, note),
            'Action marked done',
            'The quality team will check that it worked.',
          )
        }
        onCancel={() => setDialog(null)}
      />
      <ReasonDialog
        open={dialog === 'verify'}
        title={`Did ${capa.capaNumber} work?`}
        description={capa.action}
        label="Evidence"
        hint="For example: audit of 20 charts in October showed no repeat."
        confirmLabel={effective === 'yes' ? 'Verify as effective' : 'Send back for rework'}
        maxLength={2000}
        busy={busy}
        onConfirm={(note) =>
          void run(
            () => qualityApi.verifyCapa(capa.id, effective === 'yes', note),
            effective === 'yes' ? 'Verified as effective' : 'Sent back for rework',
            effective === 'yes' ? undefined : 'The owner has been notified.',
          )
        }
        onCancel={() => setDialog(null)}
      >
        <Field label="Result">
          <RadioGroup value={effective} onChange={(_, d) => setEffective(d.value as 'yes' | 'no')} layout="horizontal">
            <Radio value="yes" label="Effective: the problem has not come back" />
            <Radio value="no" label="Not effective: needs more work" />
          </RadioGroup>
        </Field>
      </ReasonDialog>
      <ReasonDialog
        open={dialog === 'cancel'}
        title={`Cancel ${capa.capaNumber}?`}
        description={capa.action}
        label="Reason"
        confirmLabel="Cancel action"
        destructive
        busy={busy}
        onConfirm={(reason) => void run(() => qualityApi.cancelCapa(capa.id, reason), 'Action cancelled')}
        onCancel={() => setDialog(null)}
      />
    </span>
  );
}
