// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <unixasmmacros.inc>
#include "AsmOffsets.inc"

.arch_extension pauth

#define PROBE_FRAME_SIZE 0x140  // 4 * 8  for fixed part of PInvokeTransitionFrame (fp, lr, m_pThread, m_Flags) +
                                // 10 * 8 for callee saved registers +
                                // 1 * 8  for caller SP +
                                // 8 * 8  for int return/argument regs (x0..x7) +
                                // 1 * 8  for padding (16-byte alignment for q regs) +
                                // 8 * 16 for FP argument registers (q0..q7)

// See PUSH_COOP_PINVOKE_FRAME, this macro is very similar, but also saves return/argument registers
// and accepts the register bitmask
// Call this macro first in the method (no further prolog instructions can be added after this).
//
//  threadReg     : register containing the Thread* (this will be preserved).
//  trashReg      : register that can be trashed by this macro
//  BITMASK       : value to initialize m_dwFlags field with (register or #constant)
.macro PUSH_PROBE_FRAME threadReg, trashReg, BITMASK

    // Define the method prolog, allocating enough stack space for the PInvokeTransitionFrame and saving
    // incoming register values into it.

    // First create PInvokeTransitionFrame
    PROLOG_SAVE_REG_PAIR_INDEXED   fp, lr, -PROBE_FRAME_SIZE      // Push down stack pointer and store FP and LR

    // Slot at [sp, #0x10] is reserved for Thread *
    // Slot at [sp, #0x18] is reserved for bitmask of saved registers

    // Save callee saved registers
    PROLOG_SAVE_REG_PAIR   x19, x20, 0x20
    PROLOG_SAVE_REG_PAIR   x21, x22, 0x30
    PROLOG_SAVE_REG_PAIR   x23, x24, 0x40
    PROLOG_SAVE_REG_PAIR   x25, x26, 0x50
    PROLOG_SAVE_REG_PAIR   x27, x28, 0x60

    // Slot at [sp, #0x70] is reserved for caller sp

    // Save the integer return/argument registers
    stp         x0, x1,   [sp, #0x78]
    stp         x2, x3,   [sp, #0x88]
    stp         x4, x5,   [sp, #0x98]
    stp         x6, x7,   [sp, #0xA8]

    // Save the FP argument registers
    SAVE_FLOAT_ARGUMENT_REGISTERS sp, 0xC0

    // Perform the rest of the PInvokeTransitionFrame initialization.
    //   str         \threadReg,[sp, #OFFSETOF__PInvokeTransitionFrame__m_pThread]       // Thread * (unused by stackwalker)
    //   str         \BITMASK,  [sp, #OFFSETOF__PInvokeTransitionFrame__m_Flags]         // save the register bitmask passed in by caller
    stp         \threadReg, \BITMASK, [sp, #OFFSETOF__PInvokeTransitionFrame__m_pThread]

    add         \trashReg, sp,  #PROBE_FRAME_SIZE                                   // recover value of caller's SP
    str         \trashReg, [sp, #0x70]                                              // save caller's SP

    // link the frame into the Thread
    mov         \trashReg, sp
    str         \trashReg, [\threadReg, #OFFSETOF__Thread__m_pDeferredTransitionFrame]
.endm

//
// Remove the frame from a previous call to PUSH_PROBE_FRAME from the top of the stack and restore preserved
// registers and return value to their values from before the probe was called (while also updating any
// object refs or byrefs).
//
.macro POP_PROBE_FRAME

    // Restore the integer return/argument registers
    ldp         x0, x1,   [sp, #0x78]
    ldp         x2, x3,   [sp, #0x88]
    ldp         x4, x5,   [sp, #0x98]
    ldp         x6, x7,   [sp, #0xA8]

    // Restore the FP argument registers
    RESTORE_FLOAT_ARGUMENT_REGISTERS sp, 0xC0

    // Restore callee saved registers
    EPILOG_RESTORE_REG_PAIR x19, x20, 0x20
    EPILOG_RESTORE_REG_PAIR x21, x22, 0x30
    EPILOG_RESTORE_REG_PAIR x23, x24, 0x40
    EPILOG_RESTORE_REG_PAIR x25, x26, 0x50
    EPILOG_RESTORE_REG_PAIR x27, x28, 0x60

    EPILOG_RESTORE_REG_PAIR_INDEXED   fp, lr, PROBE_FRAME_SIZE
.endm

//
// The prolog for all GC suspension hijacks (normal and stress). Fixes up the hijacked return address, and
// clears the hijack state.
//
// Register state on entry:
//  All registers correct for return to the original return address.
//
// Register state on exit:
//  x9: thread pointer
//  x0-x7, q0-q7 preserved, x10, x16 and x17 trashed
//
.macro FixupHijackedCallstack

    // x9 <- GetThread()

#ifdef FEATURE_EMULATED_TLS
    // Save x0-x7 and q0-q7 around RhpGetThread call (it trashes all scratch registers)
    PROLOG_SAVE_REG_PAIR_INDEXED   fp, lr, -0xD0
    stp         x0, x1, [sp, #0x10]
    stp         x2, x3, [sp, #0x20]
    stp         x4, x5, [sp, #0x30]
    stp         x6, x7, [sp, #0x40]
    SAVE_FLOAT_ARGUMENT_REGISTERS sp, 0x50

    bl C_FUNC(RhpGetThread)
    mov x9, x0

    RESTORE_FLOAT_ARGUMENT_REGISTERS sp, 0x50
    ldp         x0, x1, [sp, #0x10]
    ldp         x2, x3, [sp, #0x20]
    ldp         x4, x5, [sp, #0x30]
    ldp         x6, x7, [sp, #0x40]
    EPILOG_RESTORE_REG_PAIR_INDEXED   fp, lr, 0xD0
#else
    // INLINE_GETTHREAD makes blr calls that trash volatile FP registers.
    // Save and restore q0-q7 around the call.
    sub         sp, sp, #0x80
    .cfi_adjust_cfa_offset 0x80
    SAVE_FLOAT_ARGUMENT_REGISTERS sp, 0

    INLINE_GETTHREAD x9

    RESTORE_FLOAT_ARGUMENT_REGISTERS sp, 0
    add         sp, sp, #0x80
    .cfi_adjust_cfa_offset -0x80
#endif

    //
    // Fix the stack by restoring and authenticating the original return address.
    //
    ldr         lr, [x9, #OFFSETOF__Thread__m_pvHijackedReturnAddress]
    ldr         x16, [x9, #OFFSETOF__Thread__m_pSpForPacSign]
    cbz         x16, 1f
    // Code compiled with return address signing can run on hardware without pointer authentication,
    // where autia is undefined. autia1716 is in the hint space, so it is a NOP there.
    mov         x17, lr
    autia1716
    mov         lr, x17

1:
    //
    // Clear hijack state
    //
    // Clear m_ppvHijackedReturnAddressLocation, m_pvHijackedReturnAddress, and m_pSpForPacSign
    stp         xzr, xzr, [x9, #OFFSETOF__Thread__m_ppvHijackedReturnAddressLocation]
    str         xzr, [x9, #OFFSETOF__Thread__m_pSpForPacSign]
.endm

//
// GC Probe Hijack target
//
NESTED_ENTRY RhpGcProbeHijack, _TEXT, NoHandler
    FixupHijackedCallstack

    PREPARE_EXTERNAL_VAR_INDIRECT_W RhpTrapThreads, 10
    tbnz        x10, #TrapThreadsFlags_TrapThreads_Bit, LOCAL_LABEL(WaitForGC)
    ret

LOCAL_LABEL(WaitForGC):
    mov         x12, DEFAULT_FRAME_SAVE_FLAGS + PTFF_SAVE_X0 + PTFF_SAVE_X1 + PTFF_SAVE_X2 + PTFF_SAVE_X3 + PTFF_SAVE_X4 + PTFF_SAVE_X5 + PTFF_SAVE_X6 + PTFF_SAVE_X7
    movk        x12, PTFF_THREAD_HIJACK_HI, lsl #32
    b           C_FUNC(RhpWaitForGC)
NESTED_END RhpGcProbeHijack

.global C_FUNC(RhpThrowHwEx)

NESTED_ENTRY RhpWaitForGC, _TEXT, NoHandler
    PUSH_PROBE_FRAME x9, x10, x12

    ldr         x0, [x9, #OFFSETOF__Thread__m_pDeferredTransitionFrame]
    bl          C_FUNC(RhpWaitForGC2)

    POP_PROBE_FRAME
    EPILOG_RETURN
NESTED_END RhpWaitForGC

.global C_FUNC(RhpGcPoll2)

NESTED_ENTRY RhpGcPollRare, _TEXT, NoHandler
    PUSH_COOP_PINVOKE_FRAME x0
    bl C_FUNC(RhpGcPoll2)
    POP_COOP_PINVOKE_FRAME
    ret
NESTED_END RhpGcPollRare

// The frame of RhpGcPollPreservingRegsRare: the fixed part of the PInvokeTransitionFrame (fp, lr, m_pThread, m_Flags),
// x19-x28, the caller's SP and the scratch registers that the poll preserves, x0-x11 and x13-x15.
#define SIGNALLESS_POLL_FRAME_SIZE 0xF0
#define SIGNALLESS_POLL_FRAME_FLAGS (DEFAULT_FRAME_SAVE_FLAGS + PTFF_SAVE_X0 + PTFF_SAVE_X1 + PTFF_SAVE_X2 + PTFF_SAVE_X3 + PTFF_SAVE_X4 + PTFF_SAVE_X5 + PTFF_SAVE_X6 + PTFF_SAVE_X7 + PTFF_SAVE_X8 + PTFF_SAVE_X9 + PTFF_SAVE_X10 + PTFF_SAVE_X11 + PTFF_SAVE_X13 + PTFF_SAVE_X14 + PTFF_SAVE_X15)

//
// GC poll helper of signalless mode (ILC --signalless). Compiled code expects it to preserve all general purpose
// registers other than x12, x16, x17 and lr (RBM_SIGNALLESS_POLL_GC_TRASH in the JIT), and the GC info at the return
// address of the call describes the GC references in any of them.
//
LEAF_ENTRY RhpGcPollPreservingRegs, _TEXT
    PREPARE_EXTERNAL_VAR_INDIRECT_W RhpTrapThreads, 16
    cbnz    w16, LOCAL_LABEL(RhpGcPollPreservingRegs_RarePath)
    ret
LOCAL_LABEL(RhpGcPollPreservingRegs_RarePath):
    b       C_FUNC(RhpGcPollPreservingRegsRare)
LEAF_END RhpGcPollPreservingRegs, _TEXT

//
// Saves the registers of the method that polled in a transition frame that marks the method as the active frame, as a
// return address hijack does. The GC reports the references in the registers through the frame, and may update them
// there, so they are reloaded from it.
//
NESTED_ENTRY RhpGcPollPreservingRegsRare, _TEXT, NoHandler
    PROLOG_SAVE_REG_PAIR_INDEXED   fp, lr, -SIGNALLESS_POLL_FRAME_SIZE

    // Slot at [sp, #0x10] is reserved for Thread *
    // Slot at [sp, #0x18] is reserved for bitmask of saved registers

    // Save callee saved registers
    PROLOG_SAVE_REG_PAIR   x19, x20, 0x20
    PROLOG_SAVE_REG_PAIR   x21, x22, 0x30
    PROLOG_SAVE_REG_PAIR   x23, x24, 0x40
    PROLOG_SAVE_REG_PAIR   x25, x26, 0x50
    PROLOG_SAVE_REG_PAIR   x27, x28, 0x60

    // Slot at [sp, #0x70] is reserved for caller sp

    // Save the scratch registers
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
    bl          C_FUNC(RhpGcPoll2)

    ldp         x0, x1,   [sp, #0x78]
    ldp         x2, x3,   [sp, #0x88]
    ldp         x4, x5,   [sp, #0x98]
    ldp         x6, x7,   [sp, #0xA8]
    ldp         x8, x9,   [sp, #0xB8]
    ldp         x10, x11, [sp, #0xC8]
    ldp         x13, x14, [sp, #0xD8]
    ldr         x15,      [sp, #0xE8]

    EPILOG_RESTORE_REG_PAIR x19, x20, 0x20
    EPILOG_RESTORE_REG_PAIR x21, x22, 0x30
    EPILOG_RESTORE_REG_PAIR x23, x24, 0x40
    EPILOG_RESTORE_REG_PAIR x25, x26, 0x50
    EPILOG_RESTORE_REG_PAIR x27, x28, 0x60
    EPILOG_RESTORE_REG_PAIR_INDEXED fp, lr, SIGNALLESS_POLL_FRAME_SIZE
    EPILOG_RETURN
NESTED_END RhpGcPollPreservingRegsRare
