import assert from 'node:assert/strict'
import test from 'node:test'
import { runExportJob } from '../src/exportJob.ts'
import type { ExportInvoke, ExportOperation } from '../src/exportJob.ts'

test('stopping before export setup prevents any backend work', async () => {
  const invoke: ExportInvoke = async () => { throw new Error('unexpected invocation') }
  await assert.rejects(runExportJob(invoke, { jobId: null, stopRequested: true }, 'export_clip', {}), /Export stopped/)
})

test('a stop while reserving is delivered to the same job and awaits worker cleanup', async () => {
  const operation: ExportOperation = { jobId: null, stopRequested: false }
  let reserve!: (id: string) => void
  let finish!: () => void
  let cancelledId: unknown
  let settled = false
  const invoke: ExportInvoke = <T>(command: string, args?: Record<string, unknown>) => {
    if (command === 'begin_export') return new Promise<string>((resolve) => { reserve = resolve }) as Promise<T>
    if (command === 'cancel_export') {
      cancelledId = args?.jobId
      return Promise.resolve(true) as Promise<T>
    }
    assert.equal(args?.jobId, 'job-1')
    return new Promise<T>((_, reject) => { finish = () => reject('Export stopped') })
  }
  const work = runExportJob(invoke, operation, 'export_clip', {}).finally(() => { settled = true })
  operation.stopRequested = true
  reserve('job-1')
  await new Promise((resolve) => setImmediate(resolve))
  assert.equal(cancelledId, 'job-1')
  assert.equal(settled, false)
  finish()
  await assert.rejects(work, (error) => error === 'Export stopped')
})

test('successful jobs return their export result and pass the request unchanged', async () => {
  const request = { inputPath: 'source.mp4' }
  const result = { path: 'clip.mp4', bytes: 200 }
  const invoke: ExportInvoke = async <T>(command: string, args?: Record<string, unknown>) => {
    if (command === 'begin_export') return 'job-2' as T
    assert.equal(command, 'export_clip')
    assert.deepEqual(args, { jobId: 'job-2', request })
    return result as T
  }
  assert.equal(await runExportJob(invoke, { jobId: null, stopRequested: false }, 'export_clip', request), result)
})
