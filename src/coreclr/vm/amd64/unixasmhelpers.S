// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

.intel_syntax noprefix
#include "unixasmmacros.inc"
#include "asmconstants.h"

//////////////////////////////////////////////////////////////////////////
//
// PInvokeImportThunk
//
// The call in PInvokeImportPrecode points to this function
//
// EXTERN_C VOID __stdcall PInvokeImportThunk()//
NESTED_ENTRY PInvokeImportThunk, _TEXT, NoHandler

        //
        // Save integer parameter registers.
        // Make sure to preserve r11 as well as it is used to pass the stack argument size from JIT
        // Make sure to preserve rax as well as it is used for the return buffer for Swift calls
        //
        PUSH_ARGUMENT_REGISTERS
        push_register r11
        push_register rax

        //
        // Allocate space for XMM parameter registers and alignment
        //
        alloc_stack     0x88

        SAVE_FLOAT_ARGUMENT_REGISTERS 0

    END_PROLOGUE

        //
        // Call PInvokeImportWorker w/ the PInvokeMethodDesc*
        //
        mov             rdi, METHODDESC_REGISTER
        call            C_FUNC(PInvokeImportWorker)
        mov             r10, rax

        RESTORE_FLOAT_ARGUMENT_REGISTERS 0

        //
        // epilogue, r10 contains the native target address
        //
        free_stack      0x88

        //
        // Restore integer parameter registers, r11 and rax
        //
        pop_register rax
        pop_register r11
        POP_ARGUMENT_REGISTERS

        jmp r10

NESTED_END PInvokeImportThunk, _TEXT

//------------------------------------------------
// JIT_RareDisableHelper
//
// The JIT expects this helper to preserve registers used for return values
//
NESTED_ENTRY JIT_RareDisableHelper, _TEXT, NoHandler

    // First integer return register
    push_register rax
    // Second integer return register
    push_register rdx
    alloc_stack         0x28
    END_PROLOGUE
    // First float return register
    movdqa              xmmword ptr [rsp], xmm0
    // Second float return register
    movdqa              xmmword ptr [rsp+0x10], xmm1

    call                C_FUNC(JIT_RareDisableHelperWorker)

    movdqa              xmm0, xmmword ptr [rsp]
    movdqa              xmm1, xmmword ptr [rsp+0x10]
    free_stack          0x28
    pop_register        rdx
    pop_register        rax
    ret

NESTED_END JIT_RareDisableHelper, _TEXT

#ifdef FEATURE_HIJACK

//------------------------------------------------
// OnHijackTripThread
//
NESTED_ENTRY OnHijackTripThread, _TEXT, NoHandler

    // Make room for the real return address (rip)
    push_register rax

    PUSH_CALLEE_SAVED_REGISTERS

    // Push rcx for the async continuation
    push_register rcx
    // Push rdx for the second half of the return value
    push_register rdx
    // Push rax again - this is where integer/pointer return values are returned
    push_register rax

    mov                 rdi, rsp

    alloc_stack         0x20

    // First float return register
    movdqa              [rsp], xmm0
    // Second float return register
    movdqa              [rsp+0x10], xmm1

    END_PROLOGUE

    call                C_FUNC(OnHijackWorker)

    movdqa              xmm0, [rsp]
    movdqa              xmm1, [rsp+0x10]
    free_stack          0x20

    pop_register        rax
    pop_register        rdx
    pop_register        rcx

    POP_CALLEE_SAVED_REGISTERS
    ret

NESTED_END OnHijackTripThread, _TEXT

//------------------------------------------------
// GC poll helper (CORINFO_HELP_POLL_GC) for signalless mode. JIT-compiled code expects it to preserve all general
// purpose registers other than rax (RBM_SIGNALLESS_POLL_GC_TRASH in the JIT), and the GC info at the return address
// of the call describes the GC references in any of them.
LEAF_ENTRY JIT_PollGCPreservingRegs, _TEXT
        PREPARE_EXTERNAL_VAR g_TrapReturningThreads, rax
        cmp     dword ptr [rax], 0
        jnz     C_FUNC(JIT_PollGCPreservingRegsRare)
        ret
LEAF_END JIT_PollGCPreservingRegs, _TEXT

// The CONTEXT and the flags pushed above it.
POLL_GC_FRAME_SIZE = SIZEOF__CONTEXT + 8

