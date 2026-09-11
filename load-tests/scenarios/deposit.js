// B. Deposits. Every deposit in a currency also locks that currency's single
// settlement account, so this is the scenario that exposes a hot row if there is
// one. Run with CURRENCIES=USD and then CURRENCIES=USD,EUR: if throughput scales
// with the number of currencies, the settlement row is the constraint.
import { CURRENCIES, WALLETS } from '../lib/config.js';
import { classify, deposit } from '../lib/api.js';
import { pick, randomIndex, walletId } from '../lib/ids.js';
import { ensureWallets } from '../lib/setup.js';
import { buildOptions, summarize } from '../lib/options.js';

const NAMES = ['deposit'];

export const options = buildOptions(NAMES);

export function setup() {
  ensureWallets(CURRENCIES, WALLETS);
}

export default function () {
  const currency = pick(CURRENCIES);
  classify(deposit(walletId(currency, randomIndex(WALLETS)), '1.00', currency));
}

export function handleSummary(data) {
  return summarize(data, NAMES);
}
