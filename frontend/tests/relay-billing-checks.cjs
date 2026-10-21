const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const { webcrypto } = require('node:crypto');
const ts = require('typescript');
const { of, throwError, Subject } = require('rxjs');

const frontend = path.resolve(__dirname, '..');
const project = { id: '0123456789abcdef01234567', createdByWalletAddress: 'owner' };
const tx = '0x' + 'a'.repeat(64);
const terms = (id = 'starter') => ({
  packageId: id,
  version: 'launch-v1',
  name: id === 'starter' ? 'Starter' : 'Pro',
  gasDatoshis: id === 'starter' ? 500000000 : 1000000000,
  days: 30,
  graceDays: 3,
  maxActiveSubscriptions: id === 'starter' ? 5 : 15,
  maxEvents: id === 'starter' ? 10000 : 50000
});

function intent(id = 'quote', packageId = 'pro') {
  return {
    id,
    projectId: project.id,
    network: 'neo3:mainnet',
    recipientAddress: 'quoted-recipient',
    recipientScriptHash: '0x' + 'b'.repeat(40),
    requiredGasDatoshis: terms(packageId).gasDatoshis,
    status: 'pending',
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 900000).toISOString(),
    terms: {
      operation: 'purchase_period',
      package: terms(packageId),
      requiredGasDatoshis: terms(packageId).gasDatoshis,
      targets: []
    }
  };
}

function memoryStorage() {
  const items = new Map();
  return {
    getItem: key => items.get(key) ?? null,
    setItem: (key, value) => items.set(key, value),
    removeItem: key => items.delete(key)
  };
}

function lockManager() {
  const held = new Set();
  return {
    async request(name, options, callback) {
      if (held.has(name)) return callback(null);
      held.add(name);
      try {
        return await callback({ name });
      } finally {
        held.delete(name);
      }
    }
  };
}

function load(file, environment = {}) {
  const module = { exports: {} };
  const angular = {
    Injectable: () => value => value,
    Component: () => value => value,
    Input: () => () => {},
    Output: () => () => {},
    EventEmitter: class {
      emit() {}
    }
  };
  const context = vm.createContext({
    module,
    exports: module.exports,
    require: name =>
      name === '@angular/core'
        ? angular
        : name.startsWith('@angular/') || name.startsWith('.')
          ? {}
          : require(name),
    console,
    crypto: webcrypto,
    setTimeout: (...args) => {
      const timer = setTimeout(...args);
      timer.unref();
      return timer;
    },
    clearTimeout,
    document: { hidden: false },
    localStorage: memoryStorage(),
    navigator: { locks: lockManager() },
    ...environment
  });
  const compiled = ts.transpileModule(fs.readFileSync(path.join(frontend, file), 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
      experimentalDecorators: true
    }
  }).outputText;
  vm.runInContext(compiled, context, { filename: file });
  return module.exports;
}

function fixture(environment = {}) {
  const calls = { invoke: [], confirm: [], quote: [], selection: [] };
  const f = {
    calls,
    address: 'owner',
    network: 'neo3:mainnet',
    authenticated: true,
    history: { payments: [], entitlements: [], pendingIntents: [intent()], periods: [] },
    catalog: {
      billingReady: true,
      packages: ['starter', 'pro'].map(id => ({ terms: terms(id), purchaseEnabled: true }))
    }
  };
  const api = {
    getRelayPackages: () => of(f.catalog),
    getRelayPaymentHistory: () => of(structuredClone(f.history)),
    getRelayUsage: (_, network) =>
      of({
        plan: 'paid',
        status: 'active',
        packageId: 'starter',
        maxActiveSubscriptions: 5,
        maxEvents: 10000,
        eventsUsed: network === 'neo3:mainnet' ? 8000 : 120,
        eventsRemaining: network === 'neo3:mainnet' ? 2000 : 9880
      }),
    getWebhookSubscriptions: () => of([]),
    createRelayPaymentIntent: (_, request) => {
      calls.quote.push(structuredClone(request));
      return of(intent('new-quote', request.packageId));
    },
    confirmRelayPayment: (_, id, transactionId) => {
      calls.confirm.push({ id, transactionId });
      f.history.pendingIntents = f.history.pendingIntents.map(x =>
        x.id === id
          ? { ...x, submittedTransactionId: transactionId, status: 'awaiting_finality' }
          : x
      );
      return of({ status: 'awaiting_finality', transactionId, intentId: id });
    },
    setRelayDowngradeSelection: (_, periodId, subscriptionIds) => {
      calls.selection.push({ periodId, subscriptionIds });
      return of({ selections: [] });
    }
  };
  const wallet = {
    account: () => ({ address: f.address, scriptHash: '0x' + 'c'.repeat(40) }),
    session: () => ({ network: f.network }),
    invokeContract: async (...args) => {
      calls.invoke.push(args);
      return tx;
    }
  };
  const auth = { canUseSession: () => f.authenticated, ensureAuthenticated: async () => {} };
  const { RelayBillingStateService } = load(
    'src/app/services/relay-billing-state.service.ts',
    environment
  );
  const state = new RelayBillingStateService(
    api,
    auth,
    wallet,
    { canManage: (p, address) => !!p && p.createdByWalletAddress === address },
    { format: (error, fallback) => error.message || fallback }
  );
  Object.assign(state, {
    project,
    ready: true,
    catalog: f.catalog,
    history: structuredClone(f.history)
  });
  return Object.assign(f, { state, api, wallet, auth });
}

