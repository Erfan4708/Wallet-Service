import http from 'k6/http';
import { BASE_URL, FUNDING } from './config.js';
import { walletId } from './ids.js';

const JSON_HEADERS = { 'Content-Type': 'application/json' };
const CHUNK = 50;

// Creates and funds the benchmark wallets through the real API.
//
// Idempotent by construction, so a re-run changes nothing: account creation
// answers 409 for an identifier that already exists, and funding reuses a fixed
// idempotency key per wallet, which the ledger replays instead of depositing
// twice. No balance is written directly — funding is an ordinary deposit, with
// its settlement leg, its entries and its outbox message.
export function ensureWallets(currencies, count) {
  for (const currency of currencies) {
    for (let start = 1; start <= count; start += CHUNK) {
      const end = Math.min(start + CHUNK - 1, count);
      const creates = [];
      const fundings = [];

      for (let i = start; i <= end; i++) {
        const id = walletId(currency, i);

        creates.push([
          'POST',
          `${BASE_URL}/accounts`,
          JSON.stringify({ currency, accountId: id }),
          { headers: JSON_HEADERS, tags: { name: 'setup_create' } },
        ]);

        fundings.push([
          'POST',
          `${BASE_URL}/accounts/${id}/deposits`,
          JSON.stringify({ amount: FUNDING, currency }),
          {
            headers: Object.assign({ 'Idempotency-Key': `bench-fund-${id}` }, JSON_HEADERS),
            tags: { name: 'setup_fund' },
          },
        ]);
      }

      for (const response of http.batch(creates)) {
        if (response.status !== 201 && response.status !== 409) {
          throw new Error(`Creating a wallet failed with ${response.status}: ${response.body}`);
        }
      }

      for (const response of http.batch(fundings)) {
        if (response.status !== 201 && response.status !== 200) {
          throw new Error(`Funding a wallet failed with ${response.status}: ${response.body}`);
        }
      }
    }
  }
}
