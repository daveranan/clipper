export type ExportOperation = {
  jobId: string | null
  stopRequested: boolean
}

export type ExportInvoke = <T>(command: string, args?: Record<string, unknown>) => Promise<T>

// Reserve the backend job before enabling cancellation by ID. A stop requested
// during setup is sent as soon as the reservation arrives, so it cannot be lost.
export async function runExportJob<T>(invoke: ExportInvoke, operation: ExportOperation, command: string, request: unknown): Promise<T> {
  if (operation.stopRequested) throw new Error('Export stopped')
  operation.jobId = await invoke<string>('begin_export')
  // Always consume the reservation and await the worker, even if stop IPC fails.
  const work = invoke<T>(command, { request, jobId: operation.jobId })
  if (operation.stopRequested) {
    const [result, cancellation] = await Promise.allSettled([
      work,
      invoke('cancel_export', { jobId: operation.jobId }),
    ])
    if (result.status === 'rejected') throw result.reason
    if (cancellation.status === 'rejected') throw cancellation.reason
    return result.value
  }
  return work
}
