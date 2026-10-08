
import { uid } from "uid";
import { type LyricLine, type LyricWord, newLyricWord } from "$/types/ttml";

export const SyllableSmoothThreshold = {
	LOW: 5,
	MEDIUM: 15,
	HIGH: 30,
} as const;

export type SyllableSmoothThreshold =
	(typeof SyllableSmoothThreshold)[keyof typeof SyllableSmoothThreshold];

export interface SyllableSmoothingOptions {
	threshold?: SyllableSmoothThreshold | (number & {});
	mergeSyllables?: boolean;
}

export interface SyllableSmoothingOverrides {
	skipSmoothWordIds?: Set<string>;
	skipMergeWordIds?: Set<string>;
}

const CJK_FULL_REGEX =
	/^[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}\p{Script=Bopomofo}]+$/u;

function isSpaceSyllable(word: LyricWord): boolean {
	if (!word?.word) return false;
	return /^\s*$/.test(word.word);
}

function getDuration(word: LyricWord): number {
	return Math.max(0, (word.endTime ?? 0) - (word.startTime ?? 0));
}

function getCharCount(word: LyricWord): number {
	const clean = (word.word || "").replace(/\s+/g, "");
	return Math.max(1, Array.from(clean).length);
}

function getGapBetween(
	prev: LyricWord,
	next: LyricWord,
	spacers: LyricWord[],
): number {
	const covered = spacers.reduce((sum, w) => sum + getDuration(w), 0);
	return Math.max(0, (next.startTime ?? 0) - (prev.endTime ?? 0) - covered);
}

function calculateRateVariation(v1: number, v2: number): number {
	if (v1 + v2 === 0) {
		return 0;
	}
	return (Math.abs(v1 - v2) / (v1 + v2)) * 100;
}

function isAllCJK(text: string): boolean {
	if (!text || typeof text !== "string") return false;
	const nonSpace = text.replace(/\s+/g, "");
	return nonSpace.length > 0 && CJK_FULL_REGEX.test(nonSpace);
}

function isRubyEligibleForSmoothing(word: LyricWord): boolean {
	if (!word.ruby || word.ruby.length === 0) {
		return true;
	}
	return word.ruby.length === 1;
}

function calculateSyllableVariation(
	w1: LyricWord,
	w2: LyricWord,
	w1TrailingGap = 0,
): number {
	const r1 = (getDuration(w1) + w1TrailingGap) / getCharCount(w1);
	const r2 = getDuration(w2) / getCharCount(w2);
	return calculateRateVariation(r1, r2);
}

function calculateClusterVariation(
	cluster: LyricWord[],
	candidate: LyricWord,
): number {
	const contentWords = [...cluster, candidate].filter(
		(w) => !isSpaceSyllable(w),
	);
	const sungDuration = contentWords.reduce((sum, w) => sum + getDuration(w), 0);
	const span = Math.max(
		0,
		(contentWords[contentWords.length - 1].endTime ?? 0) -
			(contentWords[0].startTime ?? 0),
	);
	return calculateRateVariation(sungDuration, span);
}

