import assert from 'node:assert/strict';
import test from 'node:test';
import { createFeatureErrors } from '../src/featureErrors.ts';

test('late failures stay in their originating feature and stale attempts cannot replace a retry', () => {
  let messages;
  const errors = createFeatureErrors(value => { messages = value; });
  const natural = errors.begin('natural');
  const git = errors.begin('analysis');
  natural('natural failure');
  assert.equal(messages.analysis, '');
  git('git failure');
  const retry = errors.begin('natural');
  natural('old natural failure');
  assert.equal(messages.natural, '');
  assert.equal(messages.analysis, 'git failure');
  retry('new natural failure');
  errors.clear('natural');
  retry('late retry failure');
  assert.equal(messages.natural, '');
  assert.equal(messages.analysis, 'git failure');
});
