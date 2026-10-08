
import { type LyricLine, type LyricWord, newLyricWord } from "$/types/ttml";
import { CharType, type SegmentationConfig } from "../types";
import {
	escapeRegExp,
	getCharType,
	getMergeDirection,
	MergeDirection,
	PUNCT_AMBIGUOUS_QUOTES,
} from "./charUtils";

export interface SegmentationContext {
	isQuoteOpen: boolean;
}

function isMergeablePair(
	prev: CharType,
	curr: CharType,
	splitCJK: boolean,
): boolean {
	if (prev !== curr) {
		return false;
	}

	switch (prev) {
		case CharType.Latin:
		case CharType.Numeric:
		case CharType.Whitespace:
			return true;
		case CharType.Cjk:
			return !splitCJK;
		default:
			return false;
	}
}

function autoTokenize(text: string, config: SegmentationConfig): string[] {
	if (!text) {
		return [];
	}
	const tokens: string[] = [];
	let currentToken = "";
	let lastCharType: CharType | null = null;

	const pushCurrentToken = () => {
		if (!currentToken) return;

		if (
			lastCharType === CharType.Latin &&
			config.splitEnglish &&
			currentToken.length > 1 &&
			config.hyphenator
		) {
			const syllables = config.hyphenator(currentToken).split("\u00AD");
			tokens.push(...syllables);
		} else {
			tokens.push(currentToken);
		}
		currentToken = "";
	};

	const graphemes = Array.from(text);

	for (const grapheme of graphemes) {
		const firstChar = grapheme.length > 0 ? Array.from(grapheme)[0] : " ";
		const currentCharType = getCharType(firstChar);

		if (lastCharType !== null) {
			let shouldBreak = false;

			if (currentCharType === CharType.Cjk && config.splitCJK) {
				shouldBreak = true;
			} else if (lastCharType === CharType.Cjk && config.splitCJK) {
				shouldBreak = true;
			} else if (currentCharType === CharType.Other) {
				shouldBreak = true;
			} else if (lastCharType === CharType.Other) {
				shouldBreak = true;
			}

			if (
				!shouldBreak &&
				!isMergeablePair(lastCharType, currentCharType, config.splitCJK)
			) {
				shouldBreak = true;
			}

			if (shouldBreak && currentToken) {
				pushCurrentToken();
			}
		}

		currentToken += grapheme;
		lastCharType = currentCharType;
	}

	pushCurrentToken();

	return tokens;
}

function calculateWeight(token: string, config: SegmentationConfig): number {
	if (!token) {
		return 0;
	}
	const firstChar = token.length > 0 ? Array.from(token)[0] : " ";
	const charType = getCharType(firstChar);

	switch (charType) {
		case CharType.Latin:
		case CharType.Numeric:
		case CharType.Cjk:
			return token.length;
		case CharType.Other:
			return config.punctuationWeight;
		case CharType.Whitespace:
			return 0.0;
		default:
			return 0.0;
	}
}

function postProcess(
	tokens: string[],
	config: SegmentationConfig,
	context?: SegmentationContext,
): { finalTokens: string[]; finalWeights: number[]; totalWeight: number } {
	const processedTokens: string[] = [];
	const tokenWeights: number[] = [];

	let pendingPrefix = "";
	let pendingPrefixWeight = 0;

	for (const token of tokens) {
		const tokenWeight = calculateWeight(token, config);
		const firstChar = token.length > 0 ? Array.from(token)[0] : " ";
		const charType = getCharType(firstChar);

		if (config.punctuationMode === "merge" && charType === CharType.Other) {
			let direction = getMergeDirection(token);

			if (context && PUNCT_AMBIGUOUS_QUOTES.has(token)) {
				if (context.isQuoteOpen) {
					direction = MergeDirection.Left;
					context.isQuoteOpen = false;
				} else {
					direction = MergeDirection.Right;
					context.isQuoteOpen = true;
				}
			}

			if (direction === MergeDirection.Right) {
				pendingPrefix += token;
				pendingPrefixWeight += tokenWeight;
			} else {
				if (processedTokens.length > 0) {
					processedTokens[processedTokens.length - 1] += token;
					tokenWeights[tokenWeights.length - 1] += tokenWeight;
				} else {
					pendingPrefix += token;
					pendingPrefixWeight += tokenWeight;
				}
			}
		} else {
			const combinedToken = pendingPrefix + token;
			const combinedWeight = pendingPrefixWeight + tokenWeight;

			processedTokens.push(combinedToken);
			tokenWeights.push(combinedWeight);

			pendingPrefix = "";
			pendingPrefixWeight = 0;
		}
	}

	if (pendingPrefix) {
		if (processedTokens.length > 0) {
			processedTokens[processedTokens.length - 1] += pendingPrefix;
			tokenWeights[tokenWeights.length - 1] += pendingPrefixWeight;
		} else {
			processedTokens.push(pendingPrefix);
			tokenWeights.push(pendingPrefixWeight);
		}
	}

	const finalTokens: string[] = [];
	const finalWeights: number[] = [];
	let totalWeight = 0;

	for (let i = 0; i < processedTokens.length; i++) {
		const token = processedTokens[i];
		const weight = tokenWeights[i];

		if (token === "" && config.removeEmptySegments) {
			continue;
		}

		finalTokens.push(token);
		finalWeights.push(weight);
		totalWeight += weight;
	}

	return { finalTokens, finalWeights, totalWeight };
}

