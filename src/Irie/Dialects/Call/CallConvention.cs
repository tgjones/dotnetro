namespace Irie.Dialects.Call;

// Identifies the calling convention of a `call.func` / `call.indirect`. It is the
// optional Immediate use right after the callee / target
// (`call.indirect %t, bbcmicro_os, %a, %x`); absent means the target's default
// (CC_MOS on MOS6502). The ids are global, each target supports a subset and
// rejects the others (Target.GetCallLowering).
public static class CallConvention
{
    public const int Default = 0;

    // The BBC Micro OS ROM entry points: byte arguments in A, X, Y.
    public const int BbcMicroOS = 1;

    public static bool TryGetName(long id, out string name)
    {
        name = id == BbcMicroOS ? "bbcmicro_os" : string.Empty;
        return name.Length > 0;
    }

    public static bool TryFromName(string name, out long id)
    {
        id = name == "bbcmicro_os" ? BbcMicroOS : Default;
        return id != Default;
    }
}
