using System;

namespace DotNetro.Runtime;

// The run-time half of isinst / castclass / unbox.any. An object's header (at obj-2)
// points at its type's vtable, whose first word is the vtable of the base type (0 at
// System.Object), so "is a T" is "T's vtable is on the object's chain". A string or
// array has a null header; a null `target` means System.String (arrays are not
// testable yet).
internal static class TypeCheck
{
    // The object if it is a `target`, else null.
    public static unsafe byte* IsInstance(byte* obj, void* target)
    {
        if (obj == null)
            return null;

        var vtable = *(void**)(obj - sizeof(IntPtr));
        if (target == null)
        {
            if (vtable == null)
                return obj;
            return null;
        }

        while (vtable != null)
        {
            if (vtable == target)
                return obj;
            vtable = *(void**)vtable;
        }
        return null;
    }

    // The object if it is null or a `target`; otherwise halts, there being no
    // exceptions (InvalidCastException) yet.
    public static unsafe byte* CastClass(byte* obj, void* target)
    {
        if (obj == null || IsInstance(obj, target) != null)
            return obj;

        while (true)
        {
        }
    }
}
