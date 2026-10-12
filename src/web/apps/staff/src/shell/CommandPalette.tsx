import {
  Caption1,
  Dialog,
  DialogSurface,
  Input,
  makeStyles,
  mergeClasses,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowRightRegular, PersonRegular, SearchRegular } from '@fluentui/react-icons';
import { useDebouncedValue, useSession, type PagedResult } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useId, useMemo, useState, type KeyboardEvent } from 'react';
import { api } from '@/lib/api';
import { useFeatureSwitches } from '@/lib/features';
import { moduleManifests } from '@/lib/moduleRegistry';
import { visibleModules, type NavLink } from '@/lib/modules';
import { usePermission } from '@/lib/permissions';
import type { PatientListItem } from '@/modules/patients/api';

interface PaletteItem {
  id: string;
  kind: 'screen' | 'patient';
  title: string;
  subtitle: string;
  to: string;
}

const useStyles = makeStyles({
  surface: {
    padding: 0,
    maxWidth: '640px',
    width: 'calc(100vw - 32px)',
    overflow: 'hidden',
    alignSelf: 'flex-start',
    marginTop: '10vh',
  },
  input: {
    width: '100%',
    border: 'none',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: 0,
    padding: '8px 12px',
  },
  list: { maxHeight: '60vh', overflowY: 'auto', padding: '6px', margin: 0, listStyle: 'none' },
  group: { padding: '8px 10px 4px', color: tokens.colorNeutralForeground3 },
  option: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
  },
  // The selected background takes the selected text colours: in the high-contrast theme that background is the
  // highlight colour (cyan), on which the usual white text cannot be read.
  active: { backgroundColor: tokens.colorNeutralBackground1Selected, color: tokens.colorNeutralForeground1Selected },
  optionText: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  activeMuted: { color: tokens.colorNeutralForeground3Selected },
  footer: {
    display: 'flex',
    gap: '16px',
    padding: '8px 14px',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
  },
  empty: { padding: '24px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
});

function matches(link: NavLink & { module: string }, term: string): boolean {
  const haystack = [link.label, link.module, ...(link.keywords ?? [])].join(' ').toLowerCase();
  return term
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => haystack.includes(word));
}

/**
 * Ctrl+K: jump to any screen the user may open, or find a patient by UHID, mobile number or name
 * (SRS UX-03: any record within 3 keystrokes).
 */
export function CommandPalette({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const user = useSession((s) => s.user);
  const features = useFeatureSwitches();
  const canSearchPatients = usePermission('patients.read');
  const [term, setTerm] = useState('');
  const [active, setActive] = useState(0);
  const debounced = useDebouncedValue(term.trim(), 250);
  const listId = useId();

  const screens = useMemo(
    () =>
      visibleModules(user, moduleManifests, features).flatMap((module) =>
        [...module.links, ...(module.quickActions ?? [])].map((link) => ({ ...link, module: module.title })),
      ),
    [user, features],
  );

  const patients = useQuery({
    queryKey: ['palette', 'patients', debounced],
    queryFn: ({ signal }) =>
      api.get<PagedResult<PatientListItem>>('/api/v1/patients', { query: { search: debounced, pageSize: 6 }, signal }),
    enabled: open && canSearchPatients && debounced.length >= 3,
    staleTime: 10_000,
  });

  const items = useMemo<PaletteItem[]>(() => {
    const screenItems = screens
      .filter((link) => !term.trim() || matches(link, term.trim()))
      .slice(0, term.trim() ? 8 : 6)
      .map((link) => ({
        id: `screen:${link.to}:${link.label}`,
        kind: 'screen' as const,
        title: link.label,
        subtitle: link.module,
        to: link.to,
      }));
    const patientItems = (patients.data?.items ?? []).map((p) => ({
      id: `patient:${p.id}`,
      kind: 'patient' as const,
      title: p.fullName,
      subtitle: [p.uhid, p.age && `${p.age} ${p.gender.charAt(0)}`, p.phoneMasked].filter(Boolean).join(' · '),
      to: `/patients/${p.id}`,
    }));
    return [...patientItems, ...screenItems];
  }, [screens, term, patients.data]);

  const close = () => {
    onOpenChange(false);
    setTerm('');
    setActive(0);
  };

  const go = (item: PaletteItem | undefined) => {
    if (!item) {
      return;
    }
    close();
    void navigate({ href: item.to });
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActive((index) => Math.min(index + 1, items.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive((index) => Math.max(index - 1, 0));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      go(items[active]);
    }
  };

  const optionId = (index: number) => `${listId}-option-${index}`;
  const firstScreen = items.findIndex((item) => item.kind === 'screen');

  return (
    <Dialog open={open} onOpenChange={(_, data) => (data.open ? onOpenChange(true) : close())}>
      <DialogSurface className={styles.surface} aria-label="Search">
        <Input
          className={styles.input}
          size="large"
          appearance="underline"
          placeholder={
            canSearchPatients ? 'Search patients by UHID, mobile or name, or jump to a screen' : 'Jump to a screen'
          }
          contentBefore={<SearchRegular />}
          contentAfter={patients.isFetching ? <Spinner size="tiny" aria-hidden /> : undefined}
          value={term}
          onChange={(_, data) => {
            setTerm(data.value);
            setActive(0);
          }}
          onKeyDown={onKeyDown}
          autoFocus
          input={{
            role: 'combobox',
            'aria-expanded': true,
            'aria-controls': listId,
            'aria-activedescendant': items[active] ? optionId(active) : undefined,
            'aria-autocomplete': 'list',
          }}
        />
        <ul className={styles.list} id={listId} role="listbox" aria-label="Results">
          {items.length === 0 && (
            <li className={styles.empty} role="presentation">
              {debounced.length >= 3 && patients.isFetching
                ? 'Searching…'
                : 'No matches. Try a UHID, a 10-digit mobile number or part of a name.'}
            </li>
          )}
          {items.map((item, index) => (
            <li key={item.id} role="presentation">
              {index === 0 && item.kind === 'patient' && (
                <Caption1 as="p" className={styles.group}>
                  Patients
                </Caption1>
              )}
              {index === firstScreen && (
                <Caption1 as="p" className={styles.group}>
                  Screens
                </Caption1>
              )}
              <div
                id={optionId(index)}
                role="option"
                aria-selected={index === active}
                className={mergeClasses(styles.option, index === active && styles.active)}
                onMouseEnter={() => setActive(index)}
                onClick={() => go(item)}
              >
                {item.kind === 'patient' ? <PersonRegular fontSize={20} /> : <ArrowRightRegular fontSize={20} />}
                <span className={styles.optionText}>
                  <Text truncate wrap={false}>
                    {item.title}
                  </Text>
                  <Caption1
                    className={mergeClasses(styles.muted, index === active && styles.activeMuted)}
                    truncate
                    wrap={false}
                  >
                    {item.subtitle}
                  </Caption1>
                </span>
              </div>
            </li>
          ))}
        </ul>
        <div className={styles.footer}>
          <Caption1>↑ ↓ to move</Caption1>
          <Caption1>Enter to open</Caption1>
          <Caption1>Esc to close</Caption1>
        </div>
      </DialogSurface>
    </Dialog>
  );
}
