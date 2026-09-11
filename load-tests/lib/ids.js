// Deterministic wallet identifiers. The prefix makes benchmark wallets
// recognisable in the database; the index makes every run address the same set.

const PREFIX = {
  USD: 'b1000000',
  EUR: 'b2000000',
  IRR: 'b3000000',
};

export function walletId(currency, index) {
  const prefix = PREFIX[currency];
  if (!prefix) {
    throw new Error(`No benchmark wallet prefix for currency ${currency}`);
  }

  return `${prefix}-0000-4000-8000-${String(index).padStart(12, '0')}`;
}

export function randomIndex(count) {
  return 1 + Math.floor(Math.random() * count);
}

// Two distinct wallets. A transfer to the same account is refused by validation,
// which would measure the refusal path instead of the transfer path.
export function randomPair(count) {
  const source = randomIndex(count);
  let destination = randomIndex(count);
  while (destination === source) {
    destination = randomIndex(count);
  }

  return [source, destination];
}

export function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}