function distributeTime(
	originalWord: LyricWord,
	tokens: string[],
	weights: number[],
	totalWeight: number,
): LyricWord[] {
	if (tokens.length === 0) {
		return [];
	}
	if (tokens.length === 1) {
		return [{ ...originalWord, word: tokens[0] }];
	}

	const totalDuration = originalWord.endTime - originalWord.startTime;
	if (totalDuration <= 0) {
		return tokens.map((token) => ({
			...newLyricWord(),
			word: token,
			startTime: originalWord.startTime,
			endTime: originalWord.endTime,
		}));
	}

	if (totalWeight <= 0) {
		return [];
	}

	const durationPerWeight = totalDuration / totalWeight;
	const newWords: LyricWord[] = [];
	let currentTokenStartMs = originalWord.startTime;

	for (let i = 0; i < tokens.length; i++) {
		const token = tokens[i];
		const weight = weights[i];

		const tokenDuration = Math.round(weight * durationPerWeight);
		let tokenEndMs = currentTokenStartMs + tokenDuration;

		if (i === tokens.length - 1) {
			tokenEndMs = originalWord.endTime;
		}

		newWords.push({
			...newLyricWord(),
			word: token,
			startTime: currentTokenStartMs,
			endTime: tokenEndMs,
		});

		if (tokenEndMs > currentTokenStartMs) {
			currentTokenStartMs = tokenEndMs;
		}
	}

	return newWords;
}

export function segmentWord(
	word: LyricWord,
	config: SegmentationConfig,
	context?: SegmentationContext,
): LyricWord[] {
	const protectedWords = new Set([
		...config.ignoreList,
		...config.customRules.keys(),
	]);
	const ctx = context || { isQuoteOpen: false };

	if (protectedWords.size === 0) {
		const tokens = autoTokenize(word.word, config);
		if (tokens.length === 0) return [];
		const { finalTokens, finalWeights, totalWeight } = postProcess(
			tokens,
			config,
			ctx,
		);
		return distributeTime(word, finalTokens, finalWeights, totalWeight);
	}

	const sortedPatterns = Array.from(protectedWords)
		.filter((k) => k.trim() !== "")
		.sort((a, b) => b.length - a.length)
		.map(escapeRegExp);

	if (sortedPatterns.length === 0) {
		const tokens = autoTokenize(word.word, config);
		if (tokens.length === 0) return [];
		const { finalTokens, finalWeights, totalWeight } = postProcess(
			tokens,
			config,
			ctx,
		);
		return distributeTime(word, finalTokens, finalWeights, totalWeight);
	}

	const pattern = new RegExp(`(${sortedPatterns.join("|")})`, "g");

	const parts = word.word.split(pattern);

	const allTokens: string[] = [];
	const allWeights: number[] = [];
	let grandTotalWeight = 0;

	for (const part of parts) {
		if (!part) continue;

		if (config.customRules.has(part)) {
			const ruleParts = config.customRules.get(part);
			if (!ruleParts) continue;
			for (const token of ruleParts) {
				const weight = calculateWeight(token, config);
				allTokens.push(token);
				allWeights.push(weight);
				grandTotalWeight += weight;
			}
		} else if (config.ignoreList.has(part)) {
			const weight = calculateWeight(part, config);
			allTokens.push(part);
			allWeights.push(weight);
			grandTotalWeight += weight;
		} else {
			const tokens = autoTokenize(part, config);
			const { finalTokens, finalWeights, totalWeight } = postProcess(
				tokens,
				config,
				ctx,
			);
			allTokens.push(...finalTokens);
			allWeights.push(...finalWeights);
			grandTotalWeight += totalWeight;
		}
	}

	return distributeTime(word, allTokens, allWeights, grandTotalWeight);
}

export function segmentLyricLines(
	lines: LyricLine[],
	config: SegmentationConfig,
	resetStatePerLine = true,
): LyricLine[] {
	const context: SegmentationContext = { isQuoteOpen: false };

	return lines.map((line) => {
		if (resetStatePerLine) {
			context.isQuoteOpen = false;
		}

		const newWords: LyricWord[] = [];
		for (const word of line.words) {
			const segments = segmentWord(word, config, context);
			newWords.push(...segments);
		}

		return {
			...line,
			words: newWords,
		};
	});
}

export function recalculateWordTime(
	originalWord: LyricWord,
	segments: string[],
	config: SegmentationConfig,
): LyricWord[] {
	const weights = segments.map((token) => calculateWeight(token, config));
	const totalWeight = weights.reduce((sum, w) => sum + w, 0);

	return distributeTime(originalWord, segments, weights, totalWeight);
}
