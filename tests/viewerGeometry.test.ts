import assert from 'node:assert/strict'
import test from 'node:test'

import {
  actualSizeViewer,
  canvasBackingSize,
  fitViewer,
  resizeViewer,
  resizeRectWithLockedAspect,
  sourceRectToViewport,
  sourceToViewport,
  viewportToSource,
  zoomViewerAt,
} from '../src/viewerGeometry.ts'

const viewport = { width: 1200, height: 700 }
const ultrawide = { width: 3440, height: 1440 }

test('Fit centers an ultrawide source in the viewport', () => {
  const view = fitViewer(viewport, ultrawide)
  assert.equal(view.mode, 'fit')
  assert.equal(view.scale, 1200 / 3440)
  assert.ok(Math.abs(view.offsetX) < 1e-9)
  assert.ok(Math.abs(view.offsetY - (700 - 1440 * view.scale) / 2) < 1e-9)
})

test('Actual Size is one CSS pixel per source pixel and centered', () => {
  const view = actualSizeViewer(viewport, ultrawide)
  assert.equal(view.mode, 'actual')
  assert.equal(view.scale, 1)
  assert.deepEqual(sourceToViewport({ x: ultrawide.width / 2, y: ultrawide.height / 2 }, view), {
    x: viewport.width / 2,
    y: viewport.height / 2,
  })
})

test('cursor-anchored zoom preserves the source point under the cursor', () => {
  const initial = fitViewer(viewport, ultrawide)
  const anchor = { x: 917.25, y: 201.75 }
  const sourceBefore = viewportToSource(anchor, initial)
  const zoomed = zoomViewerAt(initial, anchor, initial.scale * 2, viewport, ultrawide)
  const sourceAfter = viewportToSource(anchor, zoomed)
  assert.ok(Math.abs(sourceBefore.x - sourceAfter.x) < 1e-9)
  assert.ok(Math.abs(sourceBefore.y - sourceAfter.y) < 1e-9)
})

test('custom view resize preserves the source point at viewport center', () => {
  const initial = zoomViewerAt(fitViewer(viewport, ultrawide), { x: 400, y: 300 }, 0.8, viewport, ultrawide)
  const oldCenterSource = viewportToSource({ x: viewport.width / 2, y: viewport.height / 2 }, initial)
  const nextViewport = { width: 900, height: 800 }
  const resized = resizeViewer(initial, viewport, nextViewport, ultrawide)
  const newCenterSource = viewportToSource({ x: nextViewport.width / 2, y: nextViewport.height / 2 }, resized)
  assert.ok(Math.abs(oldCenterSource.x - newCenterSource.x) < 1e-9)
  assert.ok(Math.abs(oldCenterSource.y - newCenterSource.y) < 1e-9)
})

test('source crop mapping uses the same view transform', () => {
  const view = { mode: 'custom' as const, scale: 2, offsetX: -100, offsetY: 50 }
  assert.deepEqual(sourceRectToViewport({ x: 40, y: 25, width: 320, height: 180 }, view), {
    x: -20,
    y: 100,
    width: 640,
    height: 360,
  })
})

test('canvas backing respects DPR and the 4K-equivalent pixel budget', () => {
  assert.deepEqual(canvasBackingSize({ width: 1000, height: 500 }, 1.5), {
    width: 1500,
    height: 750,
    renderDpr: 1.5,
  })
  const large = canvasBackingSize({ width: 5000, height: 3000 }, 2)
  assert.ok(large.width * large.height <= 3840 * 2160 + large.width + large.height)
  assert.ok(large.renderDpr < 2)
})

test('Shift-locked corner resize preserves ratio and the opposite corner', () => {
  const initial = { x: 100, y: 80, width: 1600, height: 900 }
  const resized = resizeRectWithLockedAspect(initial, { width: 3440, height: 1440 }, 'tl', { x: 400, y: 20 })
  assert.equal(resized.x + resized.width, initial.x + initial.width)
  assert.equal(resized.y + resized.height, initial.y + initial.height)
  assert.ok(Math.abs(resized.width / resized.height - initial.width / initial.height) < 0.01)
})
