/* Actual production-executable process checks. Fresh synthetic homes only; no desktop, real account, model or network. */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

const project = path.resolve(__dirname, '..');
const outputRoot = path.join(project, 'validation-output');
const executable = path.join(project, 'dist', 'CodexAccountSwitcher.exe');
fs.mkdirSync(outputRoot, { recursive: true });
const checks = [];
const sessions = [];
const roots = [];
function requireCheck(ok, name) {
  if (!ok) throw new Error(name);
  checks.push(name);
}
function openSession(root, initial) {
  const child = spawn(executable, ['--deferred-protocol-test', root], {
    cwd: project, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, CODEX_HOME: path.join(root, 'codex-home'), OPENAI_API_KEY: '' }
  });
  let buffered = '', sequence = 0;
  const waiting = new Map();
  const readyPromise = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Synthetic process initialization timed out.')), 18000);
    waiting.set(0, { resolve, reject, timer });
  });
  let exitResolve;
  const exitPromise = new Promise(resolve => { exitResolve = resolve; });
  function rejectPending(message) {
    for (const pending of waiting.values()) { clearTimeout(pending.timer); pending.reject(new Error(message)); }
    waiting.clear();
  }
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buffered += chunk;
    while (buffered.includes('\n')) {
      const end = buffered.indexOf('\n');
      const line = buffered.slice(0, end).trim();
      buffered = buffered.slice(end + 1);
      if (!line) continue;
      let value;
      try { value = JSON.parse(line); } catch { rejectPending('Synthetic process emitted an invalid protocol response.'); continue; }
      const pending = waiting.get(value.id);
      if (pending) { waiting.delete(value.id); clearTimeout(pending.timer); pending.resolve(value); }
    }
  });
  child.stderr.on('data', () => {}); // Do not forward arbitrary process diagnostics.
  child.on('error', () => rejectPending('Synthetic process could not start.'));
  child.on('exit', code => { rejectPending('Synthetic process exited before its response.'); exitResolve(code); });
  child.stdin.on('error', () => {});
  child.stdin.write(JSON.stringify(initial) + '\n');
  const session = {
    root, child, ready: readyPromise,
    call(operation) {
      const id = ++sequence;
      const response = new Promise((resolve, reject) => {
        const timer = setTimeout(() => { waiting.delete(id); reject(new Error('Synthetic process command timed out.')); }, 20000);
        waiting.set(id, { resolve, reject, timer });
      });
      child.stdin.write(JSON.stringify({ id, operation }) + '\n');
      return response;
    },
    async close() {
      child.stdin.end();
      const result = await Promise.race([exitPromise, new Promise((_, reject) => {
        const timer = setTimeout(() => reject(new Error('Synthetic process normal exit timed out.')), 17000);
        timer.unref();
      })]);
      return result;
    }
  };
  sessions.push(session);
  return session;
}
function create(caseMode) {
  const root = path.join(outputRoot, 'deferred-process-' + crypto.randomUUID().replaceAll('-', ''));
  fs.mkdirSync(root);
  roots.push(root);
  return openSession(root, { mode: 'create', caseMode });
}
function readyChecks(value, context) {
  requireCheck(value.syntheticOnly && !value.realDesktopConnected, context + ': only the synthetic fixture is used.');
  requireCheck(value.workerReady && value.readyConfirmed && value.workerAlive, context + ': actual child and parent confirm readiness.');
  requireCheck(['pending', 'waiting'].includes(value.state), context + ': readiness is not claimed as switching complete.');
  requireCheck(value.cache === 'original' && !value.encryptedBackup, context + ': waiting does not change the cache or create a backup.');
}
function failedChecks(value, context) {
  requireCheck(value.state === 'failed', context + ': a missing worker is reported as failed.');
  requireCheck(value.cache === 'original' && !value.encryptedBackup, context + ': failure does not change the synthetic cache.');
}