function period(id, packageId, start, end, source = 'purchase') {
  return {
    id,
    effectiveTerms: terms(packageId),
    originalTerms: terms('starter'),
    startsAt: new Date(start).toISOString(),
    endsAt: new Date(end).toISOString(),
    source
  };
}

function component(f) {
  const { RelayBillingComponent } = load(
    'src/app/components/relay-billing/relay-billing.component.ts'
  );
  return new RelayBillingComponent(f.state);
}

const plain = value => JSON.parse(JSON.stringify(value));

test('Starter and Pro quotes send explicit server selections and stable retry keys', async () => {
  for (const packageId of ['starter', 'pro']) {
    const f = fixture();
    f.state.history.pendingIntents = [];
    f.api.createRelayPaymentIntent = (_, request) => {
      f.calls.quote.push(plain(request));
      return throwError(() => new Error('network unavailable'));
    };
    await f.state.quote(packageId, 'purchase_period');
    await f.state.quote(packageId, 'purchase_period');
    assert.equal(f.calls.quote[0].packageId, packageId);
    assert.equal(f.calls.quote[0].operation, 'purchase_period');
    assert.equal(f.calls.quote[0].requestKey, f.calls.quote[1].requestKey);
    assert.equal('price' in f.calls.quote[0], false);
  }
});

test('selected upgrades send only explicit period IDs without purchasing another month', async () => {
  const f = fixture();
  f.state.history.pendingIntents = [];
  await f.state.quote('pro', 'upgrade_periods', ['second', 'first']);
  assert.deepEqual(plain(f.calls.quote[0].periodIds), ['first', 'second']);
  assert.equal(f.calls.quote[0].operation, 'upgrade_periods');
  assert.equal(f.state.openIntent, true);
});

test('both tiers transfer the immutable quoted integer amount and recipient', async () => {
  for (const packageId of ['starter', 'pro']) {
    const f = fixture();
    const quote = intent('quote', packageId);
    f.state.history.pendingIntents = [quote];
    f.catalog.packages = [];
    await f.state.pay(quote);
    const [network, contract, operation, args] = f.calls.invoke[0];
    assert.equal(network, 'neo3:mainnet');
    assert.equal(contract, '0xd2a4cff31913016155e38e474a2c06d08be276cf');
    assert.equal(operation, 'transfer');
    assert.equal(args[1].value, quote.recipientScriptHash);
    assert.equal(args[2].value, String(quote.requiredGasDatoshis));
    assert.ok(f.calls.confirm.every(x => x.transactionId === tx));
  }
});

test('non-owner, Testnet wallet, stale data and unauthenticated sessions cannot transfer', async () => {
  for (const change of [
    f => (f.address = 'stranger'),
    f => (f.network = 'neo3:testnet'),
    f => (f.state.ready = false),
    f => (f.authenticated = false)
  ]) {
    const f = fixture();
    change(f);
    await f.state.pay(intent());
    assert.equal(f.calls.invoke.length, 0);
  }
});

test('wallet changes during authentication prevent sending a transfer', async () => {
  const f = fixture();
  f.auth.ensureAuthenticated = async () => {
    f.address = 'stranger';
  };
  await f.state.pay(intent());
  assert.equal(f.calls.invoke.length, 0);
});

test('invalid, expired, foreign or already-bound quotes cannot trigger another transfer', async () => {
  for (const change of [
    q => (q.expiresAt = new Date(0).toISOString()),
    q => (q.projectId = 'other'),
    q => (q.requiredGasDatoshis = 1.2),
    q => (q.recipientScriptHash = 'invalid'),
    q => (q.submittedTransactionId = tx),
    q => (q.status = 'applying'),
    q => (q.terms.requiredGasDatoshis = 7)
  ]) {
    const f = fixture();
    const quote = intent();
    change(quote);
    await f.state.pay(quote);
    assert.equal(f.calls.invoke.length, 0);
  }
});

