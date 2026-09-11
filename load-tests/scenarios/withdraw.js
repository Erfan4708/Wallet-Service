// D. Withdrawals. Same settlement lock as a deposit, plus a sufficient-funds
// decision that is only sound because the wallet row is locked first.
import { CURRENCIES, WALLETS } from '../lib/config.js';
import { classify, withdraw } from '../lib/api.js';
import { pick, randomIndex, walletId } from '../lib/ids.js';
import { ensureWallets } from '../lib/setup.js';
import { buildOptions, summarize } from '../lib/options.js';

const NAMES = ['withdraw'];

export const options = buildOptions(NAMES);

export function setup() {
  ensureWallets(CURRENCIES, WALLETS);
}

export default function () {
  const currency = pick(CURRENCIES);
  classify(withdraw(walletId(currency, randomIndex(WALLETS)), '1.00', currency), {
    allowRefusal: true,
  });
}

export function handleSummary(data) {
  return summarize(data, NAMES);
}
