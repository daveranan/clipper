export type ViewerPoint = { x: number; y: number }
export type ViewerSize = { width: number; height: number }
export type ViewerRect = ViewerPoint & ViewerSize
export type ViewerMode = 'fit' | 'actual' | 'custom'
export type ViewerCorner = 'tl' | 'tr' | 'bl' | 'br'

export type ViewerView = {
  mode: ViewerMode
  scale: number
  offsetX: number
  offsetY: number
}

export const initialViewerView: ViewerView = {
  mode: 'fit',
  scale: 1,
  offsetX: 0,
  offsetY: 0,
}

const safeDimension = (value: number) => Math.max(1, Number.isFinite(value) ? value : 1)

export function fitScale(viewport: ViewerSize, source: ViewerSize) {
  return Math.min(
    safeDimension(viewport.width) / safeDimension(source.width),
    safeDimension(viewport.height) / safeDimension(source.height),
  )
}

export function centeredView(viewport: ViewerSize, source: ViewerSize, scale: number, mode: ViewerMode): ViewerView {
  const safeScale = Number.isFinite(scale) && scale > 0 ? scale : 1
  return {
    mode,
    scale: safeScale,
    offsetX: (viewport.width - source.width * safeScale) / 2,
    offsetY: (viewport.height - source.height * safeScale) / 2,
  }
}

export function fitViewer(viewport: ViewerSize, source: ViewerSize) {
  return centeredView(viewport, source, fitScale(viewport, source), 'fit')
}

export function actualSizeViewer(viewport: ViewerSize, source: ViewerSize) {
  return centeredView(viewport, source, 1, 'actual')
}

export function viewerScaleLimits(viewport: ViewerSize, source: ViewerSize) {
  const fitted = fitScale(viewport, source)
  return {
    min: Math.min(0.01, fitted),
    max: Math.max(32, fitted),
  }
}

export function clampViewerScale(scale: number, viewport: ViewerSize, source: ViewerSize) {
  const limits = viewerScaleLimits(viewport, source)
  const finiteScale = Number.isFinite(scale) ? scale : fitScale(viewport, source)
  return Math.min(limits.max, Math.max(limits.min, finiteScale))
}

export function sourceToViewport(point: ViewerPoint, view: ViewerView): ViewerPoint {
  return {
    x: view.offsetX + point.x * view.scale,
    y: view.offsetY + point.y * view.scale,
  }
}

export function viewportToSource(point: ViewerPoint, view: ViewerView): ViewerPoint {
  const scale = Math.max(0.000001, view.scale)
  return {
    x: (point.x - view.offsetX) / scale,
    y: (point.y - view.offsetY) / scale,
  }
}

export function sourceRectToViewport(rect: ViewerRect, view: ViewerView): ViewerRect {
  const origin = sourceToViewport(rect, view)
  return {
    ...origin,
    width: rect.width * view.scale,
    height: rect.height * view.scale,
  }
}

export function zoomViewerAt(
  view: ViewerView,
  anchor: ViewerPoint,
  requestedScale: number,
  viewport: ViewerSize,
  source: ViewerSize,
): ViewerView {
  const sourceAtAnchor = viewportToSource(anchor, view)
  const scale = clampViewerScale(requestedScale, viewport, source)
  return {
    mode: 'custom',
    scale,
    offsetX: anchor.x - sourceAtAnchor.x * scale,
    offsetY: anchor.y - sourceAtAnchor.y * scale,
  }
}

export function panViewer(view: ViewerView, delta: ViewerPoint): ViewerView {
  return {
    ...view,
    mode: 'custom',
    offsetX: view.offsetX + delta.x,
    offsetY: view.offsetY + delta.y,
  }
}

export function resizeViewer(
  view: ViewerView,
  previousViewport: ViewerSize,
  viewport: ViewerSize,
  source: ViewerSize,
): ViewerView {
  if (view.mode === 'fit') {
    return fitViewer(viewport, source)
  }
  if (view.mode === 'actual') {
    return actualSizeViewer(viewport, source)
  }
  const previousCenter = { x: previousViewport.width / 2, y: previousViewport.height / 2 }
  const sourceAtCenter = viewportToSource(previousCenter, view)
  const nextCenter = { x: viewport.width / 2, y: viewport.height / 2 }
  return {
    ...view,
    offsetX: nextCenter.x - sourceAtCenter.x * view.scale,
    offsetY: nextCenter.y - sourceAtCenter.y * view.scale,
  }
}

