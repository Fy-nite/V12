using System;
using V12.Core;

namespace SpaceFlight.Components;

/// <summary>
/// Visual controls must not spin faster when the simulation clock is warped.
/// The world updates once per fixed step, so a component that is called several
/// times in one rendered frame needs to consume the real frame delta only once.
/// </summary>
public static class RealTimeInput
{
    public static float Delta(ref double lastRealTime)
    {
        var clock = GameRoot.Instance?.Simulation;
        if (clock == null)
            return 0f;

        return Delta(ref lastRealTime, clock.RealTime);
    }

    /// <summary>Frame-time delta for the real clock. Repeated calls with the
    /// same <paramref name="now"/> return zero, so several fixed simulation
    /// steps inside one rendered frame only apply visual input once.</summary>
    public static float Delta(ref double lastRealTime, double now)
    {
        if (lastRealTime < 0)
        {
            lastRealTime = now;
            return 0f;
        }

        if (now <= lastRealTime)
            return 0f;

        float delta = (float)(now - lastRealTime);
        lastRealTime = now;
        return Math.Clamp(delta, 0f, 0.1f);
    }
}