test('repeated clicks while the wallet is open cause only one invocation', async () => {
  const f = fixture();
  let release;
  f.wallet.invokeContract = (...args) => {
    f.calls.invoke.push(args);
    return new Promise(resolve => {
      release = resolve;
    });
  };
  const first = f.state.pay(intent());
  await new Promise(resolve => setImmediate(resolve));
  await f.state.pay(intent());
  assert.equal(f.calls.invoke.length, 1);
  release(tx);
  await first;
});

test('two tabs cannot open wallets concurrently for the same intent', async () => {
  const environment = { localStorage: memoryStorage(), navigator: { locks: lockManager() } };
  const first = fixture(environment),
    second = fixture(environment);
  let release;
  first.wallet.invokeContract = (...args) => {
    first.calls.invoke.push(args);
    return new Promise(resolve => {
      release = resolve;
    });
  };
  const pending = first.state.pay(intent());
  await new Promise(resolve => setImmediate(resolve));
  await second.state.pay(intent());
  assert.equal(second.calls.invoke.length, 0);
  release(tx);
  await pending;
});

test('a rejected wallet attempt requires an explicit no-broadcast acknowledgement', async () => {
  const f = fixture();
  f.wallet.invokeContract = async () => {
    throw new Error('Wallet request rejected');
  };
  const quote = intent();
  await f.state.pay(quote);
  assert.ok(f.state.localSubmission);
  assert.equal(f.state.canPay(quote), false);
  f.state.acknowledgeNoTransfer(quote);
  assert.equal(f.state.localSubmission, null);
  assert.equal(f.state.canPay(quote), true);
});

test('site storage failure blocks the wallet before a transfer is requested', async () => {
  const storage = memoryStorage();
  storage.setItem = () => {
    throw new Error('storage denied');
  };
  const f = fixture({ localStorage: storage });
  await f.state.pay(intent());
  assert.equal(f.calls.invoke.length, 0);
  assert.match(f.state.error, /recovery cannot be saved/i);
});

test('an invalid wallet result keeps the attempt blocked for manual recovery', async () => {
  const f = fixture();
  f.wallet.invokeContract = async () => 'invalid-wallet-result';
  await f.state.pay(intent());
  assert.ok(f.state.localSubmission);
  assert.equal(f.state.localSubmission.transactionId, undefined);
  assert.equal(f.calls.confirm.length, 0);
  assert.equal(f.state.canPay(intent()), false);
});

test('an existing transaction remains confirmable when browser storage is unavailable', async () => {
  const storage = memoryStorage();
  storage.setItem = () => {
    throw new Error('storage denied');
  };
  const f = fixture({ localStorage: storage });
  await f.state.check(intent(), tx);
  assert.equal(f.calls.confirm[0].transactionId, tx);
  assert.equal(f.calls.invoke.length, 0);
});

test('leaving the billing view still binds a returned transaction to its original intent', async () => {
  const f = fixture();
  let release;
  f.wallet.invokeContract = () =>
    new Promise(resolve => {
      release = resolve;
    });
  const pending = f.state.pay(intent());
  await new Promise(resolve => setImmediate(resolve));
  f.state.start(null);
  release(tx);
  await pending;
  assert.equal(f.calls.confirm[0].id, 'quote');
  assert.equal(f.calls.confirm[0].transactionId, tx);
  assert.equal(f.state.project, null);
  assert.equal(f.state.history, null);
  assert.equal(f.state.message, '');
});

test('rejected server verification releases local recovery state but retains the rejection', async () => {
  const f = fixture();
  f.api.confirmRelayPayment = (_, id, transactionId) => {
    f.history.pendingIntents = [];
    f.history.recentIntents = [
      {
        ...intent(id),
        status: 'rejected',
        submittedTransactionId: transactionId,
        recoveryMessage: 'Wrong recipient'
      }
    ];
    return of({ status: 'rejected', message: 'Wrong recipient' });
  };
  await f.state.pay(intent());
  assert.equal(f.state.localSubmission, null);
  assert.equal(f.state.openIntent, false);
  assert.equal(f.state.pendingIntents[0].status, 'rejected');
});

