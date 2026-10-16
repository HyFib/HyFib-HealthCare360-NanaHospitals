import { Button, makeStyles, mergeClasses, Spinner, tokens, useId } from '@fluentui/react-components';
import { ArrowDownloadRegular, PrintRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import { useLanguage, useT } from '@/i18n';
import { downloadServerPdf } from '@/lib/pdf';
import { downloadPortalDocument, printPortalDocument, type PrintableDocument } from '@/lib/print';
import { useToast } from '@/lib/toast';
import { Notice } from './States';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '4px' },
  buttons: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  // Keeps the actions beside the page title on wide screens; the text wraps instead.
  note: { maxWidth: '400px' },
});

/**
 * The actions for a document the patient can keep.
 *
 * With `pdf` (the API path of the server's PDF of this document): "Download PDF", the hospital's official copy (the
 * document it prints and sends, with its labels in the patient's language: their record's preference, else the
 * language of the portal, which the API client sends), and "Print", the page's own copy in the patient's language.
 *
 * Without it (documents the server does not make, such as the privacy summary): "Print or save as PDF" and
 * "Download a copy", an HTML file of the page's copy.
 *
 * `quiet`: the page has a more important action (paying a bill that is due), so the download is not the primary button.
 */
export function DocumentActions({
  build,
  pdf,
  quiet = false,
}: {
  build: () => PrintableDocument;
  pdf?: string;
  quiet?: boolean;
}) {
  return pdf ? <OfficialPdfActions build={build} path={pdf} quiet={quiet} /> : <PageCopyActions build={build} />;
}

function OfficialPdfActions({ build, path, quiet }: { build: () => PrintableDocument; path: string; quiet: boolean }) {
  const styles = useStyles();
  const t = useT();
  const toast = useToast();
  const hintId = useId('pdf-hint');
  const [busy, setBusy] = useState(false);
  const [unavailable, setUnavailable] = useState(false);

  const download = async () => {
    setBusy(true);
    setUnavailable(false);
    try {
      const result = await downloadServerPdf(path, build().fileName);
      if (result.status === 'unavailable') {
        setUnavailable(true);
      } else {
        toast.success(t('document.pdfSaved'));
      }
    } catch (error) {
      toast.failure(error, t, 'document.pdfFailed');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.root}>
      <div className={styles.buttons}>
        {/* While busy the button stays focusable, so keyboard and screen-reader users keep their place. */}
        <Button
          appearance={quiet ? 'secondary' : 'primary'}
          size="large"
          icon={busy ? <Spinner size="tiny" aria-hidden /> : <ArrowDownloadRegular />}
          disabledFocusable={busy}
          aria-describedby={unavailable ? undefined : hintId}
          onClick={() => void download()}
        >
          {busy ? t('document.preparingPdf') : t('document.downloadPdf')}
        </Button>
        <Button
          appearance="secondary"
          size="large"
          icon={<PrintRegular />}
          onClick={() => printPortalDocument(build())}
        >
          {t('document.print')}
        </Button>
      </div>
      {unavailable ? (
        <div className={styles.note}>
          <Notice intent="warning" title={t('document.pdfUnavailable.title')}>
            {t('document.pdfUnavailable.body')}
          </Notice>
        </div>
      ) : (
        <p id={hintId} className={mergeClasses(styles.hint, styles.note)}>
          {t('document.pdfHint')}
        </p>
      )}
    </div>
  );
}

function PageCopyActions({ build }: { build: () => PrintableDocument }) {
  const styles = useStyles();
  const t = useT();
  const { language } = useLanguage();
  const toast = useToast();
  return (
    <div className={styles.root}>
      <div className={styles.buttons}>
        <Button appearance="secondary" icon={<PrintRegular />} onClick={() => printPortalDocument(build())}>
          {t('common.print')}
        </Button>
        <Button
          appearance="subtle"
          icon={<ArrowDownloadRegular />}
          onClick={() => {
            downloadPortalDocument(build(), language);
            toast.success(t('common.downloaded'));
          }}
        >
          {t('common.download')}
        </Button>
      </div>
      <p className={styles.hint}>{t('common.printHint')}</p>
    </div>
  );
}
