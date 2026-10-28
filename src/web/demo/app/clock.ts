/**
 * The demo's clock: the morning its data was recorded, running on from there. The screens ask the server for "today"
 * by the browser's date, so the browser must think it is that day; timers and animation keep their own time.
 * Installed before any of the app's code runs, so every date the app makes is on this clock.
 */
export function startClockAt(instant: number): void {
  const RealDate = Date;
  const offset = instant - RealDate.now();
  const now = () => RealDate.now() + offset;

  // A function rather than a subclass, so that dates made before (and by Intl) are still instances of Date.
  function DemoDate(this: unknown, ...args: unknown[]): Date | string {
    if (!new.target) {
      // Date() without new: the time as text.
      return new RealDate(now()).toString();
    }
    return Reflect.construct(RealDate, args.length > 0 ? args : [now()], new.target) as Date;
  }
  DemoDate.prototype = RealDate.prototype;
  DemoDate.now = now;
  DemoDate.parse = RealDate.parse;
  DemoDate.UTC = RealDate.UTC;
  globalThis.Date = DemoDate as unknown as DateConstructor;
}
