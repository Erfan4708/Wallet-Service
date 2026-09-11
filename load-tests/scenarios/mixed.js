// E. A mixed workload: mostly reads, a spread of money movements, and a small
// share of the requests that exercise correctness under concurrency — retried
// requests reusing an idempotency key, and reversals attempted twice.
import { Counter } from 'k6/metrics';
import { CURRENCIES, WALLETS } from '../lib/config.js';
import {
  classify,
  deposit,
  getAccount,
  ledgerSuccess,
  ledgerUnexpected,
  reverse,
  transfer,
  uuid,
  withdraw,
} from '../lib/api.js';
import { pick, randomIndex, randomPair, walletId } from '../lib/ids.js';
import { ensureWallets } from '../lib/setup.js';
import { buildOptions, summarize } from '../lib/options.js';

const NAMES = ['get_account', 'deposit', 'withdraw', 'transfer', 'reverse'];

// A retried request that moved money a second time, or a reversal accepted
// twice. Either is a correctness failure, and the run must say so loudly.
const idempotencyViolations = new Counter('idempotency_violations');

export const options = buildOptions(NAMES);
options.thresholds.idempotency_violations = ['count<1'];

export function setup() {
  ensureWallets(CURRENCIES, WALLETS);
}

// Per-VU state: each VU remembers its own last transfer so it has something of
// its own to reverse.
let lastTransfer = null;

export default function () {
  const roll = Math.random() * 100;
  const currency = pick(CURRENCIES);

  if (roll < 50) {
    const response = getAccount(walletId(currency, randomIndex(WALLETS)));
    if (response.status === 200) {
      ledgerSuccess.add(1);
    } else {
      ledgerUnexpected.add(1, { status: String(response.status) });
    }
    return;
  }

  if (roll < 65) {
    classify(deposit(walletId(currency, randomIndex(WALLETS)), '2.00', currency));
    return;
  }

  if (roll < 77) {
    classify(withdraw(walletId(currency, randomIndex(WALLETS)), '1.00', currency), {
      allowRefusal: true,
    });
    return;
  }

  if (roll < 97) {
    const [source, destination] = randomPair(WALLETS);
    const response = transfer(
      walletId(currency, source),
      walletId(currency, destination),
      '1.00',
      currency,
    );
    if (classify(response, { allowRefusal: true }) === 'success') {
      lastTransfer = JSON.parse(response.body).transactionId;
    }
    return;
  }

  if (roll < 99) {
    // A client retry: the same idempotency key twice. The second answer must be a
    // replay (200) naming the same transaction, not a second deposit.
    const key = uuid();
    const account = walletId(currency, randomIndex(WALLETS));
    const first = deposit(account, '3.00', currency, key);
    const second = deposit(account, '3.00', currency, key);

    classify(first);
    classify(second);

    if (
      first.status === 201 &&
      (second.status !== 200 ||
        JSON.parse(second.body).transactionId !== JSON.parse(first.body).transactionId)
    ) {
      idempotencyViolations.add(1);
    }
    return;
  }

  if (lastTransfer) {
    // A reversal, then the same reversal again under a different key. The second
    // must be refused with 409: a transaction is reversed at most once.
    const target = lastTransfer;
    lastTransfer = null;

    const first = reverse(target);
    classify(first, { allowRefusal: true });

    const second = reverse(target);
    classify(second, { allowRefusal: true });

    if (first.status === 201 && second.status === 201) {
      idempotencyViolations.add(1);
    }
  }
}

export function handleSummary(data) {
  return summarize(data, NAMES);
}
