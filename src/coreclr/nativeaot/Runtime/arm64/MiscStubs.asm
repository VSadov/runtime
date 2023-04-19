;; Licensed to the .NET Foundation under one or more agreements.
;; The .NET Foundation licenses this file to you under the MIT license.

#include "AsmMacros.h"

    TEXTAREA

NESTED_ENTRY RhpGetThreadStaticBaseForType, _TEXT, NoHandler
        ;; On entry:
        ;;   x0 - type index
        ;; On exit:
        ;;   x0 - the thread static base for the given type

        ;; x1 = GetThread(), TRASHES x2
        INLINE_GETTHREAD x1, x2

        ;; get per-thread storage
        ldr     x1, [x1, #OFFSETOF__Thread__m_pInlineThreadLocalStatics]
        cbz     x1, RhpGetInlinedThreadStaticBaseSlow

        ;; get the actual per-type storage
        add     x0, x0, #2
        ldr     x0, [x1, x0, lsl #3]  ;; x0 = *(x1 + x0 * 8)

        ;; return it
        ret
NESTED_END RhpGetThreadStaticBaseForType, _TEXT

    end