// Captures the registers of the method that polled in a CONTEXT, as an interruption at the return address of the poll
// would, and lets JIT_PollGCPreservingRegsWorker stop the thread there. The GC may update references held in the
// registers through the CONTEXT, so they are reloaded from it. The thread then returns to the method that polled, or
// raises a pending abort at the poll.
NESTED_ENTRY JIT_PollGCPreservingRegsRare, _TEXT, NoHandler
        push_eflags
        // The CONTEXT must be 16-byte aligned. The return address and the flags leave rsp aligned.
        alloc_stack     SIZEOF__CONTEXT
        END_PROLOGUE

        mov     [rsp + OFFSETOF__CONTEXT__Rax], rax
        mov     [rsp + OFFSETOF__CONTEXT__Rcx], rcx
        mov     [rsp + OFFSETOF__CONTEXT__Rdx], rdx
        mov     [rsp + OFFSETOF__CONTEXT__Rbx], rbx
        mov     [rsp + OFFSETOF__CONTEXT__Rbp], rbp
        mov     [rsp + OFFSETOF__CONTEXT__Rsi], rsi
        mov     [rsp + OFFSETOF__CONTEXT__Rdi], rdi
        mov     [rsp + OFFSETOF__CONTEXT__R8], r8
        mov     [rsp + OFFSETOF__CONTEXT__R9], r9
        mov     [rsp + OFFSETOF__CONTEXT__R10], r10
        mov     [rsp + OFFSETOF__CONTEXT__R11], r11
        mov     [rsp + OFFSETOF__CONTEXT__R12], r12
        mov     [rsp + OFFSETOF__CONTEXT__R13], r13
        mov     [rsp + OFFSETOF__CONTEXT__R14], r14
        mov     [rsp + OFFSETOF__CONTEXT__R15], r15

        // Raising an abort with the CONTEXT needs the floating point registers, as resuming after a catch restores
        // the registers from it.
        fxsave  [rsp + OFFSETOF__CONTEXT__FltSave]
        stmxcsr dword ptr [rsp + OFFSETOF__CONTEXT__MxCsr]

        // The prolog pushed the flags above the frame, and the return address of the poll above them.
        mov     eax, [rsp + SIZEOF__CONTEXT]
        mov     [rsp + OFFSETOF__CONTEXT__EFlags], eax
        mov     rax, [rsp + SIZEOF__CONTEXT + 8]
        mov     [rsp + OFFSETOF__CONTEXT__Rip], rax
        lea     rax, [rsp + SIZEOF__CONTEXT + 16]
        mov     [rsp + OFFSETOF__CONTEXT__Rsp], rax
.att_syntax
        mov     %cs, OFFSETOF__CONTEXT__SegCs(%rsp)
        mov     %ss, OFFSETOF__CONTEXT__SegSs(%rsp)
.intel_syntax noprefix
        mov     dword ptr [rsp + OFFSETOF__CONTEXT__ContextFlags], CONTEXT_CONTROL_INTEGER_FLOATING_POINT

        mov     rdi, rsp
        call    C_FUNC(JIT_PollGCPreservingRegsWorker)

        // rax is trashed.
        mov     rcx, [rsp + OFFSETOF__CONTEXT__Rcx]
        mov     rdx, [rsp + OFFSETOF__CONTEXT__Rdx]
        mov     rbx, [rsp + OFFSETOF__CONTEXT__Rbx]
        mov     rbp, [rsp + OFFSETOF__CONTEXT__Rbp]
        mov     rsi, [rsp + OFFSETOF__CONTEXT__Rsi]
        mov     rdi, [rsp + OFFSETOF__CONTEXT__Rdi]
        mov     r8, [rsp + OFFSETOF__CONTEXT__R8]
        mov     r9, [rsp + OFFSETOF__CONTEXT__R9]
        mov     r10, [rsp + OFFSETOF__CONTEXT__R10]
        mov     r11, [rsp + OFFSETOF__CONTEXT__R11]
        mov     r12, [rsp + OFFSETOF__CONTEXT__R12]
        mov     r13, [rsp + OFFSETOF__CONTEXT__R13]
        mov     r14, [rsp + OFFSETOF__CONTEXT__R14]
        mov     r15, [rsp + OFFSETOF__CONTEXT__R15]

        test    eax, eax
        jnz     LOCAL_LABEL(JIT_PollGCPreservingRegsAbort)

        .cfi_remember_state
        free_stack      POLL_GC_FRAME_SIZE
        ret
        .cfi_restore_state

LOCAL_LABEL(JIT_PollGCPreservingRegsAbort):
        // Raise a pending abort at the return address of the poll. RedirectForThrowControl expects the stack of the
        // method that polled and the address to raise the abort at in rdi, as for a thread redirected there.
        free_stack      POLL_GC_FRAME_SIZE
        pop_register    rdi
        jmp     C_FUNC(RedirectForThrowControl)
NESTED_END JIT_PollGCPreservingRegsRare, _TEXT

#endif // FEATURE_HIJACK

#ifdef FEATURE_TIERED_COMPILATION

NESTED_ENTRY OnCallCountThresholdReachedStub, _TEXT, NoHandler
        PROLOG_WITH_TRANSITION_BLOCK

        lea     rdi, [rsp + __PWTB_TransitionBlock] // TransitionBlock *
        mov     rsi, rax // stub-identifying token, see OnCallCountThresholdReachedStub
        call    C_FUNC(OnCallCountThresholdReached)

        EPILOG_WITH_TRANSITION_BLOCK_TAILCALL
        TAILJMP_RAX
NESTED_END OnCallCountThresholdReachedStub, _TEXT

NESTED_ENTRY JIT_Patchpoint, _TEXT, NoHandler
        PROLOG_WITH_TRANSITION_BLOCK

        lea     rdi, [rsp + __PWTB_TransitionBlock] // TransitionBlock *
        call    C_FUNC(JIT_PatchpointWorkerWorkerWithPolicy)

        EPILOG_WITH_TRANSITION_BLOCK_RETURN
NESTED_END JIT_Patchpoint, _TEXT

// first arg register holds iloffset, which needs to be moved to the second register, and the first register filled with NULL
LEAF_ENTRY JIT_PatchpointForced, _TEXT
        mov rsi, rdi
        xor rdi, rdi
        jmp C_FUNC(JIT_Patchpoint)
LEAF_END JIT_PatchpointForced, _TEXT

#endif // FEATURE_TIERED_COMPILATION
