import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Badge,
  Button,
  Caption1,
  Checkbox,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SearchBox,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { humanize } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { adminQueries, type PermissionGroup, type RoleDetail } from '@/modules/admin/api';

export const Route = createFileRoute('/_app/admin/roles')({
  component: RolesPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  permission: { display: 'flex', flexDirection: 'column', paddingLeft: '4px' },
  groupHeader: { display: 'flex', justifyContent: 'space-between', width: '100%', gap: '8px', alignItems: 'center' },
});

function RolesPage() {
  const canManage = usePermission('admin.roles.manage');
  const roles = useQuery(adminQueries.roles());
  const [editing, setEditing] = useState<string | 'new' | null>(null);

  return (
    <Page>
      <PageHeader
        title="Roles and permissions"
        subtitle="A role is a named set of permissions. Give users roles, not individual permissions."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              New role
            </Button>
          )
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Roles"
          rows={roles.data}
          loading={roles.isLoading}
          error={roles.error}
          onRetry={() => void roles.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => setEditing(r.id)}
          columns={[
            {
              key: 'name',
              header: 'Role',
              render: (r) => (
                <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
                  <Text weight="semibold">{r.name}</Text>
                  {r.isSystem && (
                    <Badge appearance="outline" size="small">
                      Built-in
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'description', header: 'Description', priority: 'low', render: (r) => r.description ?? '—' },
            { key: 'permissions', header: 'Permissions', width: 120, render: (r) => r.permissionCount },
            { key: 'users', header: 'Users', width: 90, render: (r) => r.userCount },
          ]}
        />
      </Section>
      {editing && <RoleDrawer roleId={editing === 'new' ? null : editing} readOnly={!canManage} onClose={() => setEditing(null)} />}
    </Page>
  );
}

function RoleDrawer({ roleId, readOnly, onClose }: { roleId: string | null; readOnly: boolean; onClose: () => void }) {
  const role = useQuery({ ...adminQueries.role(roleId ?? ''), enabled: roleId !== null });
  const groups = useQuery(adminQueries.permissions());
  const error = role.error ?? groups.error;
  const ready = (roleId === null || role.data) && groups.data;

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      {ready ? (
        <RoleForm role={role.data ?? null} groups={groups.data} readOnly={readOnly} onClose={onClose} />
      ) : (
        <DrawerBody>{error ? <ErrorState error={error} title="Could not load the role" /> : <Spinner label="Loading…" />}</DrawerBody>
      )}
    </OverlayDrawer>
  );
}

function RoleForm({ role, groups, readOnly, onClose }: { role: RoleDetail | null; groups: PermissionGroup[]; readOnly: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [name, setName] = useState(role?.name ?? '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [selected, setSelected] = useState<Set<string>>(() => new Set(role?.permissions ?? []));
  const [filter, setFilter] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const isWildcard = selected.has('*');
  const locked = readOnly || isWildcard;

  const visibleGroups = useMemo(() => {
    const term = filter.trim().toLowerCase();
    if (!term) {
      return groups;
    }
    return groups
      .map((g) => ({ ...g, permissions: g.permissions.filter((p) => `${p.name} ${p.description} ${g.group}`.toLowerCase().includes(term)) }))
      .filter((g) => g.permissions.length > 0);
  }, [groups, filter]);

  const toggle = (permission: string, on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (on) {
        next.add(permission);
      } else {
        next.delete(permission);
      }
      return next;
    });

  const toggleGroup = (group: PermissionGroup, on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      group.permissions.forEach((p) => (on ? next.add(p.name) : next.delete(p.name)));
      return next;
    });

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: ['admin'] });

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim() || null, permissions: [...selected], version: role?.version ?? null };
      return role ? api.put(`/api/v1/admin/roles/${role.id}`, body) : api.post('/api/v1/admin/roles', body);
    },
    onSuccess: () => {
      notify.success('Role saved', 'Users with this role get the new permissions within two minutes.');
      invalidate();
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not save the role'),
  });

  const remove = useMutation({
    mutationFn: () => api.delete(`/api/v1/admin/roles/${role!.id}`),
    onSuccess: () => {
      notify.success('Role deleted');
      invalidate();
      onClose();
    },
    onError: (error) => {
      setConfirmDelete(false);
      notify.error(error, 'Could not delete the role');
    },
  });

  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
          {role ? role.name : 'New role'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <Field label="Name" required>
            <Input value={name} onChange={(_, d) => setName(d.value)} disabled={readOnly || role?.isSystem} maxLength={100} />
          </Field>
          <Field label="Description">
            <Textarea value={description} onChange={(_, d) => setDescription(d.value)} disabled={readOnly} rows={2} maxLength={500} />
          </Field>
          {isWildcard ? (
            <MessageBar intent="info">
              <MessageBarBody>This role has every permission, including ones added in future updates.</MessageBarBody>
            </MessageBar>
          ) : (
            <>
              <Field label={`Permissions (${selected.size} selected)`}>
                <SearchBox value={filter} onChange={(_, d) => setFilter(d.value)} placeholder="Filter permissions" />
              </Field>
              <Accordion multiple collapsible>
                {visibleGroups.map((group) => {
                  const count = group.permissions.filter((p) => selected.has(p.name)).length;
                  return (
                    <AccordionItem key={group.group} value={group.group}>
                      <AccordionHeader>
                        <span className={styles.groupHeader}>
                          <Text weight="semibold">{humanize(group.group)}</Text>
                          <Caption1 className={styles.muted}>
                            {count} of {group.permissions.length}
                          </Caption1>
                        </span>
                      </AccordionHeader>
                      <AccordionPanel>
                        {!locked && (
                          <Checkbox
                            label="Select all"
                            checked={count === group.permissions.length ? true : count > 0 ? 'mixed' : false}
                            onChange={(_, d) => toggleGroup(group, d.checked === true)}
                          />
                        )}
                        {group.permissions.map((p) => (
                          <Checkbox
                            key={p.name}
                            disabled={locked}
                            checked={selected.has(p.name)}
                            onChange={(_, d) => toggle(p.name, d.checked === true)}
                            label={
                              <span className={styles.permission}>
                                <Text>{p.description}</Text>
                                <Caption1 className={styles.muted}>{p.name}</Caption1>
                              </span>
                            }
                          />
                        ))}
                      </AccordionPanel>
                    </AccordionItem>
                  );
                })}
              </Accordion>
            </>
          )}
        </div>
      </DrawerBody>
      {!readOnly && (
        <DrawerFooter>
          <Button appearance="primary" disabled={!name.trim() || save.isPending || isWildcard} onClick={() => save.mutate()}>
            Save
          </Button>
          <Button onClick={onClose}>Cancel</Button>
          {role && !role.isSystem && (
            <Button appearance="subtle" icon={<DeleteRegular />} onClick={() => setConfirmDelete(true)} style={{ marginLeft: 'auto' }}>
              Delete
            </Button>
          )}
        </DrawerFooter>
      )}
      <ConfirmDialog
        open={confirmDelete}
        title={`Delete the role "${role?.name}"?`}
        confirmLabel="Delete"
        destructive
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onCancel={() => setConfirmDelete(false)}
      >
        Users who have only this role will lose access to the screens it allowed.
      </ConfirmDialog>
    </>
  );
}
