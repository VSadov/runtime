;; Licensed to the .NET Foundation under one or more agreements.
;; The .NET Foundation licenses this file to you under the MIT license.

#include "AsmMacros.h"

    TEXTAREA

    EXTERN      RhpGcPoll2

    ;; Build a map of symbols representing offsets into the transition frame (see PInvokeTransitionFrame in
    ;; rhbinder.h) and keep these two in sync.
    map 0
            field OFFSETOF__PInvokeTransitionFrame__m_PreservedRegs
            field 10 * 8 ; x19..x28
m_CallersSP field 8      ; SP at routine entry
            field 8  * 8 ; x0..x7
            field 8      ; padding for 16-byte alignment of q regs
            field 8  * 16; q0..q7
PROBE_FRAME_SIZE    field 0

    ;; See PUSH_COOP_PINVOKE_FRAME, this macro is very similar, but also saves return/argument registers
    ;; and accepts the register bitmask
    ;; Call this macro first in the method (no further prolog instructions can be added after this).
    ;;
    ;;  $threadReg     : register containing the Thread* (this will be preserved).
    ;;  $trashReg      : register that can be trashed by this macro
    ;;  $BITMASK       : value to initialize m_dwFlags field with (register or #constant)
    MACRO
        PUSH_PROBE_FRAME $threadReg, $trashReg, $BITMASK

        ; Define the method prolog, allocating enough stack space for the PInvokeTransitionFrame and saving
        ; incoming register values into it.

        ;; First create PInvokeTransitionFrame
        PROLOG_SAVE_REG_PAIR   fp, lr, #-(PROBE_FRAME_SIZE)!      ;; Push down stack pointer and store FP and LR

        ;; Slot at [sp, #0x10] is reserved for Thread *
        ;; Slot at [sp, #0x18] is reserved for bitmask of saved registers

        ;; Save callee saved registers
        PROLOG_SAVE_REG_PAIR   x19, x20, #0x20
        PROLOG_SAVE_REG_PAIR   x21, x22, #0x30
        PROLOG_SAVE_REG_PAIR   x23, x24, #0x40
        PROLOG_SAVE_REG_PAIR   x25, x26, #0x50
        PROLOG_SAVE_REG_PAIR   x27, x28, #0x60

        ;; Slot at [sp, #0x70] is reserved for caller sp

        ;; Save the integer return/argument registers
        PROLOG_NOP stp         x0, x1,   [sp, #0x78]
        PROLOG_NOP stp         x2, x3,   [sp, #0x88]
        PROLOG_NOP stp         x4, x5,   [sp, #0x98]
        PROLOG_NOP stp         x6, x7,   [sp, #0xA8]

        ;; Save the FP argument registers
        PROLOG_NOP stp         q0, q1,   [sp, #0xC0]
        PROLOG_NOP stp         q2, q3,   [sp, #0xE0]
        PROLOG_NOP stp         q4, q5,   [sp, #0x100]
        PROLOG_NOP stp         q6, q7,   [sp, #0x120]

        ;; Perform the rest of the PInvokeTransitionFrame initialization.
        ;;   str         $threadReg,[sp, #OFFSETOF__PInvokeTransitionFrame__m_pThread]       ; Thread * (unused by stackwalker)
        ;;   str         $BITMASK,  [sp, #OFFSETOF__PInvokeTransitionFrame__m_Flags]         ; save the register bitmask passed in by caller
        ASSERT OFFSETOF__PInvokeTransitionFrame__m_Flags == (OFFSETOF__PInvokeTransitionFrame__m_pThread + 8)
        stp         $threadReg, $BITMASK, [sp, #OFFSETOF__PInvokeTransitionFrame__m_pThread]

        add         $trashReg, sp,  #PROBE_FRAME_SIZE                                   ; recover value of caller's SP
        str         $trashReg, [sp, #m_CallersSP]                                       ; save caller's SP

        ;; link the frame into the Thread
        mov         $trashReg, sp
        str         $trashReg, [$threadReg, #OFFSETOF__Thread__m_pDeferredTransitionFrame]
    MEND

;;
;; Remove the frame from a previous call to PUSH_PROBE_FRAME from the top of the stack and restore preserved
;; registers and return value to their values from before the probe was called (while also updating any
;; object refs or byrefs).
;;
    MACRO
        POP_PROBE_FRAME

        ;; Restore the integer return/argument registers
        PROLOG_NOP ldp          x0, x1,   [sp, #0x78]
        PROLOG_NOP ldp          x2, x3,   [sp, #0x88]
        PROLOG_NOP ldp          x4, x5,   [sp, #0x98]
        PROLOG_NOP ldp          x6, x7,   [sp, #0xA8]

        ; Restore the FP argument registers
        EPILOG_NOP ldp          q0, q1,   [sp, #0xC0]
        EPILOG_NOP ldp          q2, q3,   [sp, #0xE0]
        EPILOG_NOP ldp          q4, q5,   [sp, #0x100]
        EPILOG_NOP ldp          q6, q7,   [sp, #0x120]

        ;; Restore callee saved registers
        EPILOG_RESTORE_REG_PAIR x19, x20, #0x20
        EPILOG_RESTORE_REG_PAIR x21, x22, #0x30
        EPILOG_RESTORE_REG_PAIR x23, x24, #0x40
        EPILOG_RESTORE_REG_PAIR x25, x26, #0x50
        EPILOG_RESTORE_REG_PAIR x27, x28, #0x60

        EPILOG_RESTORE_REG_PAIR fp, lr, #(PROBE_FRAME_SIZE)!
    MEND

;;
;; The prolog for all GC suspension hijacks (normal and stress). Fixes up the hijacked return address, and
;; clears the hijack state.
;;
;; Register state on entry:
;;  All registers correct for return to the original return address.
;;
;; Register state on exit:
;;  x9: thread pointer
;;  x0-x7 preserved, x10, x16 and x17 trashed
;;
    MACRO
        FixupHijackedCallstack

        ;; x9 <- GetThread(), TRASHES x10
        INLINE_GETTHREAD x9, x10

        ;;
        ;; Fix the stack by restoring and authenticating the original return address.
        ;;
        ldr         lr, [x9, #OFFSETOF__Thread__m_pvHijackedReturnAddress]
        ldr         x16, [x9, #OFFSETOF__Thread__m_pSpForPacSign]
        cbz         x16, %ft1
        ;; Code compiled with return address signing can run on hardware without pointer authentication,
        ;; where autib is undefined. autib1716 is in the hint space, so it is a NOP there.
        mov         x17, lr
        DCD         0xD50321DF  ;; autib1716 instruction in binary to avoid requiring PAC-enabled assemblers
        mov         lr, x17

1
        ;;
        ;; Clear hijack state
        ;;
        ASSERT OFFSETOF__Thread__m_pvHijackedReturnAddress == (OFFSETOF__Thread__m_ppvHijackedReturnAddressLocation + 8)
        ;; Clear m_ppvHijackedReturnAddressLocation, m_pvHijackedReturnAddress, and m_pSpForPacSign
        stp         xzr, xzr, [x9, #OFFSETOF__Thread__m_ppvHijackedReturnAddressLocation]
        str         xzr, [x9, #OFFSETOF__Thread__m_pSpForPacSign]
    MEND

    MACRO
        HijackTargetFakeProlog

        ;; This is a fake entrypoint for the method that 'tricks' the OS into calling our personality routine.
        ;; The code here should never be executed, and the unwind info is bogus, but we don't mind since the
        ;; stack is broken by the hijack anyway until after we fix it below.
        PROLOG_SAVE_REG_PAIR   fp, lr, #-0x10!
        nop                     ; We also need a nop here to simulate the implied bl instruction.  Without
                                ; this, an OS-applied -4 will back up into the method prolog and the unwind
                                ; will not be applied as desired.

    MEND

;;
;; GC Probe Hijack target
;;

    NESTED_ENTRY RhpGcProbeHijackWrapper, .text
        HijackTargetFakeProlog

    LABELED_RETURN_ADDRESS RhpGcProbeHijack
        FixupHijackedCallstack

        ldr         x10, =RhpTrapThreads
        ldr         w10, [x10]
        tbnz        x10, #TrapThreadsFlags_TrapThreads_Bit, WaitForGC
        ret

WaitForGC
        mov         x12, #(DEFAULT_FRAME_SAVE_FLAGS + PTFF_SAVE_X0 + PTFF_SAVE_X1 + PTFF_SAVE_X2 + PTFF_SAVE_X3 + PTFF_SAVE_X4 + PTFF_SAVE_X5 + PTFF_SAVE_X6 + PTFF_SAVE_X7)
        movk        x12, #PTFF_THREAD_HIJACK_HI, lsl #32
        b           RhpWaitForGC
    NESTED_END RhpGcProbeHijackWrapper

    EXTERN RhpThrowHwEx

    NESTED_ENTRY RhpWaitForGC
        PUSH_PROBE_FRAME x9, x10, x12

        ldr         x0, [x9, #OFFSETOF__Thread__m_pDeferredTransitionFrame]
        bl          RhpWaitForGC2

        POP_PROBE_FRAME
        EPILOG_RETURN
    NESTED_END RhpWaitForGC

    NESTED_ENTRY RhpGcPollRare
        PUSH_COOP_PINVOKE_FRAME x0
        bl          RhpGcPoll2
        POP_COOP_PINVOKE_FRAME
        ret
    NESTED_END RhpGcPollRare

;; The frame of RhpGcPollPreservingRegsRare: the fixed part of the PInvokeTransitionFrame (fp, lr, m_pThread, m_Flags),
;; x19-x28, the caller's SP and the scratch registers that the poll preserves, x0-x11 and x13-x15.
SIGNALLESS_POLL_FRAME_SIZE  equ 0xF0
SIGNALLESS_POLL_FRAME_FLAGS equ DEFAULT_FRAME_SAVE_FLAGS + PTFF_SAVE_X0 + PTFF_SAVE_X1 + PTFF_SAVE_X2 + PTFF_SAVE_X3 + PTFF_SAVE_X4 + PTFF_SAVE_X5 + PTFF_SAVE_X6 + PTFF_SAVE_X7 + PTFF_SAVE_X8 + PTFF_SAVE_X9 + PTFF_SAVE_X10 + PTFF_SAVE_X11 + PTFF_SAVE_X13 + PTFF_SAVE_X14 + PTFF_SAVE_X15

;;
;; GC poll helper of signalless mode (ILC --signalless). Compiled code expects it to preserve all general purpose
;; registers other than x12, x16, x17 and lr (RBM_SIGNALLESS_POLL_GC_TRASH in the JIT), and the GC info at the return
;; address of the call describes the GC references in any of them.
;;
    LEAF_ENTRY RhpGcPollPreservingRegs
        ldr         x16, =RhpTrapThreads
        ldr         w16, [x16]
        cbnz        w16, RhpGcPollPreservingRegs_RarePath
        ret
RhpGcPollPreservingRegs_RarePath
        b           RhpGcPollPreservingRegsRare
    LEAF_END RhpGcPollPreservingRegs

;;
;; Saves the registers of the method that polled in a transition frame that marks the method as the active frame, as a
;; return address hijack does. The GC reports the references in the registers through the frame, and may update them
;; there, so they are reloaded from it.
;;
    NESTED_ENTRY RhpGcPollPreservingRegsRare
        PROLOG_SAVE_REG_PAIR   fp, lr, #-SIGNALLESS_POLL_FRAME_SIZE!

        ;; Slot at [sp, #0x10] is reserved for Thread *
        ;; Slot at [sp, #0x18] is reserved for bitmask of saved registers

        ;; Save callee saved registers
        PROLOG_SAVE_REG_PAIR   x19, x20, #0x20
        PROLOG_SAVE_REG_PAIR   x21, x22, #0x30
        PROLOG_SAVE_REG_PAIR   x23, x24, #0x40
        PROLOG_SAVE_REG_PAIR   x25, x26, #0x50
        PROLOG_SAVE_REG_PAIR   x27, x28, #0x60

        ;; Slot at [sp, #0x70] is reserved for caller sp

        ;; Save the scratch registers
        stp         x0, x1,   [sp, #0x78]
        stp         x2, x3,   [sp, #0x88]
        stp         x4, x5,   [sp, #0x98]
        stp         x6, x7,   [sp, #0xA8]
        stp         x8, x9,   [sp, #0xB8]
        stp         x10, x11, [sp, #0xC8]
        stp         x13, x14, [sp, #0xD8]
        str         x15,      [sp, #0xE8]

        add         x16, sp, #SIGNALLESS_POLL_FRAME_SIZE
        str         x16, [sp, #0x70]
        movz        x16, #(SIGNALLESS_POLL_FRAME_FLAGS & 0xFFFF)
        movk        x16, #(SIGNALLESS_POLL_FRAME_FLAGS >> 16), lsl #16
        movk        x16, #PTFF_THREAD_HIJACK_HI, lsl #32
        str         x16, [sp, #OFFSETOF__PInvokeTransitionFrame__m_Flags]

        mov         x0, sp
        bl          RhpGcPoll2

        ldp         x0, x1,   [sp, #0x78]
        ldp         x2, x3,   [sp, #0x88]
        ldp         x4, x5,   [sp, #0x98]
        ldp         x6, x7,   [sp, #0xA8]
        ldp         x8, x9,   [sp, #0xB8]
        ldp         x10, x11, [sp, #0xC8]
        ldp         x13, x14, [sp, #0xD8]
        ldr         x15,      [sp, #0xE8]

        EPILOG_RESTORE_REG_PAIR x19, x20, #0x20
        EPILOG_RESTORE_REG_PAIR x21, x22, #0x30
        EPILOG_RESTORE_REG_PAIR x23, x24, #0x40
        EPILOG_RESTORE_REG_PAIR x25, x26, #0x50
        EPILOG_RESTORE_REG_PAIR x27, x28, #0x60
        EPILOG_RESTORE_REG_PAIR fp, lr, #SIGNALLESS_POLL_FRAME_SIZE!
        EPILOG_RETURN
    NESTED_END RhpGcPollPreservingRegsRare


    end