function smoothClusterTimestamps(cluster: LyricWord[]): LyricWord[] {
	const contentWords = cluster.filter((w) => !isSpaceSyllable(w));
	if (contentWords.length <= 1) {
		return cluster;
	}

	const totalStart = contentWords[0].startTime ?? 0;
	const totalEnd = contentWords[contentWords.length - 1].endTime ?? 0;
	const span = Math.max(0, totalEnd - totalStart);

	const reservedTotal = cluster
		.filter(isSpaceSyllable)
		.reduce((sum, w) => sum + getDuration(w), 0);
	const distributable = Math.max(0, span - reservedTotal);

	const charCounts = contentWords.map(getCharCount);
	const totalWeight = charCounts.reduce((sum, c) => sum + c, 0);

	const result: LyricWord[] = [];
	let cursor = totalStart;
	let reserved = 0;
	let cumWeight = 0;
	let contentIndex = 0;

	for (const word of cluster) {
		if (isSpaceSyllable(word)) {
			const spacerDuration = getDuration(word);
			result.push({
				...word,
				startTime: cursor,
				endTime: cursor + spacerDuration,
			});
			reserved += spacerDuration;
			cursor += spacerDuration;
			continue;
		}

		cumWeight += charCounts[contentIndex];
		const isLast = contentIndex === contentWords.length - 1;
		const wordStart = cursor;
		const wordEnd = isLast
			? totalEnd
			: totalStart +
				reserved +
				(totalWeight > 0
					? Math.round((distributable * cumWeight) / totalWeight)
					: 0);
		cursor = wordEnd;
		contentIndex++;

		const originalRuby = word.ruby;
		const ruby =
			originalRuby && originalRuby.length === 1
				? [{ ...originalRuby[0], startTime: wordStart, endTime: wordEnd }]
				: originalRuby;

		result.push({
			...word,
			startTime: wordStart,
			endTime: wordEnd,
			...(ruby ? { ruby } : {}),
		});
	}

	return result;
}

export function mergeWordGroup(words: LyricWord[]): LyricWord {
	const cluster = words;
	const contentWords = cluster.filter((w) => !isSpaceSyllable(w));
	const first = contentWords[0] ?? cluster[0];
	const last =
		contentWords[contentWords.length - 1] ?? cluster[cluster.length - 1];

	const word = cluster.map((w) => w.word ?? "").join("");
	const startTime = first.startTime ?? 0;
	const endTime = last.endTime ?? 0;
	const obscene = cluster.some((w) => w.obscene);
	const emptyBeat = cluster.reduce((sum, w) => sum + (w.emptyBeat || 0), 0);

	const romanWord = cluster
		.map((w) => w.romanWord?.trim())
		.filter(Boolean)
		.join(" ");

	const rubies = cluster.flatMap((w) => w.ruby || []);

	return {
		...newLyricWord(),
		id: first.id || uid(),
		word,
		startTime,
		endTime,
		obscene,
		emptyBeat,
		romanWord,
		...(rubies.length > 0 ? { ruby: rubies } : {}),
	};
}

export function findSmoothClusters(
	line: LyricLine,
	threshold: number = SyllableSmoothThreshold.MEDIUM,
	overrides?: SyllableSmoothingOverrides,
): LyricWord[][] {
	if (!line.words || line.words.length <= 1) return [];
	const skipSmooth = overrides?.skipSmoothWordIds;
	const clusters: LyricWord[][] = [];
	let i = 0;

	while (i < line.words.length) {
		const current = line.words[i];
		if (isSpaceSyllable(current) || skipSmooth?.has(current.id)) {
			i++;
			continue;
		}
		const cluster: LyricWord[] = [current];
		let lastContentWord = current;

		while (i + 1 < line.words.length) {
			let nextIndex = i + 1;
			const pendingSpaces: LyricWord[] = [];
			while (
				nextIndex < line.words.length &&
				isSpaceSyllable(line.words[nextIndex])
			) {
				pendingSpaces.push(line.words[nextIndex]);
				nextIndex++;
			}
			if (nextIndex >= line.words.length) break;
			const nextWord = line.words[nextIndex];
			if (skipSmooth?.has(nextWord.id)) break;
			if (cluster.some((w) => skipSmooth?.has(w.id))) break;

			const isCurrentCJK = isAllCJK(lastContentWord.word);
			const isNextCJK = isAllCJK(nextWord.word);
			const isCurrentRubyEligible = isRubyEligibleForSmoothing(lastContentWord);
			const isNextRubyEligible = isRubyEligibleForSmoothing(nextWord);

			if (
				isCurrentCJK &&
				isNextCJK &&
				isCurrentRubyEligible &&
				isNextRubyEligible
			) {
				const gap = getGapBetween(lastContentWord, nextWord, pendingSpaces);
				const variation = calculateSyllableVariation(lastContentWord, nextWord);
				const gapVariation = calculateSyllableVariation(
					lastContentWord,
					nextWord,
					gap,
				);
				const clusterVariation = calculateClusterVariation(cluster, nextWord);
				if (
					variation < threshold &&
					gapVariation < threshold &&
					clusterVariation < threshold
				) {
					cluster.push(...pendingSpaces, nextWord);
					lastContentWord = nextWord;
					i = nextIndex;
					continue;
				}
			}
			break;
		}

		if (cluster.length > 1) clusters.push(cluster);
		i++;
	}
	return clusters;
}