(async () => {
  let passed = false, failure = null;
  try {
    const normal = create('complete');
    readyChecks(await normal.ready, 'Normal waiting');
    const duplicate = await normal.call('duplicate');
    requireCheck(duplicate.duplicateExitCode === 2 && ['pending', 'waiting'].includes(duplicate.state) && duplicate.workerAlive,
      'Duplicate child is rejected without changing the active request.');
    await normal.call('close');
    const complete = await normal.call('wait-exit');
    requireCheck(complete.state === 'completed' && complete.stage === 'completed' && !complete.workerAlive,
      'Two idle confirmations let the actual child finish and exit.');
    requireCheck(complete.cache === 'target' && complete.encryptedBackup && complete.backupMatchesOriginal,
      'Real synthetic cache replacement preserves an encrypted exact recovery backup.');
    requireCheck((await normal.call('status')).state === 'completed', 'Exit observer does not overwrite a completed request.');
    requireCheck(await normal.close() === 0, 'Normal parent process exits cleanly.');

    const early = create('early-exit');
    const earlyValue = await early.ready;
    failedChecks(earlyValue, 'Early exit');
    requireCheck(!earlyValue.workerReady && !earlyValue.readyConfirmed && earlyValue.exitCode === 7,
      'Actual child exit before readiness returns failure with a fixed stage and safe exit code.');
    requireCheck(await early.close() === 0, 'Early-exit fixture parent exits cleanly.');

    const timeoutStarted = Date.now();
    const noReady = create('no-ready');
    const notReady = await noReady.ready;
    const elapsed = Date.now() - timeoutStarted;
    failedChecks(notReady, 'Readiness timeout');
    requireCheck(!notReady.workerReady && !notReady.readyConfirmed && notReady.stage === 'ready-timeout' && elapsed >= 4500 && elapsed < 12000,
      'Actual live child without a readiness handshake cannot produce a registered-success result.');
    const notReadyExited = await noReady.call('wait-exit');
    requireCheck(notReadyExited.state === 'failed' && notReadyExited.exitCode === 8 && !notReadyExited.workerAlive,
      'Readiness failure is retained after observing the actual child exit code.');
    requireCheck(await noReady.close() === 0, 'No-ready fixture parent exits cleanly.');

    const lost = create('lost-after-ready');
    readyChecks(await lost.ready, 'Loss after readiness');
    await lost.call('stop-worker');
    const stopped = await lost.call('wait-exit');
    failedChecks(stopped, 'Loss after readiness');
    requireCheck(stopped.stage === 'worker-exited' && stopped.exitCode === 7 && !stopped.workerAlive,
      'An actual child lost after readiness is not mistaken for completion.');
    requireCheck(await lost.close() === 0, 'Lost-worker fixture parent exits cleanly.');

    const cancelled = create('complete');
    readyChecks(await cancelled.ready, 'Cancellation');
    await cancelled.call('cancel');
    const cancelledValue = await cancelled.call('wait-exit');
    requireCheck(cancelledValue.state === 'cancelled' && cancelledValue.cache === 'original' && !cancelledValue.encryptedBackup && !cancelledValue.workerAlive,
      'Cancellation stops the actual child without replacing the synthetic cache.');
    requireCheck(await cancelled.close() === 0, 'Cancelled fixture parent exits cleanly.');

    const departing = create('parent-exit');
    readyChecks(await departing.ready, 'Parent normal exit');
    const departed = await departing.call('parent-exit');
    requireCheck(departed.parentExit && departed.workerAlive, 'Parent explicitly leaves only its acknowledged synthetic worker running.');
    requireCheck(await departing.close() === 0, 'The original synthetic parent exits normally.');
    const inspecting = openSession(departing.root, { mode: 'inspect' });
    const observed = await inspecting.ready;
    readyChecks(observed, 'Independent observer after parent exit');
    fs.writeFileSync(path.join(departing.root, 'closed.marker'), 'synthetic-clients-closed');
    const afterParentExit = await inspecting.call('wait-exit');
    requireCheck(afterParentExit.state === 'completed' && afterParentExit.cache === 'target' && !afterParentExit.workerAlive,
      'The actual acknowledged worker completes the fixture after its parent process exits.');
    requireCheck(afterParentExit.encryptedBackup && afterParentExit.backupMatchesOriginal,
      'Independent observer verifies the exact encrypted recovery backup.');
    requireCheck(await inspecting.close() === 0, 'Independent observer exits cleanly.');
    passed = true;
  } catch (error) {
    failure = error instanceof Error ? error.message : 'Synthetic deferred process check failed.';
  } finally {
    // Only fixed markers in this run\'s fresh roots are written. No user process is terminated.
    for (const root of roots) {
      try { fs.writeFileSync(path.join(root, 'stop.marker'), 'synthetic-worker-stop'); } catch { }
    }
    for (const session of sessions) {
      try { session.child.stdin.end(); } catch { }
    }
    const result = { passed, checks, failure, syntheticOnly: true, realDesktopConnected: false,
      realAccountUsed: false, modelRequests: 0, networkRequests: 0, userProcessesTerminated: 0,
      normalParentExitTestIsNotCodexHostExitProof: true };
    const resultText = JSON.stringify(result, null, 2);
    fs.writeFileSync(path.join(outputRoot, 'deferred-worker-result.json'), resultText);
    fs.writeFileSync(path.join(outputRoot, 'deferred-worker-result-' + crypto.randomUUID() + '.json'), resultText);
    console.log(JSON.stringify({ passed, checks: checks.length, failure, syntheticOnly: true, realDesktopConnected: false }));
    process.exitCode = passed ? 0 : 1;
  }
})();
