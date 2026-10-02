import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner } from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  DocumentPdfRegular,
  DocumentTableRegular,
  TextBulletListSquareRegular,
} from '@fluentui/react-icons';
import { saveFile, todayIso } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { notifyPdfFailure } from '@/lib/documents';
import { analyticsApi, type ExportFormat, type ReportResult } from './api';
import { exportFileName } from './params';

const DOWNLOADED: Record<ExportFormat, string> = {
  Xlsx: 'Excel file downloaded',
  Csv: 'CSV file downloaded',
  Pdf: 'PDF downloaded',
};

/**
 * "Export" → Excel, CSV or a print-ready PDF of the whole report as on screen (inputs and sort). Files carry the
 * hospital name, inputs, who generated them and a confidentiality notice; exports of patient lists are confirmed first
 * and audited per patient.
 */
export function ExportMenu({
  result,
  disabled,
  appearance = 'secondary',
}: {
  result: ReportResult;
  disabled?: boolean;
  appearance?: 'primary' | 'secondary';
}) {
  const notify = useNotify();
  const [pending, setPending] = useState<ExportFormat | null>(null);

  const download = useMutation({
    mutationFn: async (format: ExportFormat) => {
      const file = await analyticsApi.exportReport(
        result.reportId,
        result.parameters,
        format,
        result.sort ?? undefined,
        result.desc,
      );
      saveFile(file.blob, file.fileName ?? exportFileName(result.reportId, result.from, result.to, todayIso(), format));
      return file.headers.get('X-Report-Notice');
    },
    onSuccess: (notice, format) => {
      setPending(null);
      if (notice) {
        notify.info(DOWNLOADED[format], notice);
      } else {
        notify.success(DOWNLOADED[format], 'It is in your Downloads folder.');
      }
    },
    onError: (error, format) => {
      setPending(null);
      if (format === 'Pdf') {
        // The server's reason already says to export to Excel or CSV instead.
        notifyPdfFailure(notify, error);
      } else {
        notify.error(error, 'Could not export the report');
      }
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
          <Button
            appearance={appearance}
            icon={download.isPending ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
            disabled={disabled || download.isPending}
          >
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
            <MenuItem
              icon={<DocumentPdfRegular />}
              secondaryContent=".pdf"
              subText={result.pdfNotice ?? undefined}
              onClick={() => start('Pdf')}
            >
              PDF (print-ready)
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
        This file lists patients by name and UHID. The download is recorded in the audit log with your name. Keep it on
        a hospital computer, do not e-mail or print it unless it is needed for care or quality review, and delete it
        when you are done.
      </ConfirmDialog>
    </>
  );
}
