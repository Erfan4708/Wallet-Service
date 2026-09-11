// Every knob a run can turn, read from the environment so the same script serves
// a smoke test and a stress test without edits. Defaults are deliberately small.

export const BASE_URL = __ENV.BASE_URL || 'http://api:8080';

// Deterministic wallets created once by setup(). Money moves between them; the
// set does not grow with the length of a run.
export const WALLETS = parseInt(__ENV.WALLETS || '200', 10);

// Large enough that withdrawals and transfers in the throughput scenarios are
// refused for insufficient funds only rarely, so they measure the write path
// rather than the refusal path.
export const FUNDING = __ENV.FUNDING || '1000000.00';

// Comma-separated. Splitting deposits across currencies is the experiment that
// tells a per-currency settlement bottleneck apart from a global one.
export const CURRENCIES = (__ENV.CURRENCIES || 'USD').split(',').map((c) => c.trim());

export const VUS = parseInt(__ENV.VUS || '10', 10);
export const DURATION = __ENV.DURATION || '60s';

// Used by the contention scenario: how many wallets the traffic is squeezed into.
export const HOT_ACCOUNTS = parseInt(__ENV.HOT_ACCOUNTS || '4', 10);

// Where the JSON summary is written, and under what name.
export const RESULTS_DIR = __ENV.RESULTS_DIR || '/results';
export const RUN_NAME = __ENV.RUN_NAME || 'run';

// Test thresholds only — a run fails if these are crossed, which flags a broken
// environment. They are NOT business SLAs and were not derived from any.
export const THRESHOLDS = (__ENV.THRESHOLDS || 'on') !== 'off';