export interface SmoothClusterPreview {
	words: LyricWord[];
	mergedText: string;
	spanStart: number;
	spanEnd: number;
	willSmooth: boolean;
}

export interface SmoothLinePreview {
	clusters: SmoothClusterPreview[];
	changed: boolean;
	smoothedText: string;
	mergedText: string;
}

export function previewSmoothLine(
	line: LyricLine,
	options?: SyllableSmoothingOptions,
): SmoothLinePreview {
	const threshold = options?.threshold ?? SyllableSmoothThreshold.MEDIUM;
	const clusters = findSmoothClusters(line, threshold).map((words) => {
		const smoothed = smoothClusterTimestamps(words);
		const merged = mergeWordGroup(smoothed);
		return {
			words,
			mergedText: merged.word,
			spanStart: words[0]?.startTime ?? 0,
			spanEnd: words[words.length - 1]?.endTime ?? 0,
			willSmooth: true,
		} as SmoothClusterPreview;
	});
	const smoothedLine = smoothSyllables(line, { threshold });
	const mergedLine = smoothSyllables(line, { threshold, mergeSyllables: true });
	return {
		clusters,
		changed: clusters.length > 0,
		smoothedText: smoothedLine.words.map((w) => w.word).join(""),
		mergedText: mergedLine.words.map((w) => w.word).join(""),
	};
}

export function smoothSyllables(
	line: LyricLine,
	options?: SyllableSmoothingOptions,
	overrides?: SyllableSmoothingOverrides,
): LyricLine {
	if (!line.words || line.words.length <= 1) {
		return line;
	}

	const threshold = options?.threshold ?? SyllableSmoothThreshold.MEDIUM;
	const shouldMerge = options?.mergeSyllables ?? false;
	const skipSmooth = overrides?.skipSmoothWordIds;
	const skipMerge = overrides?.skipMergeWordIds;

	const resultWords: LyricWord[] = [];
	let i = 0;

	while (i < line.words.length) {
		const current = line.words[i];

		if (isSpaceSyllable(current)) {
			resultWords.push(current);
			i++;
			continue;
		}

		if (skipSmooth?.has(current.id)) {
			resultWords.push(current);
			i++;
			continue;
		}

		const cluster: LyricWord[] = [current];
		let lastContentWord = current;

		while (i + 1 < line.words.length) {
			let nextIndex = i + 1;
			const pendingSpaces: LyricWord[] = [];

			while (
				nextIndex < line.words.length &&
				isSpaceSyllable(line.words[nextIndex])
			) {
				pendingSpaces.push(line.words[nextIndex]);
				nextIndex++;
			}

			if (nextIndex >= line.words.length) {
				break;
			}

			const nextWord = line.words[nextIndex];
			if (skipSmooth?.has(nextWord.id)) break;

			const isCurrentCJK = isAllCJK(lastContentWord.word);
			const isNextCJK = isAllCJK(nextWord.word);
			const isCurrentRubyEligible = isRubyEligibleForSmoothing(lastContentWord);
			const isNextRubyEligible = isRubyEligibleForSmoothing(nextWord);

			if (
				isCurrentCJK &&
				isNextCJK &&
				isCurrentRubyEligible &&
				isNextRubyEligible
			) {
				const gap = getGapBetween(lastContentWord, nextWord, pendingSpaces);

				const variation = calculateSyllableVariation(lastContentWord, nextWord);
				const gapVariation = calculateSyllableVariation(
					lastContentWord,
					nextWord,
					gap,
				);
				const clusterVariation = calculateClusterVariation(cluster, nextWord);

				if (
					variation < threshold &&
					gapVariation < threshold &&
					clusterVariation < threshold
				) {
					cluster.push(...pendingSpaces, nextWord);
					lastContentWord = nextWord;
					i = nextIndex;
					continue;
				}
			}

			break;
		}

		if (cluster.length === 1) {
			resultWords.push(cluster[0]);
		} else {
			if (cluster.some((w) => skipSmooth?.has(w.id))) {
				resultWords.push(...cluster);
			} else {
				const smoothed = smoothClusterTimestamps(cluster);
				const skipMergeHere =
					shouldMerge &&
					(skipMerge
						? cluster.some((w) => skipMerge.has(w.id))
						: false);

				if (shouldMerge && !skipMergeHere) {
					resultWords.push(mergeWordGroup(smoothed));
				} else {
					resultWords.push(...smoothed);
				}
			}
		}

		i++;
	}

	return {
		...line,
		words: resultWords,
	};
}

