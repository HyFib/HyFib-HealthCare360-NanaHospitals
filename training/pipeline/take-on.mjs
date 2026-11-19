// The take-on a hospital does when a department goes live (docs/deployment/MODULE-ROLLOUT-PLAN.md, section 5), done
// on the training server so the videos show what staff will see on their first day: leave balances credited for the
// year, and the like. Run after accounts.mjs, before the snapshot the videos start from:
//
//   server.sh restore story && node training/pipeline/take-on.mjs && server.sh snapshot morning
//
// Each item is done through the API by the role that does it at the hospital.

import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { Api, serverNow } = await import(pathToFileURL(join(ROOT, 'src/web/demo/lib/api.mjs')).href);

async function step(name, run) {
  try {
    const result = await run();
    console.log(`✓ ${name}${result ? `: ${result}` : ''}`);
  } catch (error) {
    console.log(`✗ ${name}: ${error.message ?? error}`);
    process.exitCode = 1;
  }
}

// Wave 6 take-on: leave balances. HR credits the year's entitlement under each employee's leave policy.
await step('HR credits the leave year', async () => {
  const hr = await Api.as('hrmanager');
  const year = Number(new Date(serverNow().getTime() + 330 * 60_000).toISOString().slice(0, 4));
  const result = await hr.post('/api/v1/hr/leave/balances/credit', { year });
  return JSON.stringify(result).slice(0, 160);
});
