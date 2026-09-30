import { Button, Checkbox, makeStyles, mergeClasses, Text, Title1, tokens } from '@fluentui/react-components';
import { FullScreenMaximizeRegular, Speaker2Regular, SpeakerMuteRegular } from '@fluentui/react-icons';
import { auth, HOSPITAL_TIME_ZONE, RealtimeProvider, useRealtimeEvent, useSession } from '@hc360/core';
import { EmptyState, LoadingState } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, redirect, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useDepartments } from '@/lib/lookups';
import { LogoMark } from '@/shell/BrandMark';
import { frontOfficeQueries, type DisplayDepartment } from '@/modules/front-office/api';

interface DisplaySearch {
  departments?: string;
}

/** Waiting-area TV (SRS FO-05): big tokens, masked names, chime and optional voice announcement. */
export const Route = createFileRoute('/display/queue')({
  validateSearch: (search: Record<string, unknown>): DisplaySearch => ({
    departments:
      typeof search.departments === 'string' && /^[0-9a-f,-]+$/i.test(search.departments)
        ? search.departments
        : undefined,
  }),
  beforeLoad: async ({ location }) => {
    const { status } = useSession.getState();
    const signedIn = status === 'authenticated' || (status === 'unknown' && (await auth.restore()));
    if (!signedIn) {
      throw redirect({ to: '/login', search: { redirect: location.href } });
    }
  },
  component: DisplayRoot,
});

const useStyles = makeStyles({
  root: {
    minHeight: '100vh',
    backgroundColor: '#04151D',
    color: '#fff',
    display: 'flex',
    flexDirection: 'column',
    padding: '24px 32px',
    boxSizing: 'border-box',
    gap: '24px',
  },
  header: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '16px' },
  brand: { display: 'flex', alignItems: 'center', gap: '16px' },
  clock: { fontSize: '40px', fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  grid: {
    display: 'grid',
    gap: '24px',
    gridTemplateColumns: 'repeat(auto-fit, minmax(420px, 1fr))',
    flex: 1,
    alignContent: 'start',
  },
  card: {
    backgroundColor: '#0A2A38',
    borderRadius: '16px',
    padding: '20px 24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  dept: { fontSize: '28px', fontWeight: tokens.fontWeightSemibold, color: '#AAD5EA' },
  serving: { display: 'flex', flexDirection: 'column', gap: '10px' },
  tokenRow: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '16px',
    padding: '10px 16px',
    borderRadius: '12px',
    backgroundColor: '#0F3B4F',
  },
  flash: {
    animationName: { '0%': { backgroundColor: '#2F8BB4' }, '100%': { backgroundColor: '#0F3B4F' } },
    animationDuration: '2.5s',
    animationIterationCount: 3,
  },
  token: {
    fontSize: '72px',
    lineHeight: '80px',
    fontWeight: tokens.fontWeightBold,
    letterSpacing: '3px',
    fontVariantNumeric: 'tabular-nums',
  },
  room: { fontSize: '30px', color: '#DCF0F9' },
  name: { fontSize: '22px', color: '#92C7E1' },
  next: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'center', fontSize: '26px', color: '#C3E3F2' },
  nextToken: {
    padding: '4px 14px',
    borderRadius: '8px',
    border: '1px solid #4A9AC0',
    fontVariantNumeric: 'tabular-nums',
  },
  waiting: { fontSize: '20px', color: '#92C7E1' },
  controls: { display: 'flex', gap: '8px' },
  setup: {
    maxWidth: '640px',
    margin: '10vh auto',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '24px',
  },
});

function DisplayRoot() {
  return (
    <RealtimeProvider accessTokenFactory={() => useSession.getState().accessToken ?? ''} enabled>
      <DisplayBoard />
    </RealtimeProvider>
  );
}

function spokenToken(label: string): string {
  return label.replace(/[-\s]/g, '').split('').join(' ');
}

function chime(): void {
  try {
    const context = new AudioContext();
    [880, 660].forEach((frequency, index) => {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.frequency.value = frequency;
      oscillator.connect(gain).connect(context.destination);
      const start = context.currentTime + index * 0.35;
      gain.gain.setValueAtTime(0.25, start);
      gain.gain.exponentialRampToValueAtTime(0.001, start + 0.3);
      oscillator.start(start);
      oscillator.stop(start + 0.32);
    });
  } catch {
    // Audio may be blocked until the screen is clicked once; the visual flash still shows.
  }
}

