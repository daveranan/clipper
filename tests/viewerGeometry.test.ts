import assert from 'node:assert/strict'
import test from 'node:test'

import {
  actualSizeViewer,
  canvasBackingSize,
  clampCropRect,
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

test('crop interactions stay source-accurate across the zoom acceptance matrix', () => {
  const source = { width: 1920, height: 1080 }
  const crop = { x: 200, y: 100, width: 1280, height: 720 }
  for (const scale of [0.25, 1, 2, 4]) {
    const view = { mode: 'custom' as const, scale, offsetX: 37, offsetY: -19 }
    const start = sourceToViewport({ x: 500, y: 300 }, view)
    const end = { x: start.x + 40 * scale, y: start.y + 20 * scale }
    const sourceStart = viewportToSource(start, view)
    const sourceEnd = viewportToSource(end, view)
    const moved = clampCropRect({
      ...crop,
      x: crop.x + (sourceEnd.x - sourceStart.x),
      y: crop.y + (sourceEnd.y - sourceStart.y),
    }, source)
    assert.deepEqual(moved, { x: 240, y: 120, width: 1280, height: 720 })
  }
})

test('crop move and every corner remain bounded, even, and at least eight pixels', () => {
  const bounds = { width: 1919, height: 1079 }
  const cases = [
    clampCropRect({ x: 1901, y: 1061, width: 320, height: 180 }, bounds),
    clampCropRect({ x: -50, y: -50, width: 2, height: 2 }, bounds, 'tl'),
    clampCropRect({ x: 100, y: -50, width: 4000, height: 2 }, bounds, 'tr'),
    clampCropRect({ x: -50, y: 100, width: 2, height: 4000 }, bounds, 'bl'),
    clampCropRect({ x: 100, y: 100, width: 4000, height: 4000 }, bounds, 'br'),
  ]
  for (const crop of cases) {
    assert.ok(crop.x >= 0 && crop.y >= 0)
    assert.ok(crop.width >= 8 && crop.height >= 8)
    assert.ok(crop.x + crop.width <= 1918)
    assert.ok(crop.y + crop.height <= 1078)
    assert.equal(crop.x % 2, 0)
    assert.equal(crop.y % 2, 0)
    assert.equal(crop.width % 2, 0)
    assert.equal(crop.height % 2, 0)
  }
})
