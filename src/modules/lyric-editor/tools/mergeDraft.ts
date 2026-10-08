import {
	applyMergeGroups,
	buildMergeGapModel,
	computeMergeGroups,
	findSmoothClusters,
	smoothSyllables,
} from "$/modules/segmentation/utils/syllable-smoothing.ts";
import type { LyricLine, LyricWord } from "$/types/ttml";

export interface MergeEditorDraft {
	threshold: number;
	userJoins: Set<string>;
	userCuts: Set<string>;
	lineMergeOff: Set<number>;
	smoothOffKeys: Set<string>;
}

export const emptyMergeDraft = (threshold: number): MergeEditorDraft => ({
	threshold,
	userJoins: new Set(),
	userCuts: new Set(),
	lineMergeOff: new Set(),
	smoothOffKeys: new Set(),
});

export const gapKey = (lineIndex: number, gap: number) =>
	`${lineIndex}:${gap}`;
export const clusterKey = (lineIndex: number, clusterIndex: number) =>
	`${lineIndex}:${clusterIndex}`;

export interface LineMergeModel {
	lineIndex: number;
	line: LyricLine;
	contentIndices: number[];
	joined: boolean[];
	manual: boolean[];
	groups: number[][];
	groupOfPos: number[];
	clusters: LyricWord[][];
	smoothed: LyricLine;
	merged: LyricLine;
	changed: boolean;
}

export function buildLineMergeModel(
	line: LyricLine,
	lineIndex: number,
	draft: Pick<
		MergeEditorDraft,
		"threshold" | "userJoins" | "userCuts" | "lineMergeOff" | "smoothOffKeys"
	>,
): LineMergeModel {
	const { contentIndices, autoJoined } = buildMergeGapModel(
		line,
		draft.threshold,
	);
	const mergeOn = !draft.lineMergeOff.has(lineIndex);
	const joined = autoJoined.map(
		(auto, gap) =>
			mergeOn &&
			((auto && !draft.userCuts.has(gapKey(lineIndex, gap))) ||
				draft.userJoins.has(gapKey(lineIndex, gap))),
	);
	const manual = autoJoined.map(
		(auto, gap) =>
			mergeOn &&
			((auto && draft.userCuts.has(gapKey(lineIndex, gap))) ||
				(!auto && draft.userJoins.has(gapKey(lineIndex, gap)))),
	);
	const groups = computeMergeGroups(contentIndices.length, (g) =>
		Boolean(joined[g]),
	);
	const groupOfPos = new Array<number>(contentIndices.length).fill(-1);
	groups.forEach((group, gi) => {
		for (const p of group) groupOfPos[p] = gi;
	});

	const skipSmoothWordIds = new Set<string>();
	const clusters = findSmoothClusters(line, draft.threshold);
	clusters.forEach((cluster, ci) => {
		if (draft.smoothOffKeys.has(clusterKey(lineIndex, ci))) {
			for (const w of cluster) skipSmoothWordIds.add(w.id);
		}
	});
	const smoothed = smoothSyllables(
		line,
		{ threshold: draft.threshold, mergeSyllables: false },
		skipSmoothWordIds.size > 0 ? { skipSmoothWordIds } : undefined,
	);
	const merged = applyMergeGroups(smoothed, contentIndices, groups);
	const changed =
		groups.some((g) => g.length > 1) ||
		smoothed.words.some(
			(w, i) =>
				w.startTime !== line.words[i]?.startTime ||
				w.endTime !== line.words[i]?.endTime,
		);
	return {
		lineIndex,
		line,
		contentIndices,
		joined,
		manual,
		groups,
		groupOfPos,
		clusters,
		smoothed,
		merged,
		changed,
	};
}

export function applyDraftToLine(
	line: LyricLine,
	lineIndex: number,
	draft: MergeEditorDraft,
): LyricLine {
	return buildLineMergeModel(line, lineIndex, draft).merged;
}
