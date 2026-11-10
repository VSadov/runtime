// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace System.Threading.ThreadPools.Tests
{
    // SpinLimitSwitch is compiled into this assembly from CoreLib's source, so its behavior is tested without a clock: every
    // update gets its time from the test, in ticks of any unit.
    public static class SpinLimitSwitchTests
    {
        private const long EngageTicks = 2_000;
        private const long ReleaseTicks = 1_000;
        private const long HoldOffTicks = 30_000;
        private const int One = SpinLimitSwitch.SlowWakeShareOne;

        private static SpinLimitSwitch CreateSwitch()
        {
            SpinLimitSwitch spinLimit = default;
            spinLimit.Initialize();
            return spinLimit;
        }

        private static void Record(ref SpinLimitSwitch spinLimit, long timestamp, bool slow) =>
            spinLimit.RecordLongParkWake(timestamp, slow, EngageTicks, ReleaseTicks, HoldOffTicks);

        // Fast wakes one tick apart after `from` until the share is at the engage level or below; returns the time of the last one,
        // which is when the count of the switch starts.
        private static long FeedFastUntilLow(ref SpinLimitSwitch spinLimit, long from)
        {
            long timestamp = from;
            while (spinLimit.SlowWakeShare > SpinLimitSwitch.SlowWakeShareEngage)
            {
                Record(ref spinLimit, ++timestamp, slow: false);
            }

            return timestamp;
        }

        // An engaged switch and the time of the wake that engaged it.
        private static SpinLimitSwitch CreateEngagedSwitch(out long timestamp)
        {
            SpinLimitSwitch spinLimit = CreateSwitch();
            long since = FeedFastUntilLow(ref spinLimit, 0);
            timestamp = since + EngageTicks;
            Record(ref spinLimit, timestamp, slow: false);
            Assert.True(spinLimit.IsEngaged);
            return spinLimit;
        }

        [Fact]
        public static void StartsReleasedWithTheShareAtOne()
        {
            SpinLimitSwitch spinLimit = CreateSwitch();
            Assert.False(spinLimit.IsEngaged);
            Assert.Equal(One, spinLimit.SlowWakeShare);
        }

        [Fact]
        public static void ShareMovesByA256thOfTheDifference()
        {
            SpinLimitSwitch spinLimit = CreateSwitch();
            Record(ref spinLimit, 1, slow: false);
            int expected = One - (One >> 8);
            Assert.Equal(expected, spinLimit.SlowWakeShare);

            Record(ref spinLimit, 2, slow: true);
            expected += (One - expected) >> 8;
            Assert.Equal(expected, spinLimit.SlowWakeShare);
        }

        [Fact]
        public static void EngagesWhenTheShareHasStayedLowForTheEngageTime()
        {
            SpinLimitSwitch spinLimit = CreateSwitch();
            long since = FeedFastUntilLow(ref spinLimit, 0);
            Assert.False(spinLimit.IsEngaged);

            Record(ref spinLimit, since + EngageTicks - 1, slow: false);
            Assert.False(spinLimit.IsEngaged);

            Record(ref spinLimit, since + EngageTicks, slow: false);
            Assert.True(spinLimit.IsEngaged);
        }

        [Fact]
        public static void AnUpdateAboveTheEngageLevelRestartsTheCount()
        {
            SpinLimitSwitch spinLimit = CreateSwitch();
            long timestamp = FeedFastUntilLow(ref spinLimit, 0);
            while (spinLimit.SlowWakeShare <= SpinLimitSwitch.SlowWakeShareEngage)
            {
                Record(ref spinLimit, ++timestamp, slow: true);
            }

            long restart = FeedFastUntilLow(ref spinLimit, timestamp);

            // Counted from the first time the share was low, the switch would engage here.
            Record(ref spinLimit, restart + EngageTicks - 1, slow: false);
            Assert.False(spinLimit.IsEngaged);

            Record(ref spinLimit, restart + EngageTicks, slow: false);
            Assert.True(spinLimit.IsEngaged);
        }

        // Slow wakes one tick apart after `from` until the share is above the release level; returns the time of the last one,
        // which is when the release count of an engaged switch starts.
        private static long FeedSlowUntilHigh(ref SpinLimitSwitch spinLimit, long from)
        {
            long timestamp = from;
            while (spinLimit.SlowWakeShare <= SpinLimitSwitch.SlowWakeShareRelease)
            {
                Record(ref spinLimit, ++timestamp, slow: true);
            }

            return timestamp;
        }

        [Fact]
        public static void ReleasesWhenTheShareHasStayedHighForTheReleaseTime()
        {
            SpinLimitSwitch spinLimit = CreateEngagedSwitch(out long timestamp);
            long since = FeedSlowUntilHigh(ref spinLimit, timestamp);
            Assert.True(spinLimit.IsEngaged);

            Record(ref spinLimit, since + ReleaseTicks - 1, slow: true);
            Assert.True(spinLimit.IsEngaged);

            Record(ref spinLimit, since + ReleaseTicks, slow: true);
            Assert.False(spinLimit.IsEngaged);
        }

        [Fact]
        public static void ABriefRiseAboveTheReleaseLevelDoesNotRelease()
        {
            SpinLimitSwitch spinLimit = CreateEngagedSwitch(out long timestamp);

            // A burst of slow wakes, as when work resumes after an idle period, lifts the share above the release level, and the
            // fast wakes that follow bring it back down before the release time; that restarts the count.
            long since = FeedSlowUntilHigh(ref spinLimit, timestamp);
            timestamp = since;
            while (spinLimit.SlowWakeShare > SpinLimitSwitch.SlowWakeShareRelease)
            {
                Record(ref spinLimit, ++timestamp, slow: false);
            }

            Assert.True(timestamp - since < ReleaseTicks);
            Assert.True(spinLimit.IsEngaged);

            // Counted from the first rise, the switch would release here.
            long restart = FeedSlowUntilHigh(ref spinLimit, since + ReleaseTicks);
            Assert.True(spinLimit.IsEngaged);

            Record(ref spinLimit, restart + ReleaseTicks - 1, slow: true);
            Assert.True(spinLimit.IsEngaged);

            Record(ref spinLimit, restart + ReleaseTicks, slow: true);
            Assert.False(spinLimit.IsEngaged);
        }

        [Fact]
        public static void DoesNotCountDuringTheHoldOffAfterARelease()
        {
            SpinLimitSwitch spinLimit = CreateEngagedSwitch(out long timestamp);
            while (spinLimit.IsEngaged)
            {
                Record(ref spinLimit, ++timestamp, slow: true);
            }

            long released = timestamp;

            // The share falls back below the engage level long before the hold-off ends, but nothing is counted until then.
            for (long t = released + 1; t < released + HoldOffTicks; t++)
            {
                Record(ref spinLimit, t, slow: false);
            }

            Assert.True(spinLimit.SlowWakeShare <= SpinLimitSwitch.SlowWakeShareEngage);
            Assert.False(spinLimit.IsEngaged);

            // The count starts at the end of the hold-off, and the switch engages after the engage time.
            long start = released + HoldOffTicks;
            Record(ref spinLimit, start, slow: false);
            Record(ref spinLimit, start + EngageTicks - 1, slow: false);
            Assert.False(spinLimit.IsEngaged);

            Record(ref spinLimit, start + EngageTicks, slow: false);
            Assert.True(spinLimit.IsEngaged);
        }

        [Fact]
        public static void StaysReleasedWhileTheShareStaysAboveTheEngageLevel()
        {
            SpinLimitSwitch spinLimit = CreateSwitch();

            // One wake in four is slow: the share settles near 0.25, above the engage level, and the switch never engages.
            for (long t = 1; t <= 100 * EngageTicks; t++)
            {
                Record(ref spinLimit, t, slow: t % 4 == 0);
                Assert.False(spinLimit.IsEngaged);
            }

            Assert.InRange(spinLimit.SlowWakeShare, SpinLimitSwitch.SlowWakeShareEngage + 1, One / 2);
        }
    }
}
