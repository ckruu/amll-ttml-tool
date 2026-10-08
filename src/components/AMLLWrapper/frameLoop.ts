
export const clampRefreshHz = (hz: number): number =>
	Math.min(360, Math.max(30, Math.round(hz)));

export function estimateRefreshHz(deltasMs: number[]): number | null {
	const valid = deltasMs.filter((d) => d > 0 && Number.isFinite(d));
	if (valid.length < 10) return null;
	const sorted = [...valid].sort((a, b) => a - b);
	const median = sorted[Math.floor(sorted.length / 2)];
	if (!(median > 0)) return null;
	return clampRefreshHz(1000 / median);
}

export interface CappedTick {
	fire: boolean;
	nextLast: number;
	delta: number;
}

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
