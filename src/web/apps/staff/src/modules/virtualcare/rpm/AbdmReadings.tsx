import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Select,
} from '@fluentui/react-components';
import { useState } from 'react';
import { useAbdmRecordDownload } from '@/modules/emr/abdmRecords';
import { abdmHomeReadingsBundle } from '@/modules/emr/api';
import { monthLabel } from './readingMonths';

/**
 * Gap #27: a month of the enrolment's home readings as an ABDM wellness record, the latest month first. A month in
 * which nothing was measured is refused by the server, which says so.
 */
export function AbdmReadingsDialog({
  enrollmentId,
  months,
  open,
  onClose,
}: {
  enrollmentId: string;
  months: string[];
  open: boolean;
  onClose: () => void;
}) {
  const [month, setMonth] = useState(months[0] ?? '');
  const download = useAbdmRecordDownload();
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Download ABDM record</DialogTitle>
          <DialogContent>
            <Field
              label="Month"
              hint="The readings measured in the month, as an ABDM wellness record. Linked to the patient's ABHA, every month's go under their consent."
            >
              <Select value={month} onChange={(_, data) => setMonth(data.value)}>
                {months.map((m) => (
                  <option key={m} value={m}>
                    {monthLabel(m)}
                  </option>
                ))}
              </Select>
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={!month || download.isPending}
              onClick={() => download.mutate(abdmHomeReadingsBundle(enrollmentId, month), { onSuccess: onClose })}
            >
              {download.isPending ? 'Downloading…' : 'Download'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
