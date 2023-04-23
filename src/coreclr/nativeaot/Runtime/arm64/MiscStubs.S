// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <unixasmmacros.inc>
#include "AsmOffsets.inc"

NESTED_ENTRY RhpGetInlinedThreadStaticBase, _TEXT, NoHandler
        // On exit:
        //   x0 - the thread static base for the given type

        // x1 = GetThread()
#ifdef FEATURE_EMULATED_TLS
        GETTHREAD_ETLS_1
#else
        INLINE_GETTHREAD x1
#endif

        // get per-thread storage
        ldr     x0, [x1, #OFFSETOF__Thread__m_pInlinedThreadLocalStatics]
        cmp     x0, 0
        bne     lr       // return if not null

        // tailcall the slow helper
        b       C_FUNC(RhpGetInlinedThreadStaticBaseSlow)
NESTED_END RhpGetInlinedThreadStaticBase, _TEXT