function DisplayBoard() {
  const styles = useStyles();
  const { departments } = Route.useSearch();
  const ids = departments ? departments.split(',').filter(Boolean) : [];
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const queryClient = useQueryClient();
  const [voice, setVoice] = useState(() => localStorage.getItem('hc360:display-voice') !== 'off');
  const [flash, setFlash] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());
  const board = useQuery({ ...frontOfficeQueries.display(ids), enabled: ids.length > 0 });

  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(timer);
  }, []);

  // Keep the TV awake while the board is open.
  useEffect(() => {
    let lock: { release: () => Promise<void> } | undefined;
    void (
      navigator as Navigator & { wakeLock?: { request: (type: 'screen') => Promise<{ release: () => Promise<void> }> } }
    ).wakeLock
      ?.request('screen')
      .then((l) => (lock = l))
      .catch(() => undefined);
    return () => void lock?.release();
  }, []);

  if (ids.length === 0) {
    return <DisplaySetup />;
  }

  const announce = (payload: { label?: string; room?: string | null }) => {
    void queryClient.invalidateQueries({ queryKey: ['front-office', 'display'] });
    if (!payload.label) {
      return;
    }
    setFlash(payload.label);
    chime();
    if (voice && 'speechSynthesis' in window) {
      const text = `Token ${spokenToken(payload.label)}${payload.room ? `, please go to ${payload.room}` : ', please proceed'}`;
      const utterance = new SpeechSynthesisUtterance(text);
      utterance.lang = 'en-IN';
      utterance.rate = 0.9;
      window.speechSynthesis.speak(utterance);
    }
  };

  const toggleVoice = () => {
    setVoice((v) => {
      localStorage.setItem('hc360:display-voice', v ? 'off' : 'on');
      return !v;
    });
  };

  return (
    <div className={styles.root}>
      {ids.map((id) => (
        <QueueSubscription key={id} departmentId={id} onCalled={announce} />
      ))}
      <header className={styles.header}>
        <div className={styles.brand}>
          <LogoMark size={48} />
          <Title1 as="h1" style={{ color: '#fff' }}>
            {facilityName}
          </Title1>
        </div>
        <div className={styles.controls}>
          <span className={styles.clock}>
            {new Intl.DateTimeFormat('en-IN', {
              hour: '2-digit',
              minute: '2-digit',
              hour12: true,
              timeZone: HOSPITAL_TIME_ZONE,
            }).format(now)}
          </span>
          <Button
            appearance="transparent"
            icon={voice ? <Speaker2Regular /> : <SpeakerMuteRegular />}
            onClick={toggleVoice}
            aria-label={voice ? 'Turn voice off' : 'Turn voice on'}
            style={{ color: '#fff' }}
          />
          <Button
            appearance="transparent"
            icon={<FullScreenMaximizeRegular />}
            onClick={() => void document.documentElement.requestFullscreen?.()}
            aria-label="Full screen"
            style={{ color: '#fff' }}
          />
        </div>
      </header>

      {board.isLoading ? (
        <LoadingState label="Loading queue…" />
      ) : (
        <div className={styles.grid}>
          {(board.data ?? []).map((dept: DisplayDepartment) => (
            <section key={dept.departmentId} className={styles.card} aria-label={dept.departmentName}>
              <span className={styles.dept}>{dept.departmentName}</span>
              <div className={styles.serving} aria-live="assertive">
                {dept.nowServing.length === 0 && (
                  <Text className={styles.name}>Please wait. Your token will be called soon.</Text>
                )}
                {dept.nowServing.map((t) => (
                  <div key={t.label} className={mergeClasses(styles.tokenRow, flash === t.label && styles.flash)}>
                    <span className={styles.token}>{t.label}</span>
                    <span style={{ textAlign: 'right' }}>
                      <div className={styles.room}>{t.room ?? ''}</div>
                      <div className={styles.name}>{t.maskedName}</div>
                    </span>
                  </div>
                ))}
              </div>
              {dept.upNext.length > 0 && (
                <div className={styles.next}>
                  <span>Next:</span>
                  {dept.upNext.map((label) => (
                    <span key={label} className={styles.nextToken}>
                      {label}
                    </span>
                  ))}
                </div>
              )}
              <span className={styles.waiting}>{dept.waiting} waiting</span>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

function QueueSubscription({
  departmentId,
  onCalled,
}: {
  departmentId: string;
  onCalled: (payload: { label?: string; room?: string | null }) => void;
}) {
  const queryClient = useQueryClient();
  useRealtimeEvent<{ label?: string; room?: string | null }>(`queue:${departmentId}`, null, (payload, envelope) => {
    if (envelope.name === 'queue.called') {
      onCalled(payload);
    } else {
      void queryClient.invalidateQueries({ queryKey: ['front-office', 'display'] });
    }
  });
  return null;
}

function DisplaySetup() {
  const styles = useStyles();
  const navigate = useNavigate();
  const departments = useDepartments('Clinical');
  const [selected, setSelected] = useState<string[]>([]);

  return (
    <div className={styles.root}>
      <div className={styles.setup}>
        <Title1 as="h1" style={{ color: '#fff' }}>
          Queue display setup
        </Title1>
        <Text style={{ color: '#C3E3F2' }}>
          Choose the departments this screen shows. The choice is kept in the link, so you can bookmark it or set it as
          the kiosk start page.
        </Text>
        {departments.data?.length === 0 && <EmptyState title="No clinical departments configured" />}
        {departments.data?.map((d) => (
          <Checkbox
            key={d.id}
            label={<span style={{ color: '#fff' }}>{d.name}</span>}
            checked={selected.includes(d.id)}
            onChange={(_, data) => setSelected((s) => (data.checked ? [...s, d.id] : s.filter((x) => x !== d.id)))}
          />
        ))}
        <div>
          <Button
            appearance="primary"
            size="large"
            disabled={selected.length === 0}
            onClick={() => void navigate({ to: '/display/queue', search: { departments: selected.join(',') } })}
          >
            Start display
          </Button>
        </div>
      </div>
    </div>
  );
}
