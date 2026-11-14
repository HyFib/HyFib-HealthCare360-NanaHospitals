import { useEffect, useState } from 'react';

/** The current time, refreshed every `everyMs`, so a wait that ends on its own re-enables the screen. */
export function useNow(everyMs = 15_000): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}