test('confirmation API failure retains a transaction across refresh without another transfer', async () => {
  const storage = memoryStorage();
  const f = fixture({ localStorage: storage });
  f.api.confirmRelayPayment = () => throwError(() => new Error('API offline'));
  await f.state.pay(intent());
  assert.equal(f.state.localSubmission.transactionId, tx);
  const recovered = fixture({ localStorage: storage });
  recovered.state.start(project);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(recovered.calls.invoke.length, 0);
  assert.equal(recovered.calls.confirm[0].transactionId, tx);
  recovered.state.ngOnDestroy();
});

test('timely bound payments can be confirmed after quote expiry', async () => {
  const f = fixture();
  const quote = {
    ...intent(),
    expiresAt: new Date(0).toISOString(),
    submittedTransactionId: tx,
    status: 'awaiting_finality'
  };
  f.history.pendingIntents = [quote];
  await f.state.check(quote);
  assert.ok(f.calls.confirm.length);
  assert.equal(f.calls.invoke.length, 0);
  assert.equal(f.state.canPay(quote), false);
});

test('expired and rejected quotes remain visible without blocking a new unpaid quote', async () => {
  const f = fixture();
  f.state.history.pendingIntents = [];
  f.state.history.recentIntents = [
    { ...intent('expired'), status: 'expired', expiresAt: new Date(0).toISOString() },
    { ...intent('rejected'), status: 'rejected', submittedTransactionId: tx }
  ];
  assert.equal(f.state.pendingIntents.length, 2);
  assert.equal(f.state.openIntent, false);
  assert.ok(f.state.pendingIntents.every(x => !f.state.canPay(x)));
});

test('applying and reconciliation states block new purchases, including zero-money conflicts', async () => {
  for (const status of ['applying', 'reconciliation_required']) {
    const f = fixture();
    f.state.history.pendingIntents = [{ ...intent(), status, unappliedGasDatoshis: 0 }];
    await f.state.quote('pro', 'purchase_period');
    assert.equal(f.calls.quote.length, 0);
  }
});

test('failed refresh retains visible data but prevents paying with a stale view', async () => {
  const f = fixture();
  f.api.getRelayPackages = () => throwError(() => new Error('catalog unavailable'));
  await f.state.refresh();
  assert.equal(f.state.ready, false);
  assert.equal(f.state.canPay(intent()), false);
  assert.ok(f.state.history);
});

test('out-of-order refresh cannot overwrite a different project context', async () => {
  const f = fixture();
  const delayed = new Subject();
  f.api.getRelayPaymentHistory = () => delayed;
  const pending = f.state.refresh();
  f.state.start(null);
  delayed.next(f.history);
  delayed.complete();
  await pending;
  assert.equal(f.state.history, null);
  assert.equal(f.state.ready, false);
});

test('billing refresh requests only Mainnet usage and subscriptions', async () => {
  const f = fixture();
  const networks = [];
  const originalUsage = f.api.getRelayUsage;
  f.api.getRelayUsage = (id, network) => {
    networks.push(network);
    if (network !== 'neo3:mainnet') throw new Error('Testnet is unavailable');
    return originalUsage(id, network);
  };
  f.api.getWebhookSubscriptions = (_, network) => {
    networks.push(network);
    if (network !== 'neo3:mainnet') throw new Error('Testnet is unavailable');
    return of([
      { id: 'mainnet', network },
      { id: 'testnet', network: 'neo3:testnet' }
    ]);
  };
  await f.state.refresh();
  assert.equal(f.state.usage['neo3:mainnet'].eventsUsed, 8000);
  assert.equal(f.state.usage['neo3:testnet'], undefined);
  assert.equal(f.state.ready, true);
  assert.deepEqual(networks, ['neo3:mainnet', 'neo3:mainnet']);
  assert.deepEqual(
    plain(f.state.subscriptions).map(x => x.id),
    ['mainnet']
  );
});

test('future Starter boundaries remain visible when buying or upgrading Pro', () => {
  const f = fixture(),
    c = component(f),
    now = Date.now();
  f.state.history.periods = [
    period('current', 'starter', now - 10000, now + 10000),
    period('second', 'starter', now + 10000, now + 20000),
    period('third', 'pro', now + 20000, now + 30000)
  ];
  const quote = intent();
  quote.terms.bonus = { target: { periodId: 'current' } };
  assert.deepEqual(
    plain(c.quoteStarterPeriods(quote)).map(x => x.id),
    ['second']
  );
  quote.terms.targets = [{ periodId: 'second' }];
  assert.equal(c.quoteStarterPeriods(quote).length, 0);
});

