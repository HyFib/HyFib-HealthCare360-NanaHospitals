import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner } from '@fluentui/react-components';
import { ArrowDownloadRegular, DocumentTableRegular, TextBulletListSquareRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { analyticsApi, type ExportFormat, type ReportResult } from './api';
import { saveBlob } from './download';
import { exportFileName } from './params';

/**
 * "Export" → Excel or CSV of the whole report as on screen (inputs and sort). Files carry the hospital name, inputs,
 * who generated them and a confidentiality notice; exports of patient lists are confirmed first and audited per patient.
 */
export function ExportMenu({ result, disabled, appearance = 'secondary' }: { result: ReportResult; disabled?: boolean; appearance?: 'primary' | 'secondary' }) {
  const notify = useNotify();
  const [pending, setPending] = useState<ExportFormat | null>(null);

  const download = useMutation({
    mutationFn: async (format: ExportFormat) => {
      const blob = await analyticsApi.exportReport(result.reportId, result.parameters, format, result.sort ?? undefined, result.desc);
      saveBlob(blob, exportFileName(result.reportId, result.from, result.to, todayIso(), format === 'Xlsx' ? 'xlsx' : 'csv'));
    },
    onSuccess: (_, format) => {
      setPending(null);
      notify.success(format === 'Xlsx' ? 'Excel file downloaded' : 'CSV file downloaded', 'It is in your Downloads folder.');
    },
    onError: (error) => {
      setPending(null);
      notify.error(error, 'Could not export the report');
    },
  });

  const start = (format: ExportFormat) => {
    if (result.containsPatientIdentifiers) {
      setPending(format);
    } else {
      download.mutate(format);
    }
  };

  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button appearance={appearance} icon={download.isPending ? <Spinner size="tiny" /> : <ArrowDownloadRegular />} disabled={disabled || download.isPending}>
            {download.isPending ? 'Preparing file…' : 'Export'}
          </Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem icon={<DocumentTableRegular />} secondaryContent=".xlsx" onClick={() => start('Xlsx')}>
              Excel workbook
            </MenuItem>
            <MenuItem icon={<TextBulletListSquareRegular />} secondaryContent=".csv" onClick={() => start('Csv')}>
              CSV file
            </MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>

      <ConfirmDialog
        open={pending !== null}
        title="Download a list of patients?"
        confirmLabel="Download"
        busy={download.isPending}
        onConfirm={() => pending && download.mutate(pending)}
        onCancel={() => setPending(null)}
      >
        This file lists patients by name and UHID. The download is recorded in the audit log with your name. Keep it on a hospital computer, do not
        e-mail or print it unless it is needed for care or quality review, and delete it when you are done.
      </ConfirmDialog>
    </>
  );
}
