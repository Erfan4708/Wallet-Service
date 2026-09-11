import { DURATION, RESULTS_DIR, RUN_NAME, THRESHOLDS, VUS } from './config.js';

// Statuses an unexpected response is broken down by in the summary. k6 only
// reports a tagged submetric that some threshold names, so each gets an empty
// threshold below. 0 is a request that never got a response: a network error or
// a client-side timeout.
const UNEXPECTED_STATUSES = ['0', '400', '404', '500', '502', '503', '504'];

// Constant concurrency for a fixed duration. A benchmark comparing two runs needs
// identical load shapes; ramping executors make "before" and "after" differ in
// how long each spent at each level.
export function buildOptions(names) {
  // Everything is measured through submetrics filtered to the `load` scenario.
  // setup() creates and funds wallets through the same API, and without this
  // filter those hundreds of setup requests would be averaged into the results.
  const thresholds = {
    'http_req_duration{scenario:load}': [],
    'http_reqs{scenario:load}': [],
  };

  for (const name of names) {
    thresholds[`http_req_duration{scenario:load,name:${name}}`] = [];
  }

  for (const status of UNEXPECTED_STATUSES) {
    thresholds[`ledger_unexpected{status:${status}}`] = [];
  }

  if (THRESHOLDS) {
    // Test thresholds, not SLAs: any 5xx means the environment is broken and the
    // run's latency numbers mean nothing.
    thresholds.ledger_unexpected = [{ threshold: 'count<1', abortOnFail: false }];
  }

  return {
    scenarios: {
      load: {
        executor: 'constant-vus',
        vus: VUS,
        duration: DURATION,
        gracefulStop: '10s',
      },
    },
    setupTimeout: '10m',
    summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
    thresholds,
  };
}

function metric(data, name) {
  return data.metrics[name] ? data.metrics[name].values : undefined;
}

function count(data, name) {
  const values = metric(data, name);
  return values ? values.count : 0;
}

function seconds(duration) {
  const match = /^(\d+)(ms|s|m|h)$/.exec(duration);
  if (!match) {
    throw new Error(`Unsupported DURATION ${duration}`);
  }
  const factor = { ms: 0.001, s: 1, m: 60, h: 3600 }[match[2]];
  return Number(match[1]) * factor;
}

function fixed(value) {
  return value === undefined ? '' : value.toFixed(2);
}

// Writes the full k6 JSON, a one-row CSV for aggregation, and a compact stdout
// line. Rates are computed over the scenario's own duration rather than k6's
// test-run duration, which would include setup and understate throughput.
export function summarize(data, names) {
  const window = seconds(DURATION);
  const latency = metric(data, 'http_req_duration{scenario:load}') || {};
  const requests = count(data, 'http_reqs{scenario:load}');
  const success = count(data, 'ledger_success');
  const refused = count(data, 'ledger_refused');
  const unexpected = count(data, 'ledger_unexpected');

  // "500:2 0:1" — which statuses made up the unexpected count. A status outside
  // the list is still counted in `unexpected`, and shows up here as the gap.
  const statuses = UNEXPECTED_STATUSES
    .map((status) => [status, count(data, `ledger_unexpected{status:${status}}`)])
    .filter(([, n]) => n > 0)
    .map(([status, n]) => `${status}:${n}`)
    .join(' ');

  const row = {
    run: RUN_NAME,
    vus: VUS,
    duration_s: window,
    requests,
    rps: (requests / window).toFixed(1),
    success_per_s: (success / window).toFixed(1),
    p50_ms: fixed(latency.med),
    p95_ms: fixed(latency['p(95)']),
    p99_ms: fixed(latency['p(99)']),
    max_ms: fixed(latency.max),
    success,
    refused,
    unexpected,
    unexpected_pct: requests ? ((100 * unexpected) / requests).toFixed(3) : '0',
    unexpected_statuses: statuses,
  };

  const header = Object.keys(row).join(',');
  const values = Object.values(row).join(',');

  const lines = [
    '',
    `== ${RUN_NAME} (vus=${VUS}, duration=${DURATION}) ==`,
    `requests ${requests}  rps ${row.rps}  successful/s ${row.success_per_s}`,
    `latency ms  p50 ${row.p50_ms}  p95 ${row.p95_ms}  p99 ${row.p99_ms}  max ${row.max_ms}`,
    `outcomes  success ${success}  refused ${refused}  unexpected ${unexpected}${statuses ? ` (${statuses})` : ''}`,
  ];

  for (const name of names) {
    const tagged = metric(data, `http_req_duration{scenario:load,name:${name}}`);
    if (tagged && tagged.max > 0) {
      lines.push(
        `  ${name.padEnd(12)} p50 ${fixed(tagged.med)}  p95 ${fixed(tagged['p(95)'])}  p99 ${fixed(tagged['p(99)'])}`,
      );
    }
  }

  lines.push('');

  const output = { stdout: lines.join('\n') };
  output[`${RESULTS_DIR}/${RUN_NAME}.json`] = JSON.stringify(data, null, 2);
  output[`${RESULTS_DIR}/${RUN_NAME}.csv`] = `${header}\n${values}\n`;

  return output;
}
