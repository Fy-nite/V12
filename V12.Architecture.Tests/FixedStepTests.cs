using System;
using System.Collections.Generic;
using V12.Components;
using V12.Core;
using V12.Core.Timing;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class FixedStepTests
{
    [Fact]
    public void Clock_RunsOneStepForOneFrameAtFixedRate()
    {
        var clock = new FixedStepClock(1f / 60f);
        int steps = 0;

        clock.Advance(1f / 60f, _ => steps++);

        Assert.Equal(1, steps);
        Assert.Equal(1, clock.Tick);
        Assert.Equal(0f, clock.Alpha, 3);
    }

    [Fact]
    public void Clock_AccumulatesVariableFramesIntoSeveralFixedSteps()
    {
        var clock = new FixedStepClock(1f / 60f);
        var deltas = new List<float>();

        clock.Advance(0.05f, dt => deltas.Add(dt));

        Assert.Equal(3, deltas.Count);
        Assert.All(deltas, dt => Assert.Equal(1f / 60f, dt, 6));
        Assert.Equal(3, clock.Tick);
    }

    [Fact]
    public void Clock_DropsBacklogAfterMaxSteps()
    {
        var clock = new FixedStepClock(1f / 60f) { MaxStepsPerFrame = 2, MaxFrameDeltaTime = 10f };
        int steps = 0;

        clock.Advance(0.5f, _ => steps++);

        Assert.Equal(2, steps);
        Assert.True(clock.Alpha < 1f);
    }

    [Fact]
    public void Clock_PauseAndTimeScaleAffectSimulationNotRealTime()
    {
        var clock = new FixedStepClock(1f / 60f);
        int steps = 0;

        clock.IsPaused = true;
        clock.Advance(0.1f, _ => steps++);
        Assert.Equal(0, steps);

        clock.IsPaused = false;
        clock.TimeScale = 2f;
        clock.Advance(0.05f, _ => steps++);

        Assert.Equal(6, steps);
        Assert.True(clock.RealTime > 0d);
    }

    [Fact]
    public void DeterministicRandom_SameSeedProducesSameSequence()
    {
        var a = new DeterministicRandom(12345);
        var b = new DeterministicRandom(12345);

        for (int i = 0; i < 64; i++)
            Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void DeterministicRandom_StateCanResumeExactly()
    {
        var random = new DeterministicRandom(7);
        for (int i = 0; i < 10; i++) random.NextULong();

        ulong state = random.State;
        ulong expected = random.NextULong();

        random.RestoreState(state);
        Assert.Equal(expected, random.NextULong());
    }

    [Fact]
    public void GameRoot_UpdateFeedsComponentsFixedDelta()
    {
        var root = new GameRoot();
        var world = new World("Fixed");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var element = new Element("Probe");
        var probe = new FixedDeltaProbe();
        element.AddComponent(probe);
        world.AddElement(element);

        root.Update(0.05f);

        Assert.Equal(3, probe.Count);
        Assert.All(probe.Deltas, dt => Assert.Equal(1f / 60f, dt, 6));
        Assert.Equal(3, root.SimulationTick);
    }

    private sealed class FixedDeltaProbe : ComponentBase
    {
        public override string Name => "FixedDeltaProbe";
        public int Count { get; private set; }
        public List<float> Deltas { get; } = new();

        public override void Update(float deltaTime)
        {
            Count++;
            Deltas.Add(deltaTime);
        }
    }
}
