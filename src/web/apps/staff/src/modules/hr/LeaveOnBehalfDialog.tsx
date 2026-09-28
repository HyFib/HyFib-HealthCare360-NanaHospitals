import { Caption1, Dialog, DialogBody, DialogContent, DialogSurface, DialogTitle } from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { hrApi, hrKeys, hrQueries } from './api';
import { EmployeePicker, type PickedEmployee } from './EmployeePicker';
import { LeaveApplyForm } from './LeaveApplyForm';
import { applicableLeaveTypes } from './leave';
import { currentMonth } from './shared/dates';

/**
 * HR applies leave for an employee (a phone call from a sick nurse, a long leave letter). The request then follows the
 * normal approval; nobody can approve their own request.
 */
export function LeaveOnBehalfDialog({ open, onClose, employee }: { open: boolean; onClose: () => void; employee?: PickedEmployee | null }) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>{open && <Body onClose={onClose} employee={employee ?? null} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, employee }: { onClose: () => void; employee: PickedEmployee | null }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [picked, setPicked] = useState<PickedEmployee | null>(employee);
  const { year } = currentMonth();
  const types = useQuery(hrQueries.leaveTypes());
  const detail = useQuery({ ...hrQueries.employee(picked?.id ?? ''), enabled: Boolean(picked) });
  const balances = useQuery({ ...hrQueries.leaveBalances(picked?.id ?? '', year), enabled: Boolean(picked) });

  return (
    <DialogBody>
      <DialogTitle>
        Apply leave for an employee
        {employee && (
          <Caption1 block>
            {employee.name} · {employee.code}
          </Caption1>
        )}
      </DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {!employee && <EmployeePicker value={picked} onChange={setPicked} required autoFocus />}
        {picked && types.data && (
          <LeaveApplyForm
            key={picked.id}
            leaveTypes={applicableLeaveTypes(types.data, detail.data?.gender)}
            balances={balances.data}
            submitLabel="Send for approval"
            onCancel={onClose}
            submit={async (request) => {
              await hrApi.applyLeave(picked.id, request);
              notify.success('Leave applied', `${picked.name} · waiting for approval`);
              void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
              onClose();
            }}
          />
        )}
        {!picked && <Caption1>Choose the employee to see their leave balance.</Caption1>}
      </DialogContent>
    </DialogBody>
  );
}
