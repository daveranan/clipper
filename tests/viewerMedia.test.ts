import assert from 'node:assert/strict'
import test from 'node:test'

import { adjacentSourceFrame, cachedPreviewIndex, frameDuration, isCurrentFrameRequest, isFrameAtTime } from '../src/viewerMedia.ts'

test('scrub cache selects the nearest available proxy frame', () => {
  assert.equal(cachedPreviewIndex(1.04, 12, 120), 12)
  assert.equal(cachedPreviewIndex(1.05, 12, 120), 13)
  assert.equal(cachedPreviewIndex(10, 12, 120), null)
  assert.equal(cachedPreviewIndex(-0.01, 12, 120), null)
})

test('settled video accepts one effective frame duration and rejects older frames', () => {
  assert.equal(frameDuration(30), 1 / 30)
  assert.equal(isFrameAtTime(2 + 1 / 30, 2, 30), true)
  assert.equal(isFrameAtTime(2 + 1 / 20, 2, 30), false)
})

test('exact frame completion must match clip, request, and current time', () => {
  const request = { clipEpoch: 4, requestId: 9, secondsAt: 12 }
  assert.equal(isCurrentFrameRequest(request, 4, 9, 12, 30), true)
  assert.equal(isCurrentFrameRequest(request, 5, 9, 12, 30), false)
  assert.equal(isCurrentFrameRequest(request, 4, 10, 12, 30), false)
  assert.equal(isCurrentFrameRequest(request, 4, 9, 12.1, 30), false)
})

test('frame stepping advances one timeline frame across cuts and clamps at bounds', () => {
  const clips = [
    { start: 0, end: 1, sourceStart: 0, sourceEnd: 1 },
    { start: 1, end: 2, sourceStart: 2, sourceEnd: 3 },
  ]
  assert.ok(Math.abs(adjacentSourceFrame(0.99, 1, 30, clips) - (2 + 0.99 + 1 / 30 - 1)) < 1e-9)
  assert.ok(Math.abs(adjacentSourceFrame(2.01, -1, 30, clips) - (1.01 - 1 / 30)) < 1e-9)
  assert.equal(adjacentSourceFrame(0, -1, 30, clips), 0)
  assert.equal(adjacentSourceFrame(3, 1, 30, clips), 3)
})
