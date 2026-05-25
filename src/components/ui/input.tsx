import * as React from 'react'
import { cn } from '@/lib/utils'

export function Input({ className, ...props }: React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      className={cn(
        'h-8 w-full rounded-md border border-[#343842] bg-[#101217] px-2 text-sm text-[#e8edf4] outline-none transition-colors placeholder:text-[#68707d] focus:border-[#4c8fd4]',
        className,
      )}
      {...props}
    />
  )
}
