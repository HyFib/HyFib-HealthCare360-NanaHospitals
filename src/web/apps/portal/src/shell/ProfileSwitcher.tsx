import {
  Avatar,
  Button,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  mergeClasses,
  OverlayDrawer,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, ChevronDownRegular, DismissRegular } from '@fluentui/react-icons';
import { useToast } from '@/lib/toast';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { firstName } from '@/i18n/format';
import { useT } from '@/i18n';
import { useMediaQuery, WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import { personLine } from '@/lib/people';
import { switchProfile } from '@/lib/session';
import type { PortalProfile } from '@/lib/types';

const useStyles = makeStyles({
  trigger: { minHeight: '44px', maxWidth: '200px', paddingInline: '6px' },
  triggerName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '110px' },
  drawer: { '@media (max-width: 899px)': { maxHeight: '85dvh', borderTopLeftRadius: tokens.borderRadiusXLarge, borderTopRightRadius: tokens.borderRadiusXLarge } },
  hint: { margin: '0 0 12px', color: tokens.colorNeutralForeground2 },
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  option: {
    width: '100%',
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    minHeight: '64px',
    padding: '10px 12px',
    boxSizing: 'border-box',
    textAlign: 'start',
    fontFamily: 'inherit',
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  current: { border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  optionText: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  name: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  detail: { fontSize: tokens.fontSizeBase300, color: tokens.colorNeutralForeground2 },
  check: { color: tokens.colorBrandForeground1, fontSize: '24px', flexShrink: 0 },
});

/**
 * "Whose records?" — always in the header. Families in India often share one mobile number, so one sign-in covers
 * everyone registered with it; the patient picks whose appointments, reports and bills to see.
 */
export function ProfileSwitcher({ profiles, active }: { profiles: PortalProfile[]; active: PortalProfile }) {
  const styles = useStyles();
  const t = useT();
  const [open, setOpen] = useState(false);
  const wide = useMediaQuery(WIDE_LAYOUT_QUERY);

  return (
    <>
      <Button
        appearance="subtle"
        size="large"
        className={styles.trigger}
        icon={<Avatar name={active.fullName} size={32} color="colorful" aria-hidden />}
        iconPosition="before"
        aria-label={t('shell.profile.button', { name: active.fullName })}
        aria-haspopup="dialog"
        onClick={() => setOpen(true)}
      >
        <span className={styles.triggerName}>{firstName(active.fullName)}</span>
        <ChevronDownRegular aria-hidden />
      </Button>
      <OverlayDrawer
        open={open}
        onOpenChange={(_, data) => setOpen(data.open)}
        position={wide ? 'end' : 'bottom'}
        size={wide ? 'small' : 'medium'}
        className={styles.drawer}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" size="large" icon={<DismissRegular />} aria-label={t('common.close')} onClick={() => setOpen(false)} />}
          >
            {t('shell.profile.title')}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <ProfileList profiles={profiles} activeId={active.id} onDone={() => setOpen(false)} />
        </DrawerBody>
      </OverlayDrawer>
    </>
  );
}

/** The family list with the current person marked; choosing someone else switches every screen to them. */
export function ProfileList({ profiles, activeId, onDone }: { profiles: PortalProfile[]; activeId: string | null; onDone?: () => void }) {
  const styles = useStyles();
  const t = useT();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const toast = useToast();
  const [switching, setSwitching] = useState<string | null>(null);

  const choose = async (profile: PortalProfile) => {
    if (profile.id === activeId) {
      onDone?.();
      return;
    }
    setSwitching(profile.id);
    try {
      await switchProfile(queryClient, profile.id);
      onDone?.();
      if (activeId) {
        // The page on screen belonged to the previous person; start again from their home.
        void navigate({ to: '/home' });
        toast.info(t('shell.profile.switched', { name: profile.fullName }));
      }
    } finally {
      setSwitching(null);
    }
  };

  return (
    <>
      <p className={styles.hint}>{t('shell.profile.hint')}</p>
      <ul className={styles.list}>
        {profiles.map((profile) => {
          const current = profile.id === activeId;
          return (
            <li key={profile.id}>
              <button
                type="button"
                className={mergeClasses(styles.option, current && styles.current)}
                aria-current={current ? 'true' : undefined}
                disabled={switching !== null}
                onClick={() => void choose(profile)}
              >
                <Avatar name={profile.fullName} size={40} color="colorful" aria-hidden />
                <span className={styles.optionText}>
                  <span className={styles.name}>{profile.fullName}</span>
                  <span className={styles.detail}>{[personLine(profile, t), `${t('common.uhid')} ${profile.uhid}`].filter(Boolean).join(' · ')}</span>
                </span>
                {switching === profile.id ? (
                  <Spinner size="tiny" />
                ) : (
                  current && <CheckmarkCircleRegular className={styles.check} aria-label={t('shell.profile.current')} />
                )}
              </button>
            </li>
          );
        })}
      </ul>
    </>
  );
}
