import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const buttonVariants = cva(
  'inline-flex h-8 shrink-0 items-center justify-center gap-2 rounded-md border px-3 text-sm font-medium outline-none transition-colors disabled:pointer-events-none disabled:opacity-45 [&_svg]:size-4',
  {
    variants: {
      variant: {
        default: 'border-[#3a3d45] bg-[#2d3138] text-[#f4f7fb] hover:bg-[#383d46]',
        primary: 'border-[#2f82d0] bg-[#2775bd] text-white hover:bg-[#3388d8]',
        ghost: 'border-transparent bg-transparent text-[#c5ccd6] hover:bg-[#252932]',
        danger: 'border-[#784040] bg-[#3b1f23] text-[#ffb4b4] hover:bg-[#51292f]',
        subtle: 'border-[#2d3038] bg-[#1d2027] text-[#d7dde7] hover:bg-[#262a33]',
      },
      size: {
        sm: 'h-7 px-2 text-xs',
        md: 'h-8 px-3',
        icon: 'size-8 px-0',
      },
    },
    defaultVariants: {
      variant: 'default',
      size: 'md',
    },
  },
)

export type ButtonProps = React.ButtonHTMLAttributes<HTMLButtonElement> &
  VariantProps<typeof buttonVariants>

export function Button({ className, variant, size, ...props }: ButtonProps) {
  return <button className={cn(buttonVariants({ variant, size }), className)} {...props} />
}
