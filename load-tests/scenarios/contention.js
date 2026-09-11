// F. High contention: many concurrent transfers squeezed into a handful of
// wallets, so almost every transaction waits on a row lock another holds.
//
// PATTERN selects the shape:
//   mesh    random pairs among HOT_ACCOUNTS wallets, in both directions
//           (HOT_ACCOUNTS=2 is the pure A->B / B->A case)
//   fanout  every transfer leaves wallet 1 — one hot source row
//
// This is where a lock-ordering mistake would surface as 40P01 deadlocks, and
// where lock wait time, not CPU, sets the throughput ceiling.
import { CURRENCIES, HOT_ACCOUNTS } from '../lib/config.js';
import { classify, transfer } from '../lib/api.js';
import { pick, randomIndex, randomPair, walletId } from '../lib/ids.js';
import { ensureWallets } from '../lib/setup.js';
import { buildOptions, summarize } from '../lib/options.js';

const PATTERN = __ENV.PATTERN || 'mesh';
const NAMES = ['transfer'];

export const options = buildOptions(NAMES);

export function setup() {
  if (HOT_ACCOUNTS < 2) {
    throw new Error('HOT_ACCOUNTS must be at least 2.');
  }

  ensureWallets(CURRENCIES, HOT_ACCOUNTS);
}

function endpoints() {
  if (PATTERN === 'fanout') {
    let destination = randomIndex(HOT_ACCOUNTS);
    while (destination === 1) {
      destination = randomIndex(HOT_ACCOUNTS);
    }
    return [1, destination];
  }

  return randomPair(HOT_ACCOUNTS);
}

export default function () {
  const currency = pick(CURRENCIES);
  const [source, destination] = endpoints();

  classify(
    transfer(walletId(currency, source), walletId(currency, destination), '1.00', currency),
    { allowRefusal: true },
  );
}

export function handleSummary(data) {
  return summarize(data, NAMES);
}
