using System;
using System.Runtime.InteropServices;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// Keeps the display awake while video plays, through an IOKit power assertion. The user may still
/// blank the screen deliberately; only the idle timer is held off.
/// </summary>
internal static class DisplaySleepAssertion
{
    private const string IOKit =
        "/System/Library/Frameworks/IOKit.framework/IOKit";

    private const uint AssertionLevelOn = 255;
    private const int Success = 0;

    private static uint assertionId;

    public static void SetKeepScreenOn(bool value)
    {
        if (value)
        {
            Acquire();
        }
        else
        {
            Release();
        }
    }

    private static void Acquire()
    {
        if (assertionId != 0)
        {
            return;
        }

        var type = ObjC.CreateString("PreventUserIdleDisplaySleep");
        var name = ObjC.CreateString("Playing video");
        try
        {
            if (IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out uint id) != Success)
            {
                return;
            }

            assertionId = id;
        }
        finally
        {
            ObjC.CFRelease(type);
            ObjC.CFRelease(name);
        }
    }

    private static void Release()
    {
        if (assertionId == 0)
        {
            return;
        }

        IOPMAssertionRelease(assertionId);
        assertionId = 0;
    }

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(
        IntPtr assertionType,
        uint assertionLevel,
        IntPtr assertionName,
        out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);
}