export interface MergeGapModel {
	contentIndices: number[];
	autoJoined: boolean[];
}

export function buildMergeGapModel(
	line: LyricLine,
	threshold: number = SyllableSmoothThreshold.MEDIUM,
): MergeGapModel {
	const contentIndices: number[] = [];
	line.words.forEach((w, i) => {
		if (!isSpaceSyllable(w)) contentIndices.push(i);
	});
	const autoJoined: boolean[] = new Array(
		Math.max(0, contentIndices.length - 1),
	).fill(false);
	if (contentIndices.length > 1) {
		const posOfWordIndex = new Map<number, number>();
		for (let p = 0; p < contentIndices.length; p++) {
			posOfWordIndex.set(contentIndices[p], p);
		}
		for (const cluster of findSmoothClusters(line, threshold)) {
			const members = cluster
				.map((w) => line.words.indexOf(w))
				.filter((wi) => wi >= 0)
				.map((wi) => posOfWordIndex.get(wi))
				.filter((p): p is number => p !== undefined)
				.sort((a, b) => a - b);
			for (let k = 0; k + 1 < members.length; k++) {
				if (members[k + 1] === members[k] + 1) {
					autoJoined[members[k]] = true;
				}
			}
		}
	}
	return { contentIndices, autoJoined };
}

export function computeMergeGroups(
	contentCount: number,
	isJoined: (gap: number) => boolean,
): number[][] {
	if (contentCount <= 0) return [];
	const groups: number[][] = [];
	let run = [0];
	for (let g = 0; g < contentCount - 1; g++) {
		if (isJoined(g)) {
			run.push(g + 1);
		} else {
			groups.push(run);
			run = [g + 1];
		}
	}
	groups.push(run);
	return groups;
}

export function applyMergeGroups(
	line: LyricLine,
	contentIndices: number[],
	groups: number[][],
): LyricLine {
	const words = line.words;
	if (contentIndices.length === 0 || groups.length === 0) return line;
	const groupOf = new Map<number, number[]>();
	for (const group of groups) {
		if (group.length === 0) continue;
		for (const p of group) {
			if (!groupOf.has(p)) groupOf.set(p, group);
		}
	}
	const result: LyricWord[] = [];
	let i = 0;
	let p = 0;
	while (p < contentIndices.length) {
		const group = groupOf.get(p) ?? [p];
		const first = contentIndices[group[0]];
		const last = contentIndices[group[group.length - 1]];
		while (i < first) {
			result.push(words[i]);
			i++;
		}
		const slice = words.slice(first, last + 1);
		result.push(group.length > 1 ? mergeWordGroup(slice) : slice[0]);
		i = last + 1;
		p = group[group.length - 1] + 1;
	}
	while (i < words.length) {
		result.push(words[i]);
		i++;
	}
	return { ...line, words: result };
}
