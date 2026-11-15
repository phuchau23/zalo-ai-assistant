// Crash-restart schedule for a nick's worker (CONTRACT §4): 1, 5, 15, 30 minutes, then 30 minutes
// each time; the counter resets once the worker has been healthy (connected) for 10 minutes.

export const RESTART_DELAYS_MS: readonly number[] = [1, 5, 15, 30].map((m) => m * 60_000);
export const HEALTHY_RESET_MS = 10 * 60_000;

export class RestartBackoff {
  private failures = 0;

  /**
   * Delay before the next restart. `healthySince` = when the crashed run last became connected
   * (null if it never connected).
   */
  nextDelay(healthySince: number | null, now: number): number {
    if (healthySince !== null && now - healthySince >= HEALTHY_RESET_MS) this.failures = 0;
    const delay = RESTART_DELAYS_MS[Math.min(this.failures, RESTART_DELAYS_MS.length - 1)]!;
    this.failures++;
    return delay;
  }

  reset(): void {
    this.failures = 0;
  }

  get failureCount(): number {
    return this.failures;
  }
}
