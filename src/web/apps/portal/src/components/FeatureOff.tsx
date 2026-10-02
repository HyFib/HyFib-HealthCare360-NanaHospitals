import { Button, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowClockwiseRegular, BuildingRegular, QrCodeRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useState } from 'react';
import { TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { useT } from '@/i18n';
import { usePortalFeatures } from '@/lib/features';
import { PortalFrame } from '@/shell/PortalFrame';
import { Notice } from './States';

const useStyles = makeStyles({
  box: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', textAlign: 'center' },
  still: { color: tokens.colorNeutralForeground2, maxWidth: '440px' },
  link: { display: 'inline-flex', alignItems: 'center', gap: '8px' },
});

/**
 * What a patient sees while the hospital has turned the portal off, instead of the sign-in or any signed-in page: no
 * error, what to do, and what still works. "Try again" asks the hospital's server once more.
 */
export function PortalUnavailable() {
  const styles = useStyles();
  const t = useT();
  const { recheck } = usePortalFeatures();
  const [checking, setChecking] = useState(false);

  return (
    <div className={styles.box}>
      <EmptyState
        icon={<BuildingRegular />}
        title={t('features.portalOff.title')}
        description={t('features.portalOff.body')}
      />
      <Text className={styles.still}>{t('features.portalOff.stillWorks')}</Text>
      <span className={styles.link}>
        <QrCodeRegular aria-hidden />
        <TextLink to="/verify">{t('nav.verify')}</TextLink>
      </span>
      <Button
        icon={<ArrowClockwiseRegular />}
        disabled={checking}
        onClick={() => {
          setChecking(true);
          void recheck().finally(() => setChecking(false));
        }}
      >
        {t('common.retry')}
      </Button>
    </div>
  );
}

/** The plain frame around `PortalUnavailable`, for pages that would otherwise need a signed-in patient. */
export function PortalUnavailablePage() {
  const t = useT();
  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <PortalPage title={t('app.name')} width="narrow">
        <PortalUnavailable />
      </PortalPage>
    </PortalFrame>
  );
}

/** Where a video consultation would be, while the hospital has turned video consultations off. */
export function VideoUnavailable() {
  const t = useT();
  return (
    <Notice intent="info" title={t('features.videoOff.title')}>
      {t('features.videoOff.body')}
    </Notice>
  );
}
