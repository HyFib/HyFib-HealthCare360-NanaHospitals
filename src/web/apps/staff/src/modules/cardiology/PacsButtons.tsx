import { Button } from '@fluentui/react-components';
import { CloudArrowUpRegular, OpenRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { pacsNotice } from '@/modules/radiology/pacsErrors';
import { openViewerUrl } from '@/modules/radiology/viewer';
import { cardiologyApi, cardiologyKeys, type CardiologyStudyDetail } from './api';
import { pacsState, sendLabel } from './pacs';

/**
 * "Send to PACS" for the technician while some of the study's DICOM files are not in the PACS, and "Open in PACS
 * viewer" once one of them is. After a send, a toast says how many files the PACS holds; the reason for any it does not
 * hold is shown under the file.
 */
export function PacsButtons({ study, canSend }: { study: CardiologyStudyDetail; canSend: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [opening, setOpening] = useState(false);
  const state = pacsState(study.files);

  const send = useMutation({
    mutationFn: () => cardiologyApi.sendToPacs(study.id),
    onSuccess: (detail) => {
      queryClient.setQueryData(cardiologyKeys.study(study.id), detail);
      void queryClient.invalidateQueries({ queryKey: cardiologyKeys.all });
      const after = pacsState(detail.files);
      if (after.toSend === 0) {
        notify.success(
          'Sent to the PACS',
          `The PACS holds ${after.stored === 1 ? 'the DICOM file' : `all ${after.stored} DICOM files`}. Open them in its viewer.`,
        );
      } else {
        notify.warning(
          `${after.toSend} of ${after.dicom} DICOM files are not in the PACS`,
          'The reason is under each file. Send them again once it is put right.',
        );
      }
    },
    onError: (error) => notify.error(error, 'The files could not be sent to the PACS'),
  });

  const openViewer = async () => {
    setOpening(true);
    try {
      const link = await cardiologyApi.viewer(study.id);
      await openViewerUrl(link.url);
    } catch (error) {
      const notice = pacsNotice(error, 'open');
      if (notice) {
        notify[notice.intent](notice.title, notice.message);
      } else {
        notify.error(error, 'Could not open the images');
      }
    } finally {
      setOpening(false);
    }
  };

  return (
    <>
      {canSend && state.toSend > 0 && study.status !== 'Cancelled' && (
        <Button icon={<CloudArrowUpRegular />} disabledFocusable={send.isPending} onClick={() => send.mutate()}>
          {send.isPending ? 'Sending…' : sendLabel(state)}
        </Button>
      )}
      {state.stored > 0 && (
        <Button icon={<OpenRegular />} disabledFocusable={opening} onClick={() => void openViewer()}>
          Open in PACS viewer
        </Button>
      )}
    </>
  );
}
