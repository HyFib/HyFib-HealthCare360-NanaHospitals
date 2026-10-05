import {
  Avatar,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuDivider,
  MenuGroup,
  MenuGroupHeader,
  MenuItem,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  BuildingRegular,
  DarkThemeRegular,
  LockClosedRegular,
  PersonAccountsRegular,
  PrintRegular,
  SignOutRegular,
} from '@fluentui/react-icons';
import { auth, humanize, isDesktop, useSession, type AuthResult } from '@hc360/core';
import { useNotify, useThemeMode, type ThemeMode } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { api } from '@/lib/api';

const useStyles = makeStyles({
  header: { display: 'flex', flexDirection: 'column', padding: '8px 12px 6px', maxWidth: '280px' },
  muted: { color: tokens.colorNeutralForeground3 },
  trigger: { minWidth: 'auto', padding: '2px' },
});

const THEMES: { value: ThemeMode; label: string }[] = [
  { value: 'system', label: 'Same as Windows' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'high-contrast', label: 'High contrast' },
];

export function UserMenu({ onLock }: { onLock: () => void }) {
  const styles = useStyles();
  const user = useSession((s) => s.user);
  const [themeMode, setThemeMode] = useThemeMode();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const notify = useNotify();

  if (!user) {
    return null;
  }

  const switchFacility = async (facilityId: string) => {
    try {
      auth.apply(await api.post<AuthResult>('/api/v1/auth/switch-facility', { facilityId }));
      queryClient.clear();
      await navigate({ to: '/' });
    } catch (error) {
      notify.error(error, 'Could not switch facility');
    }
  };

  const signOut = async () => {
    await auth.logout();
    queryClient.clear();
    await navigate({ to: '/login', search: {} });
  };

  return (
    <Menu positioning="below-end" hasCheckmarks>
      <MenuTrigger disableButtonEnhancement>
        <Button appearance="subtle" className={styles.trigger} aria-label={`Account menu for ${user.displayName}`}>
          <Avatar name={user.displayName} size={32} color="colorful" />
        </Button>
      </MenuTrigger>
      <MenuPopover>
        <div className={styles.header}>
          <Text weight="semibold">{user.displayName}</Text>
          <Caption1 className={styles.muted}>{user.roles.map(humanize).join(', ') || user.userName}</Caption1>
          {user.facilityName && <Caption1 className={styles.muted}>{user.facilityName}</Caption1>}
        </div>
        <MenuDivider />
        <MenuList
          checkedValues={{ theme: [themeMode], facility: user.facilityId ? [user.facilityId] : [] }}
          onCheckedValueChange={(_, data) => {
            if (data.name === 'theme' && data.checkedItems[0]) {
              setThemeMode(data.checkedItems[0] as ThemeMode);
            }
            if (data.name === 'facility' && data.checkedItems[0] && data.checkedItems[0] !== user.facilityId) {
              void switchFacility(data.checkedItems[0]);
            }
          }}
        >
          <MenuItem icon={<PersonAccountsRegular />} onClick={() => void navigate({ to: '/account' })}>
            My account and security
          </MenuItem>
          <MenuItem icon={<LockClosedRegular />} secondaryContent="Ctrl+Shift+L" onClick={onLock}>
            Lock screen
          </MenuItem>
          {isDesktop && (
            <MenuItem icon={<PrintRegular />} onClick={() => void navigate({ to: '/workstation/printers' })}>
              Printers on this computer
            </MenuItem>
          )}
          {user.facilities.length > 1 && (
            <>
              <MenuDivider />
              <MenuGroup>
                <MenuGroupHeader>
                  <BuildingRegular /> Facility
                </MenuGroupHeader>
                {user.facilities.map((facility) => (
                  <MenuItemRadio key={facility.id} name="facility" value={facility.id}>
                    {facility.name}
                  </MenuItemRadio>
                ))}
              </MenuGroup>
            </>
          )}
          <MenuDivider />
          <MenuGroup>
            <MenuGroupHeader>
              <DarkThemeRegular /> Appearance
            </MenuGroupHeader>
            {THEMES.map((theme) => (
              <MenuItemRadio key={theme.value} name="theme" value={theme.value}>
                {theme.label}
              </MenuItemRadio>
            ))}
          </MenuGroup>
          <MenuDivider />
          <MenuItem icon={<SignOutRegular />} onClick={() => void signOut()}>
            Sign out
          </MenuItem>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
