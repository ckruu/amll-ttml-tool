import { atom } from "jotai";
import { lyricLinesAtom } from "$/states/main";
import type { LyricLine } from "$/types/ttml";
import { getSynchronizableUnits } from "./lyric-states";

export interface LineSyncProgress {
	lineId: string;
	total: number;
	synced: number;
}

export interface SyncProgress {
	total: number;
	synced: number;
	percent: number;
	perLine: LineSyncProgress[];
}

export const isUnitSynced = (startTime: number, endTime: number): boolean =>
	startTime > 0 && endTime > 0 && endTime > startTime;

export function getSyncProgress(lines: LyricLine[]): SyncProgress {
	let total = 0;
	let synced = 0;
	const perLine: LineSyncProgress[] = [];

	for (const line of lines) {
		if (line.ignoreSync) continue;
		if (!line.words || line.words.length <= 1) continue;
		const units = getSynchronizableUnits(line);
		if (units.length === 0) continue;
		let lineSynced = 0;
		for (const unit of units) {
			const ts = unit.rubyWord ?? unit.word;
			if (isUnitSynced(ts.startTime, ts.endTime)) lineSynced++;
		}
		perLine.push({ lineId: line.id, total: units.length, synced: lineSynced });
		total += units.length;
		synced += lineSynced;
	}

	return {
		total,
		synced,
		percent: total === 0 ? 0 : Math.round((synced / total) * 100),
		perLine,
	};
}

export const syncProgressAtom = atom((get): SyncProgress =>
	getSyncProgress(get(lyricLinesAtom).lyricLines),
);