export function canvasBackingSize(viewport: ViewerSize, devicePixelRatio: number, maxPixels = 3840 * 2160) {
  const requestedDpr = Math.min(2, Math.max(1, Number.isFinite(devicePixelRatio) ? devicePixelRatio : 1))
  const requestedPixels = Math.max(1, viewport.width * requestedDpr) * Math.max(1, viewport.height * requestedDpr)
  const budgetScale = requestedPixels > maxPixels ? Math.sqrt(maxPixels / requestedPixels) : 1
  const renderDpr = requestedDpr * budgetScale
  return {
    width: Math.max(1, Math.round(viewport.width * renderDpr)),
    height: Math.max(1, Math.round(viewport.height * renderDpr)),
    renderDpr,
  }
}

export function resizeRectWithLockedAspect(
  rect: ViewerRect,
  bounds: ViewerSize,
  corner: ViewerCorner,
  delta: ViewerPoint,
  minimumSize = 8,
): ViewerRect {
  const movesLeft = corner === 'tl' || corner === 'bl'
  const movesTop = corner === 'tl' || corner === 'tr'
  const desiredWidth = rect.width + (movesLeft ? -delta.x : delta.x)
  const desiredHeight = rect.height + (movesTop ? -delta.y : delta.y)
  const widthScale = desiredWidth / safeDimension(rect.width)
  const heightScale = desiredHeight / safeDimension(rect.height)
  const requestedScale = Math.abs(widthScale - 1) >= Math.abs(heightScale - 1) ? widthScale : heightScale
  const fixedX = movesLeft ? rect.x + rect.width : rect.x
  const fixedY = movesTop ? rect.y + rect.height : rect.y
  const maxWidth = movesLeft ? fixedX : bounds.width - fixedX
  const maxHeight = movesTop ? fixedY : bounds.height - fixedY
  const minScale = Math.max(minimumSize / safeDimension(rect.width), minimumSize / safeDimension(rect.height))
  const maxScale = Math.min(maxWidth / safeDimension(rect.width), maxHeight / safeDimension(rect.height))
  const scale = Math.min(Math.max(minScale, maxScale), Math.max(minScale, requestedScale))
  const makeEven = (value: number) => {
    const rounded = Math.max(minimumSize, Math.round(value))
    return rounded % 2 === 0 ? rounded : rounded - 1
  }
  const width = makeEven(Math.min(maxWidth, rect.width * scale))
  const height = makeEven(Math.min(maxHeight, rect.height * scale))
  return {
    x: movesLeft ? fixedX - width : fixedX,
    y: movesTop ? fixedY - height : fixedY,
    width,
    height,
  }
}

const evenFloor = (value: number) => {
  const integer = Math.max(0, Math.floor(Number.isFinite(value) ? value : 0))
  return integer - (integer % 2)
}

/**
 * Applies the same even-pixel crop constraints used by the FFmpeg export path.
 * Keeping this normalization in the interaction layer prevents the inspector
 * from promising coordinates that the exporter would silently alter.
 */
export function clampCropRect(
  rect: ViewerRect,
  bounds: ViewerSize,
  mode: 'move' | ViewerCorner = 'move',
  minimumSize = 8,
): ViewerRect {
  const maxRight = Math.max(2, evenFloor(bounds.width))
  const maxBottom = Math.max(2, evenFloor(bounds.height))
  const minimum = Math.max(2, evenFloor(minimumSize))
  const width = Math.min(maxRight, Math.max(minimum, evenFloor(rect.width)))
  const height = Math.min(maxBottom, Math.max(minimum, evenFloor(rect.height)))

  if (mode === 'move') {
    return {
      x: Math.min(maxRight - width, evenFloor(rect.x)),
      y: Math.min(maxBottom - height, evenFloor(rect.y)),
      width,
      height,
    }
  }

  const movesLeft = mode === 'tl' || mode === 'bl'
  const movesTop = mode === 'tl' || mode === 'tr'
  const rawFixedX = evenFloor(movesLeft ? rect.x + rect.width : rect.x)
  const rawFixedY = evenFloor(movesTop ? rect.y + rect.height : rect.y)
  const fixedX = movesLeft
    ? Math.min(maxRight, Math.max(minimum, rawFixedX))
    : Math.min(maxRight - minimum, rawFixedX)
  const fixedY = movesTop
    ? Math.min(maxBottom, Math.max(minimum, rawFixedY))
    : Math.min(maxBottom - minimum, rawFixedY)
  const availableWidth = movesLeft ? fixedX : maxRight - fixedX
  const availableHeight = movesTop ? fixedY : maxBottom - fixedY
  const resizedWidth = Math.min(Math.max(minimum, width), Math.max(minimum, availableWidth))
  const resizedHeight = Math.min(Math.max(minimum, height), Math.max(minimum, availableHeight))

  return {
    x: movesLeft ? fixedX - resizedWidth : fixedX,
    y: movesTop ? fixedY - resizedHeight : fixedY,
    width: resizedWidth,
    height: resizedHeight,
  }
}
