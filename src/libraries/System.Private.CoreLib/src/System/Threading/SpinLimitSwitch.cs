// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading
{
    /// <summary>
    /// Decides from the wakes of workers after long parks whether the thread pool limits its spinners (see LowLevelLifoSemaphore).
    /// The caller passes the time of every update, so the switch reads no clock and behaves the same for the same sequence of wakes.
    /// </summary>
    internal struct SpinLimitSwitch
    {
        // The measure is the share of the long-park wakes that are slow, rather than a mean latency. A mean is dominated by the rare
        // very slow wakes, and clamping them to limit that brings the mean of a machine on which part of the wakes are slow close to
        // the mean of a machine on which all are fast. The share counts each wake once, so rare extreme wakes cannot move it.
        //
        // The share is a moving average in fixed point, SlowWakeShareOne is every long-park wake slow. A wake has the weight of
        // 1 / 2^SlowWakeShareShift, which makes the average follow the last few hundred wakes: a burst of slow wakes does not move it
        // much, and it still responds within a fraction of a second at the wake rate of a busy pool. It starts at one, so that the
        // spinners are not limited until there is evidence.
        internal const int SlowWakeShareOne = 1 << 16;
        private const int SlowWakeShareShift = 8;

        // The switch is one-sided. The limit saves CPU where the wakes are fast and costs throughput where they are slow, and over a
        // short time the shares of the two kinds of machine are too close to tell them apart. The limit is therefore engaged only on
        // sustained evidence: the share must stay at or below SlowWakeShareEngage for EngageSeconds, which is longer than the stretches
        // in which the share of a machine with slow wakes dips below that level, and any update above it restarts the count. The limit
        // is released at once when the share rises above SlowWakeShareRelease, because a limit that is wrong costs throughput while a
        // saving that is missed costs only CPU. The engage level is lower than the release level, so that the share has to be clearly
        // low to engage and clearly high to release. After a release the switch does not count for HoldOffSeconds. A machine on which
        // the limit does not hold then has the limit for the short time that the average takes to respond, once per hold-off, instead
        // of cycling between engaging and releasing.
        internal const int SlowWakeShareEngage = SlowWakeShareOne * 20 / 100;
        internal const int SlowWakeShareRelease = SlowWakeShareOne * 25 / 100;
        internal const int EngageSeconds = 2;
        internal const int HoldOffSeconds = 30;

        // The fields are written only when they change, without synchronization. A lost update only delays a switch, and a release
        // repeats on the next update above the release level.
        private int _slowWakeShare;
        private int _engaged;

        // The time the share fell to the engage level or below, zero while it is above.
        private long _belowSince;

        // The time until which the switch does not count after a release.
        private long _holdOffUntil;

        /// <summary>Puts the switch in its initial state: released, with the share at one.</summary>
        public void Initialize()
        {
            _slowWakeShare = SlowWakeShareOne;
            _engaged = 0;
            _belowSince = 0;
            _holdOffUntil = 0;
        }

        /// <summary>Whether the spinners are limited.</summary>
        public bool IsEngaged => Volatile.Read(ref _engaged) != 0;

        /// <summary>The share of slow long-park wakes, in fixed point (<see cref="SlowWakeShareOne"/> is every wake slow).</summary>
        public int SlowWakeShare => Volatile.Read(ref _slowWakeShare);

        /// <summary>Records a wake after a long park and updates the switch.</summary>
        /// <param name="timestamp">The time of the wake. It must not be zero.</param>
        /// <param name="slow">Whether the wake took the shortest spin or longer.</param>
        /// <param name="engageTicks">The engage time in the units of <paramref name="timestamp"/>.</param>
        /// <param name="holdOffTicks">The hold-off time in the units of <paramref name="timestamp"/>.</param>
        public void RecordLongParkWake(long timestamp, bool slow, long engageTicks, long holdOffTicks)
        {
            int share = Volatile.Read(ref _slowWakeShare);
            int updated = share + (((slow ? SlowWakeShareOne : 0) - share) >> SlowWakeShareShift);
            if (updated != share)
            {
                Volatile.Write(ref _slowWakeShare, updated);
            }

            if (Volatile.Read(ref _engaged) != 0)
            {
                if (updated > SlowWakeShareRelease)
                {
                    Volatile.Write(ref _engaged, 0);
                    Volatile.Write(ref _belowSince, 0);
                    Volatile.Write(ref _holdOffUntil, timestamp + holdOffTicks);
                }

                return;
            }

            if (timestamp < Volatile.Read(ref _holdOffUntil))
            {
                return;
            }

            if (updated > SlowWakeShareEngage)
            {
                if (Volatile.Read(ref _belowSince) != 0)
                {
                    Volatile.Write(ref _belowSince, 0);
                }

                return;
            }

            long since = Volatile.Read(ref _belowSince);
            if (since == 0)
            {
                Volatile.Write(ref _belowSince, timestamp);
            }
            else if (timestamp - since >= engageTicks)
            {
                Volatile.Write(ref _engaged, 1);
                Volatile.Write(ref _belowSince, 0);
            }
        }
    }
}
