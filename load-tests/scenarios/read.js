// A. Account lookup. The only scenario that takes no row lock, so it is the
// ceiling the write scenarios are measured against.
import { CURRENCIES, WALLETS } from '../lib/config.js';
import { getAccount, ledgerSuccess, ledgerUnexpected } from '../lib/api.js';
import { pick, randomIndex, walletId } from '../lib/ids.js';
import { ensureWallets } from '../lib/setup.js';
import { buildOptions, summarize } from '../lib/options.js';

const NAMES = ['get_account'];

export const options = buildOptions(NAMES);

export function setup() {
  ensureWallets(CURRENCIES, WALLETS);
}

export default function () {
  const response = getAccount(walletId(pick(CURRENCIES), randomIndex(WALLETS)));

  if (response.status === 200) {
    ledgerSuccess.add(1);
  } else {
    ledgerUnexpected.add(1, { status: String(response.status) });
  }
}

export function handleSummary(data) {
  return summarize(data, NAMES);
}
