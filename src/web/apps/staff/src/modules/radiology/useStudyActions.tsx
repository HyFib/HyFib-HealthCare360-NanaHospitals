import { Caption1 } from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { ReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { radiologyApi, radiologyKeys } from './api';
import { AcquireDialog, RepeatDialog, ScheduleDialog, type StudyRef } from './StudyDialogs';
import { openViewerUrl } from './viewer';
import type { StudyAction } from './workflow';

/**
 * Technologist actions on an imaging study (schedule, arrival, scan, repeats, cancel, PACS linking, viewer) with their
 * dialogs. Used by the worklist rows and the study page so both behave the same.
 */
export function useStudyActions(): {
  run: (action: StudyAction, study: StudyRef) => void;
  openImages: (studyId: string) => Promise<void>;
  busy: boolean;
  dialogs: ReactNode;
} {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<{
    action: 'schedule' | 'acquire' | 'repeat' | 'cancel';
    study: StudyRef;
  } | null>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
  const done = (message: string) => {
    setDialog(null);
    notify.success(message);
    refresh();
  };

  const quick = useMutation({
    mutationFn: ({ action, study }: { action: 'arrive' | 'start' | 'reconcile'; study: StudyRef }) =>
      action === 'arrive'
        ? radiologyApi.arrive(study.id)
        : action === 'start'
          ? radiologyApi.start(study.id)
          : radiologyApi.reconcile(study.id),
    onSuccess: (detail, { action }) => {
      refresh();
      if (action === 'reconcile') {
        notify.success(
          'Images linked from PACS',
          detail.seriesCount ? `${detail.seriesCount} series, ${detail.instanceCount ?? 0} images` : undefined,
        );
      } else {
        notify.success(action === 'arrive' ? 'Patient marked as arrived' : 'Scan started');
      }
    },
    onError: (error, { action }) => {
      if (action === 'reconcile' && isApiError(error) && error.code?.startsWith('pacs.')) {
        notify.info('Images not linked', error.userMessage);
      } else {
        notify.error(error, 'The study could not be updated');
      }
      refresh();
    },
  });

  const cancel = useMutation({
    mutationFn: ({ study, reason }: { study: StudyRef; reason: string }) => radiologyApi.cancel(study.id, reason),
    onSuccess: () => done('Study cancelled'),
    onError: (error) => notify.error(error, 'The study could not be cancelled'),
  });

  const run = (action: StudyAction, study: StudyRef) => {
    switch (action) {
      case 'arrive':
      case 'start':
      case 'reconcile':
        quick.mutate({ action, study });
        break;
      case 'report':
        void navigate({ to: '/radiology/studies/$studyId', params: { studyId: study.id } });
        break;
      default:
        setDialog({ action, study });
    }
  };

  const openImages = async (studyId: string) => {
    try {
      const link = await radiologyApi.viewer(studyId);
      await openViewerUrl(link.url);
    } catch (error) {
      if (isApiError(error) && error.code?.startsWith('pacs.')) {
        notify.info('Images are not available', error.userMessage);
      } else {
        notify.error(error, 'Could not open the images');
      }
    }
  };

  const dialogs = (
    <>
      {dialog?.action === 'schedule' && (
        <ScheduleDialog study={dialog.study} onClose={() => setDialog(null)} onDone={() => done('Study scheduled')} />
      )}
      {dialog?.action === 'acquire' && (
        <AcquireDialog
          study={dialog.study}
          onClose={() => setDialog(null)}
          onDone={() => done('Scan completed. The study is on the reporting worklist.')}
        />
      )}
      {dialog?.action === 'repeat' && (
        <RepeatDialog study={dialog.study} onClose={() => setDialog(null)} onDone={() => done('Repeat recorded')} />
      )}
      {dialog?.action === 'cancel' && (
        <ReasonDialog
          key={dialog.study.id}
          open
          title={`Cancel ${dialog.study.studyName}?`}
          description={
            <Caption1>The charge is reversed on the bill. A study that has been reported cannot be cancelled.</Caption1>
          }
          suggestions={[
            'Patient declined',
            'Doctor cancelled the order',
            'Patient not fit for the scan',
            'Duplicate order',
          ]}
          confirmLabel="Cancel study"
          destructive
          busy={cancel.isPending}
          onCancel={() => setDialog(null)}
          onConfirm={(reason) => cancel.mutate({ study: dialog.study, reason })}
        />
      )}
    </>
  );

  return { run, openImages, busy: quick.isPending, dialogs };
}
