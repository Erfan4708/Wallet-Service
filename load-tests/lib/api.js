import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { BASE_URL } from './config.js';

// Outcome counters. A 422 for insufficient funds is the ledger working, not
// failing, so it is counted separately from anything unexpected. k6's own
// http_req_failed treats every non-2xx as a failure and cannot draw that line.
export const ledgerSuccess = new Counter('ledger_success');
export const ledgerRefused = new Counter('ledger_refused');
export const ledgerUnexpected = new Counter('ledger_unexpected');

const JSON_HEADERS = { 'Content-Type': 'application/json' };

// Request identifiers only. Wallet identifiers are deterministic (see ids.js).
export function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

// URLs contain identifiers, so every request carries a fixed `name` tag.
// Without it k6 would create a separate series per account.
function post(path, body, name, idempotencyKey) {
  const headers = idempotencyKey
    ? Object.assign({ 'Idempotency-Key': idempotencyKey }, JSON_HEADERS)
    : JSON_HEADERS;

  return http.post(`${BASE_URL}${path}`, JSON.stringify(body), { headers, tags: { name } });
}

export function classify(response, { allowRefusal = false } = {}) {
  const status = response.status;

  if (status === 200 || status === 201) {
    ledgerSuccess.add(1);
    return 'success';
  }

  if (allowRefusal && (status === 409 || status === 422)) {
    ledgerRefused.add(1);
    return 'refused';
  }

  ledgerUnexpected.add(1, { status: String(status) });
  return 'unexpected';
}

export function createAccount(id, currency) {
  return post('/accounts', { currency, accountId: id }, 'create_account');
}

export function getAccount(id) {
  return http.get(`${BASE_URL}/accounts/${id}`, { tags: { name: 'get_account' } });
}

export function deposit(accountId, amount, currency, key = uuid()) {
  return post(`/accounts/${accountId}/deposits`, { amount, currency }, 'deposit', key);
}

export function withdraw(accountId, amount, currency, key = uuid()) {
  return post(`/accounts/${accountId}/withdrawals`, { amount, currency }, 'withdraw', key);
}

export function transfer(sourceAccountId, destinationAccountId, amount, currency, key = uuid()) {
  return post(
    '/transfers',
    { sourceAccountId, destinationAccountId, amount, currency },
    'transfer',
    key,
  );
}

export function reverse(transactionId, key = uuid()) {
  return post(`/ledger/transactions/${transactionId}/reversal`, {}, 'reverse', key);
}
