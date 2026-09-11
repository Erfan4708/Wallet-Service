// C. Transfers between random distinct wallets. No settlement account is
// involved, so with enough wallets two transfers rarely share a row: this is the
// write path with contention designed out, for comparison with deposit.js.
import { CURRENCIES, WALLETS } from '../lib/config.js';
import { classify, transfer } from '../lib/api.js';
import { pick, randomPair, walletId } from '../lib/ids.js';
import { ensureWallets } from '../lib/setup.js';
import { buildOptions, summarize } from '../lib/options.js';

const NAMES = ['transfer'];

export const options = buildOptions(NAMES);

export function setup() {
  ensureWallets(CURRENCIES, WALLETS);
}

export default function () {
  const currency = pick(CURRENCIES);
  const [source, destination] = randomPair(WALLETS);

  classify(
    transfer(walletId(currency, source), walletId(currency, destination), '1.00', currency),
    { allowRefusal: true },
  );
}

export function handleSummary(data) {
  return summarize(data, NAMES);
}
