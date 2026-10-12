import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SearchBox,
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  CopyRegular,
  DismissRegular,
  KeyResetRegular,
  LockOpenRegular,
  MoreHorizontalRegular,
  PhoneKeyRegular,
  SignOutRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, isApiError, normalizePhone, useDebouncedValue, useSession } from '@hc360/core';
import {
  applyServerErrors,
  ConfirmDialog,
  DataTable,
  FilterBar,
  LookupField,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { api } from '@/lib/api';
import { searchPractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { PasswordConfirmDialog } from '@/modules/account/PasswordConfirmDialog';
import { adminQueries, type UserDetail, type UserListItem } from '@/modules/admin/api';

export const Route = createFileRoute('/_app/admin/users')({
  component: UsersPage,
});

const useStyles = makeStyles({
  password: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase500,
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    letterSpacing: '1px',
    userSelect: 'all',
  },
  roles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: '2px 12px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type RowAction = 'reset-password' | 'unlock' | 'reset-mfa' | 'revoke-sessions';

const ACTION_TEXT: Record<Exclude<RowAction, 'reset-password'>, { title: string; body: string; done: string }> = {
  unlock: {
    title: 'Unlock this account?',
    body: 'The user can sign in again straight away.',
    done: 'Account unlocked',
  },
  'reset-mfa': {
    title: 'Reset two-step verification?',
    body: 'Use this when the user lost their phone. They will set up two-step verification again at next sign-in.',
    done: 'Two-step verification reset',
  },
  'revoke-sessions': {
    title: 'Sign this user out everywhere?',
    body: 'All their open sessions on every computer and tablet end immediately.',
    done: 'Signed out everywhere',
  },
};

function UsersPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('admin.users.manage');
  const canSignOutEveryone = usePermission('admin.system.manage');
  const [signingOutEveryone, setSigningOutEveryone] = useState(false);
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const [page, setPage] = useState(1);
  const [active, setActive] = useState<'all' | 'active' | 'inactive'>('active');
  const [roleId, setRoleId] = useState('');
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const [pending, setPending] = useState<{ user: UserListItem; action: RowAction } | null>(null);
  const [tempPassword, setTempPassword] = useState<TemporaryPassword | null>(null);
  const roles = useQuery(adminQueries.roles());
  const users = useQuery(
    adminQueries.users({
      search: search || undefined,
      page,
      active: active === 'all' ? undefined : active === 'active',
      roleId: roleId || undefined,
    }),
  );

  const act = useMutation({
    mutationFn: ({ user, action }: { user: UserListItem; action: RowAction }) =>
      api.post<{ temporaryPassword?: string; expiresAt?: string }>(`/api/v1/admin/users/${user.id}/${action}`),
    onSuccess: (result, { user, action }) => {
      setPending(null);
      if (action === 'reset-password' && result?.temporaryPassword) {
        setTempPassword({
          name: user.displayName,
          password: result.temporaryPassword,
          expiresAt: result.expiresAt ?? null,
        });
      } else if (action !== 'reset-password') {
        notify.success(ACTION_TEXT[action].done);
      }
      void queryClient.invalidateQueries({ queryKey: ['admin', 'users'] });
    },
    onError: (error) => notify.error(error, 'The action failed'),
  });

  // For a suspected break-in: everybody but this administrator signs in again (ASVS V7.4.5).
  const signOutEveryone = useMutation({
    mutationFn: (password: string) => api.post<{ ended: number }>('/api/v1/admin/sessions/end-all', { password }),
    onSuccess: ({ ended }) => {
      setSigningOutEveryone(false);
      notify.success(`Signed everybody out: ${ended} session(s) ended`);
    },
  });

  return (
    <Page>
      <PageHeader
        title="Users"
        subtitle="Staff accounts, their roles and sign-in security"
        actions={
          (canSignOutEveryone || canManage) && (
            <>
              {canSignOutEveryone && (
                <Button
                  icon={<SignOutRegular />}
                  onClick={() => {
                    signOutEveryone.reset();
                    setSigningOutEveryone(true);
                  }}
                >
                  Sign out everyone
                </Button>
              )}
              {canManage && (
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
                  Add user
                </Button>
              )}
            </>
          )
        }
      />
      <PasswordConfirmDialog
        open={signingOutEveryone}
        title="Sign out everyone?"
        confirmLabel="Sign out everyone"
        busy={signOutEveryone.isPending}
        error={signOutEveryone.error}
        onConfirm={(password) => signOutEveryone.mutate(password)}
        onCancel={() => setSigningOutEveryone(false)}
      >
        Every session ends immediately, staff and patient portal alike, on every computer, tablet and phone. Only you
        stay signed in. Use this when a password may have leaked or someone may have got in: everyone signs in again.
      </PasswordConfirmDialog>
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Name, user name, email or phone"
              />
            </Field>
            <Field label="Role">
              <Select
                value={roleId}
                onChange={(_, d) => {
                  setRoleId(d.value);
                  setPage(1);
                }}
              >
                <option value="">All roles</option>
                {roles.data?.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={active}
                onChange={(_, d) => {
                  setActive(d.value as typeof active);
                  setPage(1);
                }}
              >
                <option value="active">Active</option>
                <option value="inactive">Deactivated</option>
                <option value="all">All</option>
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Users"
          rows={users.data?.items}
          loading={users.isLoading}
          error={users.error}
          onRetry={() => void users.refetch()}
          getRowId={(u) => u.id}
          onRowClick={canManage ? (u) => setEditing(u.id) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Name',
              render: (u) => (
                <span>
                  <Text weight="semibold">{u.displayName}</Text>
                  <Caption1 block className={styles.muted}>
                    {u.userName}
                  </Caption1>
                </span>
              ),
            },
            { key: 'roles', header: 'Roles', render: (u) => u.roles.join(', ') || '—' },
            {
              key: 'mfa',
              header: 'Two-step',
              width: 110,
              priority: 'low',
              render: (u) =>
                u.mfaEnabled ? (
                  <Badge appearance="tint" color="success">
                    On
                  </Badge>
                ) : (
                  <Badge appearance="tint">Off</Badge>
                ),
            },
            {
              key: 'last',
              header: 'Last sign-in',
              priority: 'low',
              render: (u) => (u.lastLoginAt ? formatRelative(u.lastLoginAt) : 'Never'),
            },
            {
              key: 'status',
              header: 'Status',
              width: 120,
              render: (u) =>
                u.isLocked ? (
                  <StatusBadge status="Blocked" label="Locked" />
                ) : (
                  <StatusBadge
                    status={u.isActive ? 'Active' : 'Inactive'}
                    tone={u.isActive ? 'success' : 'informative'}
                  />
                ),
            },
          ]}
          rowActions={
            canManage
              ? (u) => (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<MoreHorizontalRegular />}
                        aria-label={`Actions for ${u.displayName}`}
                        {...restoreFocusTarget}
                      />
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItem
                          icon={<KeyResetRegular />}
                          onClick={() => setPending({ user: u, action: 'reset-password' })}
                        >
                          Reset password
                        </MenuItem>
                        {u.isLocked && (
                          <MenuItem
                            icon={<LockOpenRegular />}
                            onClick={() => setPending({ user: u, action: 'unlock' })}
                          >
                            Unlock
                          </MenuItem>
                        )}
                        {u.mfaEnabled && (
                          <MenuItem
                            icon={<PhoneKeyRegular />}
                            onClick={() => setPending({ user: u, action: 'reset-mfa' })}
                          >
                            Reset two-step verification
                          </MenuItem>
                        )}
                        <MenuItem
                          icon={<SignOutRegular />}
                          onClick={() => setPending({ user: u, action: 'revoke-sessions' })}
                        >
                          Sign out everywhere
                        </MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                )
              : undefined
          }
          emptyTitle="No users match"
          page={page}
          pageSize={25}
          totalCount={users.data?.totalCount}
          onPageChange={setPage}
        />
      </Section>

      {editing && (
        <UserDrawer
          userId={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onCreated={(created) => {
            setEditing(null);
            setTempPassword(created);
          }}
        />
      )}

      <ConfirmDialog
        open={pending !== null}
        title={
          pending?.action === 'reset-password' ? 'Reset password?' : pending ? ACTION_TEXT[pending.action].title : ''
        }
        confirmLabel="Continue"
        destructive={pending?.action === 'revoke-sessions'}
        busy={act.isPending}
        onConfirm={() => pending && act.mutate(pending)}
        onCancel={() => setPending(null)}
      >
        {pending?.action === 'reset-password'
          ? `A new temporary password is created for ${pending.user.displayName}. They must choose their own password at next sign-in, and their open sessions end.`
          : pending
            ? ACTION_TEXT[pending.action].body
            : null}
      </ConfirmDialog>

      <Dialog open={tempPassword !== null} onOpenChange={(_, d) => !d.open && setTempPassword(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Temporary password for {tempPassword?.name}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div className={styles.password}>{tempPassword?.password}</div>
              <MessageBar intent="warning">
                <MessageBarBody>
                  This is shown only once. Give it to the user in person. They will be asked to change it when they sign
                  in
                  {tempPassword?.expiresAt
                    ? `, and it stops working after ${formatDateTime(tempPassword.expiresAt)}`
                    : ''}
                  .
                </MessageBarBody>
              </MessageBar>
            </DialogContent>
            <DialogActions>
              <Button
                icon={<CopyRegular />}
                onClick={() => tempPassword && void navigator.clipboard?.writeText(tempPassword.password)}
              >
                Copy
              </Button>
              <Button appearance="primary" onClick={() => setTempPassword(null)}>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}

const userSchema = z.object({
  userName: z
    .string()
    .trim()
    .min(3, 'Use at least 3 characters.')
    .max(64)
    .regex(/^[a-zA-Z0-9._@-]+$/, 'Use letters, digits, dot, underscore, @ or hyphen.'),
  displayName: z.string().trim().min(1, 'Enter the full name.').max(200),
  email: z
    .string()
    .trim()
    .max(256)
    .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
  phone: z
    .string()
    .trim()
    .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
  roleIds: z.array(z.string()).min(1, 'Give the user at least one role.'),
  practitionerId: z.string().nullable(),
  temporaryPassword: z.string().max(128),
  isActive: z.boolean(),
});

type UserForm = z.infer<typeof userSchema>;

/** A temporary password just given out: shown once, with when it stops working. */
interface TemporaryPassword {
  name: string;
  password: string;
  expiresAt: string | null;
}

function UserDrawer({
  userId,
  onClose,
  onCreated,
}: {
  userId: string | null;
  onClose: () => void;
  onCreated: (created: TemporaryPassword) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const selfId = useSession((s) => s.user?.id);
  const roles = useQuery(adminQueries.roles());
  const existing = useQuery({ ...adminQueries.user(userId ?? ''), enabled: userId !== null });

  if (userId && !existing.data) {
    return (
      <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
        <DrawerBody>
          {existing.error ? (
            <MessageBar intent="error">
              <MessageBarBody>
                {isApiError(existing.error) ? existing.error.userMessage : 'Could not load the user.'}
              </MessageBarBody>
            </MessageBar>
          ) : (
            <Spinner label="Loading…" />
          )}
        </DrawerBody>
      </OverlayDrawer>
    );
  }

  return (
    <UserDrawerForm
      user={existing.data ?? null}
      roles={roles.data ?? []}
      isSelf={existing.data?.id === selfId}
      onClose={onClose}
      onCreated={onCreated}
      onSaved={() => {
        notify.success('User saved');
        void queryClient.invalidateQueries({ queryKey: ['admin'] });
        onClose();
      }}
      styles={styles}
    />
  );
}

function UserDrawerForm({
  user,
  roles,
  isSelf,
  onClose,
  onCreated,
  onSaved,
  styles,
}: {
  user: UserDetail | null;
  roles: { id: string; name: string; description: string | null }[];
  isSelf: boolean;
  onClose: () => void;
  onCreated: (created: TemporaryPassword) => void;
  onSaved: () => void;
  styles: ReturnType<typeof useStyles>;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<UserForm>({
    resolver: zodResolver(userSchema),
    defaultValues: {
      userName: user?.userName ?? '',
      displayName: user?.displayName ?? '',
      email: user?.email ?? '',
      phone: user?.phone ?? '',
      roleIds: user?.roleIds ?? [],
      practitionerId: user?.practitionerId ?? null,
      temporaryPassword: '',
      isActive: user?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: UserForm) => {
      const common = {
        displayName: values.displayName,
        email: values.email || null,
        phone: values.phone || null,
        roleIds: values.roleIds,
        facilityIds: user?.facilityIds ?? null,
        defaultFacilityId: user?.defaultFacilityId ?? null,
        practitionerId: values.practitionerId,
      };
      return user
        ? api.put(`/api/v1/admin/users/${user.id}`, { ...common, isActive: values.isActive, version: user.version })
        : api.post<{ userId: string; temporaryPassword: string; expiresAt: string }>('/api/v1/admin/users', {
            ...common,
            userName: values.userName,
            temporaryPassword: values.temporaryPassword || null,
          });
    },
    onSuccess: (result, values) => {
      if (!user && result && typeof result === 'object' && 'temporaryPassword' in result) {
        void queryClient.invalidateQueries({ queryKey: ['admin'] });
        const created = result as { temporaryPassword: string; expiresAt: string };
        onCreated({ name: values.displayName, password: created.temporaryPassword, expiresAt: created.expiresAt });
      } else {
        onSaved();
      }
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the user');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {user ? user.displayName : 'Add user'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <TextField
              control={form.control}
              name="userName"
              label="User name"
              required
              disabled={Boolean(user)}
              autoComplete="off"
              hint={user ? 'User names cannot be changed.' : 'Used to sign in, e.g. priya.s'}
            />
            <TextField
              control={form.control}
              name="displayName"
              label="Full name"
              required
              hint="As shown on screens and prints, e.g. Dr. Priya Sharma"
            />
            <TextField control={form.control} name="email" label="Email" type="email" />
            <TextField control={form.control} name="phone" label="Mobile" type="tel" />
            <Controller
              control={form.control}
              name="roleIds"
              render={({ field, fieldState }) => (
                <Field
                  label="Roles"
                  required
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <div className={styles.roles}>
                    {roles.map((role) => (
                      <Checkbox
                        key={role.id}
                        label={role.name}
                        checked={field.value.includes(role.id)}
                        onChange={(_, d) =>
                          field.onChange(
                            d.checked ? [...field.value, role.id] : field.value.filter((id) => id !== role.id),
                          )
                        }
                      />
                    ))}
                  </div>
                </Field>
              )}
            />
            <LookupField
              control={form.control}
              name="practitionerId"
              label="Linked doctor / practitioner"
              load={searchPractitioners}
              hint="Link doctors so their queue, orders and fees follow their sign-in."
            />
            {!user && (
              <TextField
                control={form.control}
                name="temporaryPassword"
                label="Temporary password"
                type="password"
                autoComplete="new-password"
                hint="Leave blank to generate a strong one. The user must change it at first sign-in."
              />
            )}
            {user && (
              <Controller
                control={form.control}
                name="isActive"
                render={({ field }) => (
                  <Switch
                    checked={field.value}
                    onChange={(_, d) => field.onChange(d.checked)}
                    label="Account is active"
                    disabled={isSelf}
                  />
                )}
              />
            )}
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button type="submit" appearance="primary" disabled={save.isPending}>
            {user ? 'Save' : 'Create user'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
