export type FrameRequest = {
  clipEpoch: number
  requestId: number
  secondsAt: number
}

export type TimelineFrameSegment = {
  start: number
  end: number
  sourceStart: number
  sourceEnd: number
}

export function frameDuration(frameRate: number) {
  return 1 / Math.max(1, Number.isFinite(frameRate) ? frameRate : 1)
}

export function isFrameAtTime(actualSeconds: number, requestedSeconds: number, frameRate: number) {
  return Number.isFinite(actualSeconds)
    && Number.isFinite(requestedSeconds)
    && Math.abs(actualSeconds - requestedSeconds) <= Math.max(frameDuration(frameRate), 0.01)
}

export function isCurrentFrameRequest(
  request: FrameRequest,
  currentClipEpoch: number,
  currentRequestId: number,
  currentSeconds: number,
  frameRate: number,
) {
  return request.clipEpoch === currentClipEpoch
    && request.requestId === currentRequestId
    && isFrameAtTime(request.secondsAt, currentSeconds, frameRate)
}

export function cachedPreviewIndex(secondsAt: number, fps: number, frameCount: number) {
  if (!Number.isFinite(secondsAt) || secondsAt < 0 || !Number.isFinite(fps) || fps <= 0 || frameCount <= 0) {
    return null
  }
  const index = Math.round(secondsAt * fps)
  return index >= 0 && index < frameCount ? index : null
}

export function adjacentSourceFrame(
  currentSourceSeconds: number,
  direction: -1 | 1,
  frameRate: number,
  clips: TimelineFrameSegment[],
) {
  if (clips.length === 0) {
    return 0
  }
  const currentClip = clips.find((clip) => currentSourceSeconds >= clip.sourceStart && currentSourceSeconds <= clip.sourceEnd)
  const previousClip = [...clips].reverse().find((clip) => currentSourceSeconds >= clip.sourceEnd)
  const currentTimeline = currentClip
    ? currentClip.start + (currentSourceSeconds - currentClip.sourceStart)
    : previousClip?.end ?? clips[0].start
  const start = clips[0].start
  const end = clips[clips.length - 1].end
  const nextTimeline = Math.min(end, Math.max(start, currentTimeline + direction * frameDuration(frameRate)))
  const nextClip = clips.find((clip) => nextTimeline >= clip.start && nextTimeline <= clip.end)
  if (nextClip) {
    return nextClip.sourceStart + (nextTimeline - nextClip.start)
  }
  const priorClip = [...clips].reverse().find((clip) => nextTimeline >= clip.end)
  return priorClip?.sourceEnd ?? clips[0].sourceStart
}
