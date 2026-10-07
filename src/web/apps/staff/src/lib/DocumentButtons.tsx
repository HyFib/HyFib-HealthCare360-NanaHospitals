import { Button, Spinner, Tooltip, type ButtonProps } from '@fluentui/react-components';
import { ArrowDownloadRegular, PrintRegular } from '@fluentui/react-icons';
import { useServerDocuments } from './documents';

/**
 * Print and Download PDF for one document the server makes (see documents.ts). Both give the same document, so a
 * screen offers them side by side where it used to have only a print button.
 */
export function DocumentButtons({
  path,
  printLabel = 'Print',
  printHint,
  printAppearance = 'secondary',
  disabled = false,
}: {
  /** The document's resource path, from the module's api.ts. */
  path: string;
  printLabel?: string;
  /** What the document contains, shown as the print button's tooltip. */
  printHint?: string;
  printAppearance?: ButtonProps['appearance'];
  disabled?: boolean;
}) {
  const { print, downloadPdf } = useServerDocuments();
  // While its request runs, each button stays focusable (aria-disabled) and ignores clicks: a disabled button drops
  // keyboard focus to the page, or out of an open dialog.
  const printButton = (
    <Button
      appearance={printAppearance}
      icon={print.isPending ? <Spinner size="tiny" /> : <PrintRegular />}
      disabled={disabled}
      disabledFocusable={print.isPending}
      onClick={() => print.mutate(path)}
    >
      {printLabel}
    </Button>
  );

  return (
    <>
      {printHint ? (
        <Tooltip content={printHint} relationship="description">
          {printButton}
        </Tooltip>
      ) : (
        printButton
      )}
      <Button
        icon={downloadPdf.isPending ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
        disabled={disabled}
        disabledFocusable={downloadPdf.isPending}
        onClick={() => downloadPdf.mutate(path)}
      >
        Download PDF
      </Button>
    </>
  );
}
