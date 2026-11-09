// The demo server's database, asked with psql (HC360_DEMO_DB, a connection URI). A demo server has no SMS gateway, so
// codes it sends by SMS (a patient's portal sign-in, a new mobile number) are read from the messages it queued.

import { execFileSync } from 'node:child_process';

const DB = process.env.HC360_DEMO_DB;

/** Asks the demo server's database one question; `values` are passed as psql variables. Rows of columns, as text. */
export function ask(query, values = {}) {
  if (!DB) {
    throw new Error("Set HC360_DEMO_DB to a connection URI for the demo server's database (codes sent by SMS).");
  }
  const variables = Object.entries(values).flatMap(([name, value]) => ['-v', `${name}=${value}`]);
  return execFileSync('psql', [DB, '-At', '-v', 'ON_ERROR_STOP=1', ...variables], { input: query, encoding: 'utf8' })
    .split('\n')
    .filter(Boolean)
    .map((row) => row.split('|'));
}

/** The codes the server queued by SMS from template `templateKey` to `phone` (+91…), as [message id, code]. */
export const sentCodes = (templateKey, phone) =>
  ask(
    `select id, payload->'values'->>'code' from platform.outbox_messages
     where payload->>'templateKey' = :'templateKey' and payload->>'to' = :'phone';`,
    { templateKey, phone },
  );