test('only unexpired standard Starter periods are selectable for standalone upgrades', () => {
  const c = component(fixture()),
    now = Date.now();
  assert.equal(c.canUpgrade(period('current', 'starter', now - 10000, now + 10000)), true);
  assert.equal(c.canUpgrade(period('future', 'starter', now + 10000, now + 20000)), true);
  assert.equal(c.canUpgrade(period('ended', 'starter', now - 20000, now - 10000)), false);
  assert.equal(c.canUpgrade(period('pro', 'pro', now - 10000, now + 10000)), false);
  assert.equal(
    c.canUpgrade(period('legacy', 'starter', now - 10000, now + 10000, 'legacy_carryover')),
    false
  );
});

test('a lower-cap boundary uses only enabled Mainnet subscriptions in deterministic order', () => {
  const f = fixture(),
    c = component(f),
    now = Date.now();
  f.state.history.periods = [
    period('pro', 'pro', now - 10000, now + 10000),
    period('starter', 'starter', now + 10000, now + 20000)
  ];
  f.state.subscriptions = ['f', 'e', 'd', 'c', 'b', 'a'].map((id, index) => ({
    id,
    createdAt: new Date(0).toISOString(),
    isEnabled: true,
    network: index % 2 ? 'neo3:mainnet' : 'neo3:testnet'
  }));
  assert.equal(c.downgradePeriods.length, 1);
  assert.deepEqual(plain(c.selections(c.downgradePeriods[0])), ['a', 'c', 'e']);
});

test('an explicit empty boundary selection remains empty and does not refill defaults', async () => {
  const f = fixture(),
    c = component(f),
    now = Date.now();
  const boundary = period('starter', 'starter', now + 10000, now + 20000);
  f.state.usage['neo3:mainnet'] = {
    transitions: { selections: [{ periodId: 'starter', subscriptionIds: [] }] }
  };
  f.state.subscriptions = [
    { id: 'a', isEnabled: true, createdAt: new Date(0).toISOString(), network: 'neo3:mainnet' }
  ];
  assert.deepEqual(plain(c.selections(boundary)), []);
  await c.saveSelection(boundary);
  assert.deepEqual(plain(f.calls.selection[0].subscriptionIds), []);
});

test('disabled or deleted selected webhooks are not sent as active boundary choices', async () => {
  const f = fixture(),
    c = component(f),
    now = Date.now();
  c.selectionDrafts.starter = ['enabled', 'paused', 'deleted'];
  f.state.subscriptions = [
    {
      id: 'enabled',
      isEnabled: true,
      createdAt: new Date(0).toISOString(),
      network: 'neo3:mainnet'
    },
    { id: 'paused', isEnabled: false, network: 'neo3:mainnet' },
    { id: 'testnet', isEnabled: true, network: 'neo3:testnet' }
  ];
  await c.saveSelection(period('starter', 'starter', now + 10000, now + 20000));
  assert.deepEqual(plain(f.calls.selection[0].subscriptionIds), ['enabled']);
});

test('unapplied totals separate failed paid upgrade lines from excess or late funds', () => {
  const c = component(fixture());
  assert.equal(
    c.otherUnapplied({
      unappliedGasDatoshis: 700000000,
      allocations: [
        { status: 'ineligible', priceDatoshis: 500000000 },
        { status: 'ineligible', priceDatoshis: 0 }
      ]
    }),
    200000000
  );
});

test('owner gateway requests carry package selection and boundary IDs with wallet context', () => {
  class HttpContext {
    set(key, value) {
      this[key] = value;
      return this;
    }
  }
  const module = load('src/app/services/event-relay-api.service.ts', {
    require: name =>
      name === '@angular/core'
        ? { Injectable: () => value => value }
        : name === '@angular/common/http'
          ? { HttpContext }
          : name.endsWith('wallet-auth.interceptor')
            ? { REQUIRE_WALLET_SESSION: 'wallet' }
            : {}
  });
  const calls = [];
  const api = new module.EventRelayApiService(
    { post: (...args) => calls.push(args) },
    { value: { apiBaseUrl: '/api' } }
  );
  const request = {
    packageId: 'pro',
    operation: 'upgrade_periods',
    periodIds: ['second'],
    requestKey: 'unique-key'
  };
  api.createRelayPaymentIntent(project.id, request);
  api.setRelayDowngradeSelection(project.id, 'second', ['webhook']);
  assert.equal(calls[0][0], `/api/projects/${project.id}/relay/mainnet/payments/intents`);
  assert.deepEqual(calls[0][1], request);
  assert.equal(calls[0][2].context.wallet, true);
  assert.deepEqual(plain(calls[1][1]), { periodId: 'second', subscriptionIds: ['webhook'] });
});
