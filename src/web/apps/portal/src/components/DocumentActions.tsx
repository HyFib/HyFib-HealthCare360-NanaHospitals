import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowDownloadRegular, PrintRegular } from '@fluentui/react-icons';
import { useT } from '@/i18n';
import { downloadPortalDocument, printPortalDocument, type PrintableDocument } from '@/lib/print';
import { useToast } from '@/lib/toast';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '4px' },
  buttons: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

/** "Print or save as PDF" and "Download a copy" for any document the patient can keep. */
export function DocumentActions({ build }: { build: () => PrintableDocument }) {
  const styles = useStyles();
  const t = useT();
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
            downloadPortalDocument(build());
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
