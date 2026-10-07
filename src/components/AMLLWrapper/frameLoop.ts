/**
 * Frame-loop helpers for the vsync FPS cap.
 *
 * Pure functions (unit-tested); the rAF wiring lives in AMLLWrapper.
 */

/** Clamp a raw refresh estimate to a sane display range. */
export const clampRefreshHz = (hz: number): number =>
	Math.min(360, Math.max(30, Math.round(hz)));

/**
 * Estimate display refresh rate from rAF timestamp deltas (ms).
 * Returns null when there are too few samples.
 */
export function estimateRefreshHz(deltasMs: number[]): number | null {
	const valid = deltasMs.filter((d) => d > 0 && Number.isFinite(d));
	if (valid.length < 10) return null;
	const sorted = [...valid].sort((a, b) => a - b);
	const median = sorted[Math.floor(sorted.length / 2)];
	if (!(median > 0)) return null;
	return clampRefreshHz(1000 / median);
}

export interface CappedTick {
	/** Whether player.update() should run this tick */
	fire: boolean;
	/** Value to store as `last` for the next tick (carries remainder) */
	nextLast: number;
	/** Delta to pass to update() when firing */
	delta: number;
}

/**
 * Decide whether a capped frame should fire, carrying the sub-interval
 * remainder forward so the long-term rate matches the cap without drift.
 */
export function cappedTick(
	last: number,
	now: number,
	intervalMs: number,
): CappedTick {
	const elapsed = now - last;
	if (elapsed < intervalMs) {
		return { fire: false, nextLast: last, delta: 0 };
	}
	return {
		fire: true,
		nextLast: now - (elapsed % intervalMs),
		delta: elapsed,
	};
}
