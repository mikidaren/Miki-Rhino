import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import test from 'node:test';

test('the independent OpenBIM snapshot records the exact shipped source bytes', () => {
  const root = new URL('../vendor/openbim/', import.meta.url);
  const snapshot = JSON.parse(readFileSync(new URL('snapshot.json', root), 'utf8'));
  for (const entry of snapshot.files) {
    const digest = createHash('sha256').update(readFileSync(new URL(entry.path, root))).digest('hex');
    assert.equal(digest, entry.sha256, entry.path);
  }
  assert.equal(snapshot.sourceCommit, '5cdd4205c43d2dff6fe12d9aca530c1256f3e891');
  assert.equal(snapshot.localAdjustments.length, 2);
});
